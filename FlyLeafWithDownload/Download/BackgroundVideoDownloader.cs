using System.IO;
using Downloader;

namespace FlyLeafWithDownload.Download
{
    /// <summary>
    /// Background download of the video using bezzad/Downloader.
    /// Downloads to a *.part file and atomically promotes it to the final cached file.
    /// </summary>
    public sealed class BackgroundVideoDownloader : IDisposable
    {
        private readonly Cache.VideoCache _cache;
        private DownloadService? _service;
        private CancellationTokenSource? _cts;

        public BackgroundVideoDownloader(Cache.VideoCache cache) => _cache = cache;

        public event EventHandler<double>? ProgressChanged;
        public event EventHandler<string>? Completed;
        public event EventHandler<Exception?>? Failed;

        public bool IsRunning { get; private set; }

        public async Task StartAsync(string url)
        {
            if (IsRunning)
                return;

            var finalPath = _cache.GetCachedFilePath(url);
            var partialPath = _cache.GetPartialFilePath(url);

            var config = new DownloadConfiguration
            {
                ChunkCount = 4,
                ParallelDownload = true,
                MaximumBytesPerSecond = 0,
                BufferBlockSize = 8 * 1024,
                MaxTryAgainOnFailure = 3,
                ReserveStorageSpaceBeforeStartingDownload = true
            };

            _cts = new CancellationTokenSource();
            _service = new DownloadService(config);
            _service.DownloadProgressChanged += OnProgress;
            _service.DownloadFileCompleted += (_, e) => OnCompleted(url, partialPath, finalPath, e);

            IsRunning = true;

            try
            {
                await _service.DownloadFileTaskAsync(url, partialPath, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                IsRunning = false;
                Failed?.Invoke(this, ex);
            }
        }

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch { /* best effort */ }
            IsRunning = false;
        }

        private void OnProgress(object? sender, DownloadProgressChangedEventArgs e)
            => ProgressChanged?.Invoke(this, e.ProgressPercentage);

        private void OnCompleted(string url, string partialPath, string finalPath, System.ComponentModel.AsyncCompletedEventArgs e)
        {
            IsRunning = false;

            if (e.Cancelled)
                return;

            if (e.Error != null)
            {
                Failed?.Invoke(this, e.Error);
                return;
            }

            try
            {
                if (File.Exists(finalPath))
                    File.Delete(finalPath);

                File.Move(partialPath, finalPath);
                _cache.MarkCompleted(url);
                Completed?.Invoke(this, finalPath);
            }
            catch (Exception ex)
            {
                Failed?.Invoke(this, ex);
            }
        }

        public void Dispose()
        {
            Cancel();
            _cts?.Dispose();
            _service?.Dispose();
        }
    }
}
