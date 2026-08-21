using System.IO;
using System.Net.Http;

namespace FlyLeafWithDownload.Download
{
    public class HlsDownloader : IDownloader
    {
        private readonly Cache.VideoCache _cache;
        private readonly HttpClient _http;
        public event EventHandler<double>? ProgressChanged;
        public event EventHandler<string>? Completed;
        public event EventHandler<Exception?>? Failed;
        private CancellationTokenSource? _cts;

        public HlsDownloader(Cache.VideoCache cache)
        {
            _cache = cache;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public async Task StartAsync(string url)
        {
            try
            {
                // Step 1: Get the list of individual segment URLs (.ts files)
                _cts = new CancellationTokenSource();
                List<string> segmentUrls = await GetSegmentUrlsAsync(url);

                var finalPath = _cache.GetCachedFilePath(url);
                var partialPath = _cache.GetPartialFilePath(url);

                if (segmentUrls.Count == 0)
                    throw new InvalidOperationException("No video segments found in the playlist.");

                // Step 2: Open a stream to append all segment bytes into one local file
                // TODO: check if it is possible to do partial? does it make sense since anyway we need to download chunks and then compose? I smell no
                using var outputStream = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None);

                // TODO: Improve this, now this is sequential but we can in theory download segments in parallel and then write them in order. But for now, let's keep it simple.
                // TODO: There should be also ideally a way to resume the download if it was interrupted, but that would require more complex state management (e.g., tracking which segments have been downloaded).
                // TODO: Maybe to achieve the above we can use the downloader library we already have, but for now let's keep it simple and sequential.
                for (int i = 0; i < segmentUrls.Count; i++)
                {
                    byte[] segmentBytes = await _http.GetByteArrayAsync(segmentUrls[i], _cts.Token);
                    await outputStream.WriteAsync(segmentBytes, 0, segmentBytes.Length);

                    // Report progress (0.0 to 1.0)
                    ProgressChanged?.Invoke(this, (double)(i + 1) / segmentUrls.Count);
                }

                // Step 3: Notify completion
                Completed?.Invoke(this, finalPath);
            }
            catch (Exception ex)
            {
                // Handle exceptions (e.g., log them, rethrow, etc.)
                Failed?.Invoke(this, ex);
            }
        }

        private async Task<List<string>> GetSegmentUrlsAsync(string m3u8Url)
        {
            string text = await _http.GetStringAsync(m3u8Url, _cts.Token);

            // Filter out HLS metadata lines starting with '#'
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(l => l.Trim())
                            .Where(l => !l.StartsWith('#'))
                            .ToList();

            if (lines.Count == 0)
                return new List<string>();

            // Check if the first entry is another .m3u8 playlist (Master Playlist scenario)
            string firstResolved = ResolveAbsolute(m3u8Url, lines[0]);
            if (firstResolved.Contains(".m3u8"))
            {
                // Follow the link to the Media/Variant Playlist
                return await GetSegmentUrlsAsync(firstResolved);
            }

            // We reached the segment playlist; resolve all relative segment URLs
            return lines.Select(line => ResolveAbsolute(m3u8Url, line)).ToList();
        }

        private static string ResolveAbsolute(string baseUrl, string relativeUrl)
        {
            if (Uri.TryCreate(relativeUrl, UriKind.Absolute, out var result))
                return result.ToString();

            return new Uri(new Uri(baseUrl), relativeUrl).ToString();
        }

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch { /* best effort */ }
        }   

        public void Dispose()
        {
            Cancel();
            _http.Dispose();
        }
    }
}