using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.Wave;
using RetroClip.Services;

namespace RetroClip;

public partial class ClipPlaybackPanel : UserControl
{
    private readonly ClipPlayer _player = new();
    private readonly DispatcherTimer _playbackTimer;
    private string _clipsDirectory = string.Empty;
    private string? _preferredPreviewDeviceId;
    private string? _preferredStreamDeviceId;
    private string? _preferredClipPath;
    private bool _suppressSelectionHandler;
    private bool _initialized;
    private TimeSpan _clipDuration = TimeSpan.Zero;
    private Action? _stopEditPreview;

    public ClipPlaybackPanel()
    {
        InitializeComponent();

        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _playbackTimer.Tick += (_, _) => UpdatePlaybackUi();
        _player.PlaybackStopped += (_, _) => Dispatcher.BeginInvoke(OnPlaybackStoppedUi);

        Unloaded += (_, _) =>
        {
            _playbackTimer.Stop();
            _player.Dispose();
        };
    }

    public void Initialize(string clipsDirectory, Action? stopEditPreview = null)
    {
        _clipsDirectory = clipsDirectory;
        _stopEditPreview = stopEditPreview;
        Directory.CreateDirectory(_clipsDirectory);
        _initialized = true;
        RefreshDevices();
        RefreshClips(forceSelectPreferred: true);
    }

    public void NotifyClipSaved(string path)
    {
        if (!_initialized)
        {
            return;
        }

        var currentPath = (ClipCombo.SelectedItem as ClipInfo)?.Path;
        RefreshClips(preservePath: currentPath, forceSelectPreferred: false);
        SetStatus($"Library updated · new clip {Path.GetFileName(path)}");
    }

    public void StopPlayback()
    {
        _player.Stop();
        OnPlaybackStoppedUi();
    }

    private void RefreshClips_Click(object sender, RoutedEventArgs e)
    {
        var currentPath = (ClipCombo.SelectedItem as ClipInfo)?.Path;
        RefreshClips(preservePath: currentPath, forceSelectPreferred: false);
        SetStatus($"Loaded {ClipLibrary.ListClips(_clipsDirectory).Count} clip(s) for playback.");
    }

    private void RefreshClips(string? preservePath = null, bool forceSelectPreferred = false)
    {
        var clips = ClipLibrary.ListClips(_clipsDirectory);
        var previousPath = (ClipCombo.SelectedItem as ClipInfo)?.Path;

        ClipInfo? selected = null;
        if (forceSelectPreferred && !string.IsNullOrWhiteSpace(_preferredClipPath))
        {
            selected = clips.FirstOrDefault(c =>
                string.Equals(c.Path, _preferredClipPath, StringComparison.OrdinalIgnoreCase));
        }

        selected ??= ResolveClip(clips, preservePath)
                     ?? ResolveClip(clips, previousPath)
                     ?? clips.FirstOrDefault();

        _preferredClipPath = null;

        var sameSelection = selected is not null
                            && !string.IsNullOrWhiteSpace(previousPath)
                            && string.Equals(selected.Path, previousPath, StringComparison.OrdinalIgnoreCase);

        _suppressSelectionHandler = sameSelection
                                    || (!forceSelectPreferred
                                        && selected is not null
                                        && string.Equals(selected.Path, preservePath, StringComparison.OrdinalIgnoreCase));
        try
        {
            if (!_suppressSelectionHandler)
            {
                StopPlayback();
            }

            ClipCombo.ItemsSource = clips;
            ClipCombo.SelectedItem = selected;
            if (selected is not null)
            {
                _clipDuration = selected.Duration;
            }
        }
        finally
        {
            _suppressSelectionHandler = false;
        }

        if (clips.Count == 0)
        {
            _clipDuration = TimeSpan.Zero;
            SetStatus("No clips yet for playback.");
        }
    }

    private static ClipInfo? ResolveClip(IReadOnlyList<ClipInfo> clips, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return clips.FirstOrDefault(c =>
            string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    private void ClipCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionHandler)
        {
            return;
        }

        StopPlayback();

        if (ClipCombo.SelectedItem is ClipInfo clip)
        {
            _clipDuration = clip.Duration;
            SetStatus($"Playback clip: {clip.FileName} ({clip.Duration.TotalSeconds:0.##}s)");
        }
        else
        {
            _clipDuration = TimeSpan.Zero;
        }
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        RefreshDevices();
        SetStatus($"Devices refreshed · preview {PreviewOutputCombo.Items.Count} · stream {StreamTargetCombo.Items.Count}");
    }

