using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FlyLeafWithDownload.Cache
{
    /// <summary>
    /// Maps a remote video url to a deterministic file inside the retention folder
    /// and tracks whether the download has been fully completed.
    /// </summary>
    public sealed class VideoCache
    {
        public string RetentionFolder { get; }

        public VideoCache(string? retentionFolder = null)
        {
            RetentionFolder = retentionFolder ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FlyLeafWithDownload", "Retention");

            Directory.CreateDirectory(RetentionFolder);
        }

        public string GetCachedFilePath(string url)
        {
            var extension = GetExtension(url);
            return Path.Combine(RetentionFolder, GetKey(url) + extension);
        }

        /// <summary>File used while the download is still running.</summary>
        public string GetPartialFilePath(string url) => GetCachedFilePath(url) + ".part";

        /// <summary>Marker written only after a successful, complete download.</summary>
        public string GetCompletedMarkerPath(string url) => GetCachedFilePath(url) + ".complete";

        public bool IsFullyDownloaded(string url)
            => File.Exists(GetCachedFilePath(url)) && File.Exists(GetCompletedMarkerPath(url));

        public void MarkCompleted(string url)
        {
            File.WriteAllText(GetCompletedMarkerPath(url), url);
        }

        public void Invalidate(string url)
        {
            TryDelete(GetCompletedMarkerPath(url));
            TryDelete(GetPartialFilePath(url));
            TryDelete(GetCachedFilePath(url));
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }

        private static string GetKey(string url)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim()));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string GetExtension(string url)
        {
            try
            {
                var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
                var ext = Path.GetExtension(path);
                if(ext.ToLower() == ".m3u8")
                    return ".mp4"; // HLS streams are converted to mp4
                if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 6)
                    return ext;
            }
            catch { /* fall through */ }

            return ".mp4";
        }
    }
}
