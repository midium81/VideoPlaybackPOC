namespace CustomVideoPlayerPOC.Downloader
{
    public class DownloadMetadata
    {
        public string Url { get; set; } = string.Empty;
        public long ContentLength { get; set; }
        public string? ETag { get; set; }
        public DateTimeOffset LastModified { get; set; }
        public DateTimeOffset DownloadedAt { get; set; }
        public List<(long start, long end)> Ranges { get; set; } = new();
    }
}
