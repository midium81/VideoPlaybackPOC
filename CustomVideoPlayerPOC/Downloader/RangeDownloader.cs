using CustomVideoPlayerPOC.Core;
using Newtonsoft.Json;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace CustomVideoPlayerPOC.Downloader
{
    public class RangeDownloader : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _url;
        private readonly string _localPath;
        private readonly string _metaPath;
        private readonly RangeSet _downloadedRanges = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private DownloadMetadata _meta = new();
        private readonly int _chunkSize;
        private readonly int _maxRetries = 5;

        // Prefetch tuning.
        private const long PrefetchWindowBytes = 10L * 1024 * 1024;
        private const int MaxParallelChunks = 4;
        private const int MaxChunksPerIteration = 16;

        private int _completedFlag;
        private DateTimeOffset _lastMetaSave = DateTimeOffset.MinValue;

        public RangeSet DownloadedRanges => _downloadedRanges;
        public DownloadMetadata Metadata => _meta;

        /// <summary>Total size in bytes, or -1 when the server did not report a Content-Length.</summary>
        public long TotalBytes => _meta.ContentLength;

        public long DownloadedBytes => _downloadedRanges.DownloadedBytes;

        /// <summary>True once the whole file is present on disk.</summary>
        public bool IsComplete => Volatile.Read(ref _completedFlag) == 1;

        public DownloadProgress CurrentProgress => new(DownloadedBytes, TotalBytes, IsComplete);

        /// <summary>Raised on a background thread whenever bytes land on disk.</summary>
        public event EventHandler<DownloadProgress>? ProgressChanged;

        /// <summary>Raised exactly once, on a background thread, when the file is fully downloaded.</summary>
        public event EventHandler<DownloadProgress>? Completed;

        public RangeDownloader(string url, string localPath, int chunkSize = 256 * 1024)
        {
            _url = url;
            _localPath = localPath;
            _metaPath = localPath + ".meta.json";
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _chunkSize = chunkSize;
        }

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            ct = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token).Token;
            var head = new HttpRequestMessage(HttpMethod.Head, _url);
            var resp = await _http.SendAsync(head, ct);
            resp.EnsureSuccessStatusCode();
            var cl = resp.Content.Headers.ContentLength ?? -1;
            var etag = resp.Headers.ETag?.Tag;
            var lm = resp.Content.Headers.LastModified ?? DateTimeOffset.MinValue;

            if (File.Exists(_metaPath))
            {
                var json = await File.ReadAllTextAsync(_metaPath, ct);
                var existing = JsonConvert.DeserializeObject<DownloadMetadata>(json);
                if (existing != null && existing.Url == _url && existing.ContentLength == cl && existing.ETag == etag)
                {
                    _meta = existing;
                    foreach (var r in _meta.Ranges) _downloadedRanges.Add(new ByteRange(r.start, r.end));

                    // A resumed download may already be finished.
                    ReportProgress();
                    return;
                }
            }

            _meta = new DownloadMetadata
            {
                Url = _url,
                ContentLength = cl,
                ETag = etag,
                LastModified = lm,
                DownloadedAt = DateTimeOffset.UtcNow
            };
            await SaveMetaAsync(ct);
            if (cl > 0)
            {
                Helpers.EnsureDirectoryForFile(_localPath);
                using var fs = new FileStream(_localPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                if (fs.Length < cl) fs.SetLength(cl);
            }

            ReportProgress();
        }

        /// <summary>
        /// Publishes the current progress and raises <see cref="Completed"/> once, the first time
        /// the file becomes fully available.
        /// </summary>
        private void ReportProgress()
        {
            var wasComplete = IsComplete;
            var nowComplete = wasComplete || _downloadedRanges.IsComplete(_meta.ContentLength);

            var justCompleted = nowComplete && Interlocked.CompareExchange(ref _completedFlag, 1, 0) == 0;

            var progress = new DownloadProgress(DownloadedBytes, TotalBytes, nowComplete);
            ProgressChanged?.Invoke(this, progress);

            if (justCompleted)
                Completed?.Invoke(this, progress);
        }

        private async Task SaveMetaAsync(CancellationToken ct = default)
        {
            var json = JsonConvert.SerializeObject(_meta);
            await File.WriteAllTextAsync(_metaPath, json, ct);
        }

        public async Task DownloadRangeAsync(long start, long end, CancellationToken ct = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            ct = linked.Token;

            int attempt = 0;
            while (attempt < _maxRetries)
            {
                attempt++;
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, _url);
                    req.Headers.Range = new RangeHeaderValue(start, end);
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                    long written;
                    try
                    {
                        using var fs = new FileStream(_localPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                        fs.Seek(start, SeekOrigin.Begin);

                        // Count what actually arrived rather than assuming the server honoured the
                        // full range - otherwise the range set claims bytes we never wrote.
                        var buffer = new byte[81920];
                        written = 0;
                        int read;
                        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            written += read;
                        }

                        if (written > 0)
                        {
                            _downloadedRanges.Add(new ByteRange(start, start + written - 1));
                            _meta.Ranges = _downloadedRanges.AllRanges.Select(r => (r.Start, r.End)).ToList();
                            _meta.DownloadedAt = DateTimeOffset.UtcNow;

                            // Persisting after every 256 KB chunk is far too chatty; throttle it.
                            var complete = _downloadedRanges.IsComplete(_meta.ContentLength);
                            if (complete || DateTimeOffset.UtcNow - _lastMetaSave > TimeSpan.FromSeconds(2))
                            {
                                _lastMetaSave = DateTimeOffset.UtcNow;
                                await SaveMetaAsync(ct).ConfigureAwait(false);
                            }
                        }
                    }
                    finally { _writeLock.Release(); }

                    if (written > 0)
                        ReportProgress();

                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    if (attempt >= _maxRetries) throw;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Continuously fills gaps until the whole file is on disk, then returns.
        /// Bytes just ahead of the current playback position are fetched first; once that window is
        /// satisfied the loop falls back to the first gap anywhere in the file, which is what allows
        /// the download to actually reach 100%.
        /// </summary>
        public async Task StartPrefetchLoopAsync(Func<long> getPlaybackOffsetBytes, Action<ByteRange> onRangeDownloaded, CancellationToken ct = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            ct = linked.Token;

            var total = _meta.ContentLength;
            if (total <= 0) return;

            if (_downloadedRanges.IsComplete(total))
            {
                ReportProgress();
                return;
            }

            using var gate = new SemaphoreSlim(MaxParallelChunks, MaxParallelChunks);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var chunks = GetNextChunks(getPlaybackOffsetBytes, total);
                    if (chunks.Count == 0)
                    {
                        ReportProgress();
                        return; // nothing missing - download finished
                    }

                    var tasks = new List<Task>(chunks.Count);
                    foreach (var chunk in chunks)
                    {
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        tasks.Add(DownloadChunkAsync(chunk, onRangeDownloaded, gate, ct));
                    }

                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    // Transient failure: back off, then re-evaluate the gaps.
                    try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                }
            }
        }

        private async Task DownloadChunkAsync(ByteRange chunk, Action<ByteRange>? onRangeDownloaded, SemaphoreSlim gate, CancellationToken ct)
        {
            try
            {
                await DownloadRangeAsync(chunk.Start, chunk.End, ct).ConfigureAwait(false);
                onRangeDownloaded?.Invoke(chunk);
            }
            catch (OperationCanceledException) { }
            catch { /* retried on the next loop iteration */ }
            finally { gate.Release(); }
        }

        /// <summary>
        /// Picks the next batch of missing chunks: the prefetch window ahead of playback first,
        /// otherwise the earliest gap remaining anywhere in the file.
        /// </summary>
        private List<ByteRange> GetNextChunks(Func<long> getPlaybackOffsetBytes, long total)
        {
            long pos = 0;
            try { pos = getPlaybackOffsetBytes(); } catch { /* treat a faulty probe as offset 0 */ }
            pos = Math.Clamp(pos, 0, Math.Max(0, total - 1));

            var windowLength = Math.Min(PrefetchWindowBytes, total - pos);
            var missing = _downloadedRanges.GetMissingRanges(pos, windowLength).ToList();

            if (missing.Count == 0)
            {
                var gap = _downloadedRanges.FirstMissing(total);
                if (gap == null) return [];
                missing.Add(gap.Value);
            }

            var chunks = new List<ByteRange>(MaxChunksPerIteration);
            foreach (var gap in missing)
            {
                for (var s = gap.Start; s <= gap.End; s += _chunkSize)
                {
                    chunks.Add(new ByteRange(s, Math.Min(s + _chunkSize - 1, gap.End)));
                    if (chunks.Count >= MaxChunksPerIteration) return chunks;
                }
            }

            return chunks;
        }

        // Request a specific high-priority range (used for seeks and moov-tail fetch)
        public async Task RequestPriorityRangeAsync(long start, long end, CancellationToken ct = default)
        {
            await DownloadRangeAsync(start, end, ct);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _http.Dispose();
        }
    }
}