using CustomVideoPlayerPOC.Cache;
using CustomVideoPlayerPOC.Core;
using CustomVideoPlayerPOC.Downloader;
using CustomVideoPlayerPOC.FFmpeg;
using CustomVideoPlayerPOC.Playback;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace CustomVideoPlayerPOC
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private RangeDownloader? _downloader;
        private GrowingFileCache? _cache;
        private FFmpegIO? _ffio;
        private PlaybackController? _controller;
        private WriteableBitmap? _bitmap;
        private CancellationTokenSource _cts = new();

        private readonly AppSettings _settings = AppSettings.Load();
        private CacheCleaner? _cleaner;

        // Throttles UI updates so a fast download does not flood the dispatcher.
        private double _lastReportedPercent = -1;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            RetentionFolderText.Text = _settings.RetentionFolder;
            await StartPlaybackAsync();
        }

        private async Task StartPlaybackAsync()
        {
            try
            {
                var videoUrl = _settings.VideoUrl;

                var store = new DownloadStore(_settings.RetentionFolder);
                var entry = store.GetEntry(videoUrl);

                // Housekeeping for the retained downloads lives with the chosen folder.
                _cleaner ??= new CacheCleaner(store.RetentionFolder);

                FFmpegHelper.RegisterFFmpegBinaries(AppDomain.CurrentDomain.BaseDirectory);
                FFmpegHelper.Init();

                long fileSize;

                if (entry.TryGetCompleted(out var retainedLength))
                {
                    // A previous application instance already downloaded this video in full.
                    // Play straight from disk - the remote URL is never contacted.
                    fileSize = retainedLength;

                    var ranges = new RangeSet();
                    ranges.Add(new ByteRange(0, retainedLength - 1));

                    _cache = new GrowingFileCache(entry.LocalPath, ranges);
                    _cache.MarkComplete();

                    UpdateDownloadUi(new DownloadProgress(retainedLength, retainedLength, true));
                    StatusText.Text = "Playing retained local file";
                }
                else
                {
                    _downloader = new RangeDownloader(videoUrl, entry.LocalPath);
                    _downloader.ProgressChanged += Downloader_ProgressChanged;
                    _downloader.Completed += Downloader_Completed;
                    await _downloader.InitializeAsync();

                    _cache = new GrowingFileCache(entry.LocalPath, _downloader.DownloadedRanges);

                    // A previously interrupted download may already be complete on disk.
                    if (_downloader.IsComplete)
                        _cache.MarkComplete();

                    UpdateDownloadUi(_downloader.CurrentProgress);

                    // If MP4 and moov at end, fetch tail
                    await EnsureMoovAtomAsync(videoUrl, _downloader, entry.LocalPath, _cache, _cts.Token);

                    fileSize = _downloader.Metadata.ContentLength > 0 ? _downloader.Metadata.ContentLength : 10 * 1024 * 1024;
                    StatusText.Text = "Initialized";
                }

                _ffio = new FFmpegIO(_cache, fileSize);

                _bitmap = new WriteableBitmap(1280, 720, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null);
                VideoImage.Source = _bitmap;

                _controller = new PlaybackController(_ffio, _cache, _downloader, _bitmap);
                _controller.Start();

                // Hook downloader notifications to cache
                if (_downloader != null)
                {
                    var cache = _cache;
                    _ = _downloader.StartPrefetchLoopAsync(() => 0, (r) => cache.NotifyRangeAvailable(r), _cts.Token);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error: " + ex.Message;
            }
        }

        private async void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select the folder where downloads are retained",
                InitialDirectory = Directory.Exists(_settings.RetentionFolder) ? _settings.RetentionFolder : null
            };

            if (dialog.ShowDialog(this) != true) return;
            if (string.Equals(dialog.FolderName, _settings.RetentionFolder, StringComparison.OrdinalIgnoreCase)) return;

            _settings.RetentionFolder = dialog.FolderName;
            _settings.Save();
            RetentionFolderText.Text = _settings.RetentionFolder;

            // Restart against the new location so an already retained copy there is picked up.
            TeardownPlayback();
            _cts = new CancellationTokenSource();
            _lastReportedPercent = -1;
            await StartPlaybackAsync();
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_settings.RetentionFolder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _settings.RetentionFolder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error: " + ex.Message;
            }
        }

        private void Downloader_ProgressChanged(object? sender, DownloadProgress e)
        {
            // Only redraw on a meaningful change - chunks land several times per second.
            if (!e.IsComplete && Math.Abs(e.Percent - _lastReportedPercent) < 0.1) return;
            _lastReportedPercent = e.Percent;

            Dispatcher.BeginInvoke(() => UpdateDownloadUi(e));
        }

        private void Downloader_Completed(object? sender, DownloadProgress e)
        {
            // Everything is on disk: stop consulting the range set and read the local file only.
            _cache?.MarkComplete();

            Dispatcher.BeginInvoke(() =>
            {
                UpdateDownloadUi(e);
                StatusText.Text = "Playing from local file";
            });
        }

        private void UpdateDownloadUi(DownloadProgress progress)
        {
            DownloadProgressBar.Value = progress.Percent;
            DownloadProgressBar.IsIndeterminate = progress.TotalBytes <= 0 && !progress.IsComplete;

            DownloadStatusText.Text = progress.IsComplete
                ? $"Downloaded {DownloadProgress.FormatBytes(progress.TotalBytes)} - local playback"
                : progress.ToString();
        }

        private async Task EnsureMoovAtomAsync(string url, RangeDownloader downloader, string localPath, GrowingFileCache cache, CancellationToken ct)
        {
            // If MP4 and moov atom likely at end, request last N bytes and scan for moov atom.
            // We'll request last 1MB and search for 'moov' box. If found, write to file and notify cache.
            const int tailSize = 1024 * 1024; // 1 MB
            var cl = downloader.Metadata.ContentLength;
            if (cl <= 0) return;
            // Quick heuristic: if no ranges include the start of file (0..some small), request small head too
            // But main goal: ensure moov is available for seeking
            var tailStart = Math.Max(0, cl - tailSize);
            // If already downloaded, skip
            if (downloader.DownloadedRanges.IsRangeAvailable(tailStart, (int)(cl - tailStart))) return;
            try
            {
                await downloader.RequestPriorityRangeAsync(tailStart, cl - 1, ct);
                cache.NotifyRangeAvailable(new ByteRange(tailStart, cl - 1));
                // scan file for 'moov' atom
                using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[cl - tailStart];
                fs.Seek(tailStart, SeekOrigin.Begin);
                await fs.ReadExactlyAsync(buf, 0, buf.Length, ct);
                var moovIndex = FindBox(buf, "moov");
                if (moovIndex >= 0)
                {
                    // moov found in tail; good
                }
                else
                {
                    // If not found, request a larger tail or request head as fallback
                    var headSize = 64 * 1024;
                    if (!downloader.DownloadedRanges.IsRangeAvailable(0, headSize))
                    {
                        await downloader.RequestPriorityRangeAsync(0, headSize - 1, ct);
                        cache.NotifyRangeAvailable(new ByteRange(0, headSize - 1));
                    }
                }
            }
            catch { /* ignore */ }
        }

        private int FindBox(byte[] data, string box)
        {
            var b = System.Text.Encoding.ASCII.GetBytes(box);
            for (int i = 0; i < data.Length - 4; i++)
            {
                if (data[i] == b[0] && data[i + 1] == b[1] && data[i + 2] == b[2] && data[i + 3] == b[3]) return i;
            }
            return -1;
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e) => _controller?.Play();
        private void BtnPause_Click(object sender, RoutedEventArgs e) => _controller?.Pause();
        private async void BtnStep_Click(object sender, RoutedEventArgs e) { if (_controller != null) await _controller.StepFrameAsync(true); }
        private async void BtnSeek10_Click(object sender, RoutedEventArgs e) { if (_controller != null) await _controller.SeekTimeAsync(TimeSpan.FromSeconds(10)); }
        private void BtnFF2_Click(object sender, RoutedEventArgs e) { _controller?.SetRate(2.0); }
        private void BtnFF4_Click(object sender, RoutedEventArgs e) { _controller?.SetRate(4.0); }
        private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { /* map slider to seek if desired */ }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            TeardownPlayback();
        }

        private void TeardownPlayback()
        {
            _cts.Cancel();

            if (_downloader != null)
            {
                _downloader.ProgressChanged -= Downloader_ProgressChanged;
                _downloader.Completed -= Downloader_Completed;
            }

            _controller?.Dispose();
            _ffio?.Dispose();
            _cache?.Dispose();
            _downloader?.Dispose();

            _controller = null;
            _ffio = null;
            _cache = null;
            _downloader = null;
        }
    }
}