using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlyLeafWithDownload.Cache;
using FlyLeafWithDownload.Download;
using FlyleafLib;
using FlyleafLib.MediaPlayer;

namespace FlyLeafWithDownload
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const long SkipTicks = 10 * TimeSpan.TicksPerSecond;

        private readonly VideoCache _cache = new();
        private readonly BackgroundVideoDownloader _downloader;
        private readonly DispatcherTimer _uiTimer;
        private readonly Player _player;

        private string? _currentUrl;
        private bool _isDraggingSlider;
        private bool _isSwitchingSource;

        public MainWindow()
        {
            InitializeComponent();

            _player = new Player(new Config
            {
                Player = { AutoPlay = true },
                Demuxer = { BufferDuration = 30 * 1000 * (long)10000 }
            });

            _downloader = new BackgroundVideoDownloader(_cache);
            _downloader.ProgressChanged += OnDownloadProgress;
            _downloader.Completed += OnDownloadCompleted;
            _downloader.Failed += OnDownloadFailed;

            _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _uiTimer.Tick += (_, _) => UpdateTimeUi();

            SpeedCombo.ItemsSource = new[] { 0.25, 0.5, 1.0, 1.5, 2.0, 4.0 };
            SpeedCombo.SelectedItem = 1.0;

            UrlTextBox.Text = "https://uploads.video-commander.com/sample/BigBuckBunny.mp4";

            Loaded += (_, _) =>
            {
                Host.Player = _player;
                _uiTimer.Start();
            };

            Closed += (_, _) =>
            {
                _uiTimer.Stop();
                _downloader.Dispose();
                _player.Dispose();
            };
        }

        private async void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            var url = UrlTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(url))
                return;

            _downloader.Cancel();
            _currentUrl = url;

            // If the video is already fully retained locally, play it directly.
            if (_cache.IsFullyDownloaded(url))
            {
                var localPath = _cache.GetCachedFilePath(url);
                SetStatus($"Playing cached copy: {localPath}");
                await OpenSourceAsync(localPath, TimeSpan.Zero, playAfterOpen: true);
                return;
            }

            SetStatus("Streaming from network...");
            await OpenSourceAsync(url, TimeSpan.Zero, playAfterOpen: true);

            // Start the background retention download while the stream keeps playing.
            _ = _downloader.StartAsync(url);
        }

        private async Task OpenSourceAsync(string source, TimeSpan resumeAt, bool playAfterOpen)
        {
            var result = await Task.Run(() => _player.Open(source));

            if (!result.Success)
            {
                SetStatus($"Open failed: {result.Error}");
                return;
            }

            if (resumeAt > TimeSpan.Zero)
                _player.SeekAccurate((int)resumeAt.TotalMilliseconds);

            if (playAfterOpen)
                _player.Play();
            else
                _player.Pause();

            UpdateTimeUi();
        }

        private void OnDownloadProgress(object? sender, double percentage)
            => Dispatcher.BeginInvoke(() => SetStatus($"Downloading... {percentage:0.0}%"));

        private void OnDownloadCompleted(object? sender, string localPath)
            => Dispatcher.BeginInvoke(async () => await SwitchToLocalFileAsync(localPath));

        private void OnDownloadFailed(object? sender, Exception? error)
            => Dispatcher.BeginInvoke(() =>
            {
                if (_currentUrl != null)
                    _cache.Invalidate(_currentUrl);

                SetStatus($"Download failed: {error?.Message}");
            });

        /// <summary>Switches playback to the retained file on the fly, keeping position and play state.</summary>
        private async Task SwitchToLocalFileAsync(string localPath)
        {
            if (_isSwitchingSource)
                return;

            _isSwitchingSource = true;

            try
            {
                var position = TimeSpan.FromTicks(_player.CurTime);
                var wasPlaying = _player.IsPlaying;

                SetStatus("Download complete - switching to local file...");
                await OpenSourceAsync(localPath, position, wasPlaying);
                SetStatus($"Playing local file: {localPath}");
            }
            finally
            {
                _isSwitchingSource = false;
            }
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e) => _player.TogglePlayPause();

        private void Backward_Click(object sender, RoutedEventArgs e) => SeekTo(_player.CurTime - SkipTicks);

        private void Forward_Click(object sender, RoutedEventArgs e) => SeekTo(_player.CurTime + SkipTicks);

        private void PrevFrame_Click(object sender, RoutedEventArgs e) => _player.ShowFramePrev();

        private void NextFrame_Click(object sender, RoutedEventArgs e) => _player.ShowFrameNext();

        private void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SpeedCombo.SelectedItem is double speed)
                _player.Speed = speed;
        }

        private void SeekSlider_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => _isDraggingSlider = true;

        private void SeekSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isDraggingSlider = false;

            if (_player.Duration > 0)
                SeekTo((long)(_player.Duration * (SeekSlider.Value / SeekSlider.Maximum)));
        }

        private void SeekTo(long ticks)
        {
            if (_player.Duration <= 0)
                return;

            ticks = Math.Clamp(ticks, 0, _player.Duration);
            _player.SeekAccurate((int)TimeSpan.FromTicks(ticks).TotalMilliseconds);
        }

        private void UpdateTimeUi()
        {
            var duration = _player.Duration;

            CurTimeText.Text = TimeSpan.FromTicks(_player.CurTime).ToString(@"hh\:mm\:ss");
            DurationText.Text = TimeSpan.FromTicks(duration).ToString(@"hh\:mm\:ss");
            PlayPauseButton.Content = _player.IsPlaying ? "Pause" : "Play";

            if (!_isDraggingSlider && duration > 0)
                SeekSlider.Value = (double)_player.CurTime / duration * SeekSlider.Maximum;
        }

        private void SetStatus(string status) => StatusText.Text = status;
    }
}