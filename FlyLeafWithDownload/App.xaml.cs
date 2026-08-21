using System.Windows;
using FlyleafLib;

namespace FlyLeafWithDownload
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ":FFmpeg" resolves to the "FFmpeg" folder next to the executable.
            Engine.Start(new EngineConfig
            {
                FFmpegPath = ":FFmpeg",
                UIRefresh = true,
                UIRefreshInterval = 100
            });
        }
    }
}
