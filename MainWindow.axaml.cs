using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TagLib;

namespace Sonix
{
    public class SongItem
    {
        public string FilePath { get; set; } = "";
        public string Title { get; set; } = "Unknown Title";
        public string Artist { get; set; } = "Unknown Artist";
        public string Album { get; set; } = "";
        public string Duration { get; set; } = "0:00";
        public double DurationSeconds { get; set; }
        public string Bitrate { get; set; } = "";
        public string SampleRate { get; set; } = "";
        public string Channels { get; set; } = "";
        public bool HasAlbumArt { get; set; }
        public Bitmap? AlbumArtBitmap { get; set; }
    }

    public partial class MainWindow : Window
    {
        private LibVLC? _libVLC;
        private MediaPlayer? _mediaPlayer;
        private DispatcherTimer? _progressTimer;
        private DispatcherTimer? _spectrumTimer;
        private ObservableCollection<SongItem> _allSongs = new();
        private ObservableCollection<SongItem> _filteredSongs = new();
        private int _currentIndex = -1;
        private bool _isPlaying = false;
        private bool _isSeeking = false;
        private bool _isShuffle = false;
        private bool _isRepeat = false;
        private bool _isMuted = false;
        private double _lastVolume = 80;
        private Random _random = new();

        // Spectrum visualizer state
        private double[] _spectrumBars = new double[48];
        private double[] _spectrumTargets = new double[48];
        private readonly IBrush[] _spectrumColors;

        public MainWindow()
        {
            InitializeComponent();

            // Build gradient colors for spectrum bars
            _spectrumColors = BuildSpectrumColors(48);

            try
            {
                Core.Initialize();
                _libVLC = new LibVLC("--no-video");
                _mediaPlayer = new MediaPlayer(_libVLC);
                _mediaPlayer.EndReached += OnTrackEnded;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"VLC init error: {ex.Message}";
            }

            PlaylistBox.ItemsSource = _filteredSongs;

            // Progress timer (100ms)
            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _progressTimer.Tick += ProgressTimer_Tick;
            _progressTimer.Start();

            // Spectrum timer (50ms = ~20fps)
            _spectrumTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _spectrumTimer.Tick += SpectrumTimer_Tick;
            _spectrumTimer.Start();

            SpectrumCanvas.GetObservable(BoundsProperty).Subscribe(_ => DrawSpectrum());
        }

        // ─── SPECTRUM VISUALIZER ───────────────────────────────────────────────

        private IBrush[] BuildSpectrumColors(int count)
        {
            var colors = new IBrush[count];
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / count;
                // Purple → Violet → Pink gradient
                byte r = (byte)(124 + t * 131);
                byte g = (byte)(58 - t * 30);
                byte b = (byte)(237 - t * 100);
                colors[i] = new SolidColorBrush(new Color(200, r, g, b));
            }
            return colors;
        }

        private void SpectrumTimer_Tick(object? sender, EventArgs e)
        {
            // Animate toward random targets when playing, decay when paused
            for (int i = 0; i < _spectrumBars.Length; i++)
            {
                if (_isPlaying)
                {
                    // Occasionally update targets
                    if (_random.NextDouble() < 0.3)
                    {
                        double freq = (double)i / _spectrumBars.Length;
                        // Bass-heavy distribution
                        double maxH = freq < 0.15 ? 0.9 : freq < 0.4 ? 0.75 : 0.5;
                        _spectrumTargets[i] = _random.NextDouble() * maxH;
                    }
                    // Smooth approach
                    _spectrumBars[i] += (_spectrumTargets[i] - _spectrumBars[i]) * 0.35;
                }
                else
                {
                    _spectrumBars[i] *= 0.88; // decay
                }
            }
            DrawSpectrum();
        }

