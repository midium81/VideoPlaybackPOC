namespace CustomVideoPlayerPOC.Downloader
{
    /// <summary>
    /// Immutable snapshot of the download state, published by <see cref="RangeDownloader"/>.
    /// </summary>
    public sealed class DownloadProgress
    {
        public DownloadProgress(long downloadedBytes, long totalBytes, bool isComplete)
        {
            DownloadedBytes = downloadedBytes;
            TotalBytes = totalBytes;
            IsComplete = isComplete;
        }

        public long DownloadedBytes { get; }

        /// <summary>Total size in bytes, or -1 when the server did not report a Content-Length.</summary>
        public long TotalBytes { get; }

        public bool IsComplete { get; }

        public double Percent => TotalBytes > 0
            ? Math.Clamp(DownloadedBytes * 100.0 / TotalBytes, 0, 100)
            : 0;

        public override string ToString()
        {
            if (IsComplete)
                return $"Downloaded {FormatBytes(TotalBytes)} (100%)";

            return TotalBytes > 0
                ? $"Downloading {Percent:F1}% - {FormatBytes(DownloadedBytes)} / {FormatBytes(TotalBytes)}"
                : $"Downloading {FormatBytes(DownloadedBytes)} (total size unknown)";
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "?";
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
        }
    }
}
