using CustomVideoPlayerPOC.Downloader;
using Newtonsoft.Json;
using System.IO;

namespace CustomVideoPlayerPOC.Cache
{
    public class CacheCleaner
    {
        private readonly string _cacheFolder;
        private readonly int _retentionDays;
        private readonly Timer _timer;

        public CacheCleaner(string cacheFolder, int retentionDays = 7)
        {
            _cacheFolder = cacheFolder;
            _retentionDays = retentionDays;
            _timer = new Timer(Cleanup, null, TimeSpan.Zero, TimeSpan.FromHours(24));
        }

        private void Cleanup(object state)
        {
            try
            {
                var files = Directory.GetFiles(_cacheFolder, "*.mp4");
                foreach (var f in files)
                {
                    var meta = f + ".meta.json";
                    DateTimeOffset dt = File.GetLastWriteTimeUtc(f);
                    if (File.Exists(meta))
                    {
                        var json = File.ReadAllText(meta);
                        var m = JsonConvert.DeserializeObject<DownloadMetadata>(json);
                        if (m != null) dt = m.DownloadedAt;
                    }
                    if (DateTimeOffset.UtcNow - dt > TimeSpan.FromDays(_retentionDays))
                    {
                        File.Delete(f);
                        if (File.Exists(meta)) File.Delete(meta);
                    }
                }
            }
            catch { /* log */ }
        }
    }
}
