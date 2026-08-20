using System.IO;

namespace CustomVideoPlayerPOC.Core
{
    public static class Helpers
    {
        public static void EnsureDirectoryForFile(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
    }
}
