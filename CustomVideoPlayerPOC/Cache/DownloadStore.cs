using CustomVideoPlayerPOC.Core;
using CustomVideoPlayerPOC.Downloader;
using Newtonsoft.Json;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CustomVideoPlayerPOC.Cache
{
    /// <summary>
    /// Maps a video URL to a stable location inside the retention folder, and can tell whether a
    /// previous run already downloaded it in full.
    ///
    /// The old code used a fixed "video.mp4" name, so every URL overwrote the same file and nothing
    /// could be reused. Names are now derived from the URL, allowing several videos to be retained
    /// side by side and recognised on the next application start.
    /// </summary>
    public sealed class DownloadStore
    {
        public DownloadStore(string retentionFolder)
        {
            RetentionFolder = string.IsNullOrWhiteSpace(retentionFolder)
                ? AppSettings.DefaultRetentionFolder
                : retentionFolder;
        }

        public string RetentionFolder { get; }

        public DownloadEntry GetEntry(string url)
        {
            Directory.CreateDirectory(RetentionFolder);

            var localPath = Path.Combine(RetentionFolder, BuildFileName(url));
            return new DownloadEntry(url, localPath);
        }

        /// <summary>
        /// "BigBuckBunny_3f2a1c9d.mp4" - readable, collision-free and stable for a given URL.
        /// </summary>
        private static string BuildFileName(string url)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..8].ToLowerInvariant();

            var name = "video";
            var extension = ".mp4";

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var leaf = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(leaf)) name = leaf;

                var ext = Path.GetExtension(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 6) extension = ext;
            }

            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            if (name.Length > 60) name = name[..60];

            return $"{name}_{hash}{extension}";
        }
    }

    /// <summary>A single retained download: the media file plus its sidecar metadata.</summary>
    public sealed class DownloadEntry
    {
        public DownloadEntry(string url, string localPath)
        {
            Url = url;
            LocalPath = localPath;
            MetaPath = localPath + ".meta.json";
        }

        public string Url { get; }
        public string LocalPath { get; }
        public string MetaPath { get; }

        /// <summary>
        /// True when a previous run downloaded this URL completely and the bytes are still on disk.
        /// When this returns true the player can start without contacting the server at all.
        /// </summary>
        public bool TryGetCompleted(out long contentLength)
        {
            contentLength = 0;

            try
            {
                if (!File.Exists(LocalPath) || !File.Exists(MetaPath)) return false;

                var meta = JsonConvert.DeserializeObject<DownloadMetadata>(File.ReadAllText(MetaPath));
                if (meta == null || meta.Url != Url || meta.ContentLength <= 0) return false;

                // The file must still be the full expected size.
                if (new FileInfo(LocalPath).Length != meta.ContentLength) return false;

                // And the recorded ranges must cover every byte.
                var ranges = new RangeSet();
                foreach (var r in meta.Ranges) ranges.Add(new ByteRange(r.start, r.end));
                if (!ranges.IsComplete(meta.ContentLength)) return false;

                contentLength = meta.ContentLength;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
