using Newtonsoft.Json;
using System.IO;

namespace CustomVideoPlayerPOC.Core
{
    /// <summary>
    /// User settings persisted between application instances, so the retention folder
    /// chosen in one run is reused by the next one.
    /// </summary>
    public sealed class AppSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProgressivePlayer",
            "settings.json");

        public static string DefaultRetentionFolder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProgressivePlayer",
            "Cache");

        /// <summary>Folder where downloaded videos are retained across runs.</summary>
        public string RetentionFolder { get; set; } = DefaultRetentionFolder;

        public string VideoUrl { get; set; } = "https://cdn.mzeeshan.me/assets/Large_1920_1080_1080p_FHD_157_2_MB_5429a568b3.mp4";

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var loaded = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(SettingsPath));
                    if (loaded != null)
                    {
                        if (string.IsNullOrWhiteSpace(loaded.RetentionFolder))
                            loaded.RetentionFolder = DefaultRetentionFolder;

                        return loaded;
                    }
                }
            }
            catch { /* fall back to defaults on any corruption */ }

            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Helpers.EnsureDirectoryForFile(SettingsPath);
                File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch { /* settings are best-effort */ }
        }
    }
}