        private void DrawSpectrum()
        {
            var canvas = SpectrumCanvas;
            canvas.Children.Clear();
            double w = canvas.Bounds.Width;
            double h = canvas.Bounds.Height;
            if (w <= 0 || h <= 0) return;

            int count = _spectrumBars.Length;
            double barWidth = (w / count) * 0.7;
            double gap = (w / count) * 0.3;
            double maxBarH = h * 0.55;
            double baseY = h * 0.75;

            for (int i = 0; i < count; i++)
            {
                double x = i * (barWidth + gap) + gap / 2;
                double barH = Math.Max(3, _spectrumBars[i] * maxBarH);

                // Main bar
                var rect = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = barWidth,
                    Height = barH,
                    Fill = _spectrumColors[Math.Min(i, _spectrumColors.Length - 1)],
                    RadiusX = barWidth / 2,
                    RadiusY = barWidth / 2,
                    Opacity = 0.85 + _spectrumBars[i] * 0.15
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, baseY - barH);
                canvas.Children.Add(rect);

                // Mirror (reflection below)
                var mirror = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = barWidth,
                    Height = barH * 0.3,
                    Fill = _spectrumColors[Math.Min(i, _spectrumColors.Length - 1)],
                    RadiusX = barWidth / 2,
                    RadiusY = barWidth / 2,
                    Opacity = 0.15
                };
                Canvas.SetLeft(mirror, x);
                Canvas.SetTop(mirror, baseY + 4);
                canvas.Children.Add(mirror);
            }
        }

        // ─── FILE LOADING ──────────────────────────────────────────────────────

        private async void AddBtn_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add Songs",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Audio Files")
                    {
                        Patterns = new[] { "*.mp3", "*.flac", "*.wav", "*.aac",
                                           "*.ogg", "*.m4a", "*.opus", "*.wma" }
                    }
                }
            });

            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path == null || _allSongs.Any(s => s.FilePath == path)) continue;
                var song = await LoadSongMetadata(path);
                _allSongs.Add(song);
            }
            ApplySearch(SearchBox.Text ?? "");
            StatusText.Text = $"{_allSongs.Count} song{(_allSongs.Count != 1 ? "s" : "")} in library";
        }

        private Task<SongItem> LoadSongMetadata(string path)
        {
            return Task.Run(() =>
            {
                var song = new SongItem { FilePath = path };
                try
                {
                    var tag = TagLib.File.Create(path);
                    song.Title = string.IsNullOrWhiteSpace(tag.Tag.Title)
                        ? Path.GetFileNameWithoutExtension(path) : tag.Tag.Title;
                    song.Artist = tag.Tag.FirstPerformer ?? "Unknown Artist";
                    song.Album = tag.Tag.Album ?? "";
                    var ts = tag.Properties.Duration;
                    song.DurationSeconds = ts.TotalSeconds;
                    song.Duration = FormatTime(ts.TotalSeconds);
                    song.Bitrate = tag.Properties.AudioBitrate > 0
                        ? $"{tag.Properties.AudioBitrate} kbps" : "";
                    song.SampleRate = tag.Properties.AudioSampleRate > 0
                        ? $"{tag.Properties.AudioSampleRate / 1000.0:0.#} kHz" : "";
                    song.Channels = tag.Properties.AudioChannels == 2 ? "Stereo"
                        : tag.Properties.AudioChannels == 1 ? "Mono" : "";

                    // Album art
                    var pic = tag.Tag.Pictures?.FirstOrDefault();
                    if (pic != null)
                    {
                        using var ms = new MemoryStream(pic.Data.Data);
                        song.AlbumArtBitmap = new Bitmap(ms);
                        song.HasAlbumArt = true;
                    }
                }
                catch
                {
                    song.Title = Path.GetFileNameWithoutExtension(path);
                }
                return song;
            });
        }

        // ─── PLAYBACK ─────────────────────────────────────────────────────────

        private void PlaylistBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (PlaylistBox.SelectedIndex >= 0)
            {
                _currentIndex = PlaylistBox.SelectedIndex;
                PlaySong(_filteredSongs[_currentIndex]);
            }
        }

        private void PlaySong(SongItem song)
        {
            if (_mediaPlayer == null || _libVLC == null) return;
            _mediaPlayer.Stop();
            using var media = new Media(_libVLC, song.FilePath);
            _mediaPlayer.Media = media;
            _mediaPlayer.Volume = (int)VolumeSlider.Value;
            _mediaPlayer.Play();
            _isPlaying = true;

            // Update UI
            TitleText.Text = song.Title;
            ArtistText.Text = song.Artist;
            AlbumText.Text = song.Album;
            BitrateText.Text = song.Bitrate;
            SampleRateText.Text = song.SampleRate;
            ChannelsText.Text = song.Channels;
            PlayPauseIcon.Text = "⏸";
            StatusText.Text = $"Playing · {song.Artist} — {song.Title}";

            if (song.HasAlbumArt && song.AlbumArtBitmap != null)
            {
                AlbumArtImage.Source = song.AlbumArtBitmap;
                AlbumArtImage.IsVisible = true;
            }
            else
            {
                AlbumArtImage.IsVisible = false;
            }
        }

        private void PlayPauseBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            if (_currentIndex < 0 && _filteredSongs.Count > 0)
            {
                _currentIndex = 0;
                PlaylistBox.SelectedIndex = 0;
                PlaySong(_filteredSongs[0]);
                return;
            }
            if (_isPlaying)
            {
                _mediaPlayer.Pause();
                _isPlaying = false;
                PlayPauseIcon.Text = "▶";
                StatusText.Text = $"Paused · {TitleText.Text}";
            }
            else
            {
                _mediaPlayer.Play();
                _isPlaying = true;
                PlayPauseIcon.Text = "⏸";
                StatusText.Text = $"Playing · {TitleText.Text}";
            }
        }

        private void PrevBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (_filteredSongs.Count == 0) return;
            // If > 3s into song, restart; else go prev
            if (_mediaPlayer != null && _mediaPlayer.Time > 3000)
            {
                _mediaPlayer.Time = 0;
                return;
            }
            int next = _isShuffle
                ? _random.Next(_filteredSongs.Count)
                : (_currentIndex - 1 + _filteredSongs.Count) % _filteredSongs.Count;
            _currentIndex = next;
            PlaylistBox.SelectedIndex = next;
            PlaySong(_filteredSongs[next]);
        }

        private void NextBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (_filteredSongs.Count == 0) return;
            int next = _isShuffle
                ? _random.Next(_filteredSongs.Count)
                : (_currentIndex + 1) % _filteredSongs.Count;
            _currentIndex = next;
            PlaylistBox.SelectedIndex = next;
            PlaySong(_filteredSongs[next]);
        }

        private void OnTrackEnded(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_isRepeat && _currentIndex >= 0)
                {
                    PlaySong(_filteredSongs[_currentIndex]);
                }
                else
                {
                    NextBtn_Click(null, null!);
                }
            });
        }

        // ─── SHUFFLE / REPEAT ─────────────────────────────────────────────────

        private void ShuffleBtn_Click(object? sender, RoutedEventArgs e)
        {
            _isShuffle = !_isShuffle;
            ShuffleIcon.Foreground = _isShuffle
                ? new SolidColorBrush(Color.Parse("#7C3AED"))
                : new SolidColorBrush(Color.Parse("#555"));
        }

        private void RepeatBtn_Click(object? sender, RoutedEventArgs e)
        {
            _isRepeat = !_isRepeat;
            RepeatIcon.Foreground = _isRepeat
                ? new SolidColorBrush(Color.Parse("#7C3AED"))
                : new SolidColorBrush(Color.Parse("#555"));
        }

        // ─── VOLUME ───────────────────────────────────────────────────────────

        private void VolumeSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            _mediaPlayer.Volume = (int)e.NewValue;
            _lastVolume = e.NewValue;
            VolumeIcon.Text = e.NewValue == 0 ? "🔇" : e.NewValue < 40 ? "🔈" : e.NewValue < 70 ? "🔉" : "🔊";
        }

        private void MuteBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            _isMuted = !_isMuted;
            if (_isMuted)
            {
                _mediaPlayer.Volume = 0;
                VolumeSlider.Value = 0;
            }
            else
            {
                VolumeSlider.Value = _lastVolume;
                _mediaPlayer.Volume = (int)_lastVolume;
            }
        }

        // ─── PROGRESS / SEEK ──────────────────────────────────────────────────

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (_mediaPlayer == null || _isSeeking) return;
            var dur = _mediaPlayer.Length;
            var pos = _mediaPlayer.Time;
            if (dur > 0)
            {
                ProgressSlider.Value = (double)pos / dur * 100;
                CurrentTimeText.Text = FormatTime(pos / 1000.0);
                TotalTimeText.Text = FormatTime(dur / 1000.0);
            }
        }

        private void ProgressSlider_PointerPressed(object? sender, PointerPressedEventArgs e)
            => _isSeeking = true;

        private void ProgressSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_mediaPlayer != null && _mediaPlayer.Length > 0)
            {
                _mediaPlayer.Time = (long)(ProgressSlider.Value / 100.0 * _mediaPlayer.Length);
            }
            _isSeeking = false;
        }

        // ─── SEARCH ───────────────────────────────────────────────────────────

        private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
            => ApplySearch(SearchBox.Text ?? "");

        private void ApplySearch(string q)
        {
            _filteredSongs.Clear();
            var filtered = string.IsNullOrWhiteSpace(q)
                ? _allSongs
                : _allSongs.Where(s =>
                    s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    s.Artist.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    s.Album.Contains(q, StringComparison.OrdinalIgnoreCase));
            foreach (var s in filtered) _filteredSongs.Add(s);
        }

        // ─── WINDOW CONTROLS ──────────────────────────────────────────────────

        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private void MinBtn_Click(object? sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void MaxBtn_Click(object? sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;

        private void CloseBtn_Click(object? sender, RoutedEventArgs e)
            => Close();

        // ─── HELPERS ─────────────────────────────────────────────────────────

        private static string FormatTime(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        protected override void OnClosed(EventArgs e)
        {
            _progressTimer?.Stop();
            _spectrumTimer?.Stop();
            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();
            base.OnClosed(e);
        }
    }
}