    private void RefreshDevices()
    {
        var devices = AudioOutputDevices.ListActiveRenderDevices();
        var defaultId = AudioOutputDevices.GetDefaultRenderDeviceId();

        var previewSelectedId = (PreviewOutputCombo.SelectedItem as AudioOutputDeviceInfo)?.Id
                                ?? _preferredPreviewDeviceId
                                ?? defaultId;
        PreviewOutputCombo.ItemsSource = devices;
        var preview = devices.FirstOrDefault(d =>
                          string.Equals(d.Id, previewSelectedId, StringComparison.OrdinalIgnoreCase))
                      ?? devices.FirstOrDefault(d =>
                          string.Equals(d.Id, defaultId, StringComparison.OrdinalIgnoreCase))
                      ?? devices.FirstOrDefault();
        PreviewOutputCombo.SelectedItem = preview;
        _preferredPreviewDeviceId = preview?.Id;

        var streamSelectedId = (StreamTargetCombo.SelectedItem as AudioOutputDeviceInfo)?.Id
                               ?? _preferredStreamDeviceId;
        StreamTargetCombo.ItemsSource = devices;
        var stream = devices.FirstOrDefault(d =>
                         string.Equals(d.Id, streamSelectedId, StringComparison.OrdinalIgnoreCase))
                     ?? devices.FirstOrDefault(d => AudioOutputDevices.LooksLikeVirtualMicFeed(d.FriendlyName))
                     ?? devices.FirstOrDefault();
        StreamTargetCombo.SelectedItem = stream;
        _preferredStreamDeviceId = stream?.Id;
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewOutputCombo.SelectedItem is not AudioOutputDeviceInfo device)
        {
            SetStatus("Pick a preview output device.", isError: true);
            return;
        }

        _preferredPreviewDeviceId = device.Id;
        StartPlayback(device.Id, null, $"Preview → {device.FriendlyName}");
    }

    private void Stream_Click(object sender, RoutedEventArgs e)
    {
        if (StreamTargetCombo.SelectedItem is not AudioOutputDeviceInfo device)
        {
            SetStatus("Pick a stream target (VoiceMeeter Input).", isError: true);
            return;
        }

        if (!AudioOutputDevices.LooksLikeVirtualMicFeed(device.FriendlyName))
        {
            var owner = Window.GetWindow(this);
            var confirm = MessageBox.Show(
                owner,
                $"\"{device.FriendlyName}\" does not look like a virtual mic feed.\n\nContinue anyway?",
                "Stream target",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        string? localId = null;
        string localNote = string.Empty;
        if (AlsoHearLocallyCheck.IsChecked == true)
        {
            if (PreviewOutputCombo.SelectedItem is not AudioOutputDeviceInfo preview)
            {
                SetStatus("Pick a preview output for local hear.", isError: true);
                return;
            }

            localId = preview.Id;
            localNote = $" + local {preview.FriendlyName}";
            _preferredPreviewDeviceId = preview.Id;
        }

        _preferredStreamDeviceId = device.Id;
        StartPlayback(device.Id, localId, $"Stream → {device.FriendlyName}{localNote}");
    }

    private void StartPlayback(string primaryDeviceId, string? secondaryDeviceId, string status)
    {
        if (!_initialized || ClipCombo.SelectedItem is not ClipInfo clip)
        {
            SetStatus("Select a playback clip first.", isError: true);
            return;
        }

        try
        {
            var start = TimeSpan.Zero;
            var end = clip.Duration;
            if (end <= start)
            {
                SetStatus("Clip has no audio duration.", isError: true);
                return;
            }

            _stopEditPreview?.Invoke();
            _clipDuration = end;
            _player.PlayRange(clip.Path, start, end, primaryDeviceId, secondaryDeviceId);
            _playbackTimer.Start();
            PreviewButton.Content = "Playing…";
            SetStatus($"{status} · {clip.FileName} ({end.TotalSeconds:0.##}s)");
        }
        catch (Exception ex)
        {
            SetStatus($"Playback failed: {ex.Message}", isError: true);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void OnPlaybackStoppedUi()
    {
        _playbackTimer.Stop();
        PreviewButton.Content = "Preview";
        PlaybackProgress.Value = 0;
        PlaybackLabel.Text = "Stopped";
    }

    private void UpdatePlaybackUi()
    {
        if (_player.State != PlaybackState.Playing)
        {
            return;
        }

        var span = Math.Max(0.01, _clipDuration.TotalSeconds);
        PlaybackProgress.Value = Math.Clamp(_player.CurrentTime.TotalSeconds / span, 0, 1);
        PlaybackLabel.Text = $"{_player.CurrentTime.TotalSeconds:0.00}s";
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }
}
