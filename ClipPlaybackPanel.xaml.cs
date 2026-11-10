using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using RetroClip.Services;

namespace RetroClip;

public partial class ClipPlaybackPanel : UserControl
{
    private readonly ClipPlayer _player = new();
    private readonly DispatcherTimer _playbackTimer;
    private readonly Button[] _padButtons = new Button[SoundboardStore.SlotCount];
    private string _clipsDirectory = string.Empty;
    private SoundboardStore? _soundboard;
    private string? _preferredPreviewDeviceId;
    private string? _preferredStreamDeviceId;
    private string? _preferredClipPath;
    private IReadOnlyList<ClipInfo> _allClips = Array.Empty<ClipInfo>();
    private bool _suppressSelectionHandler;
    private bool _suppressPreferenceSave;
    private bool _initialized;
    private bool _externalProgressActive;
    private TimeSpan _clipDuration = TimeSpan.Zero;
    private Action? _stopEditPreview;
    private UiPreferencesStore? _preferences;

    public ClipPlaybackPanel()
    {
        InitializeComponent();
        BuildSoundboardPads();

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

    public void Initialize(
        string clipsDirectory,
        UiPreferencesStore preferences,
        Action? stopEditPreview = null)
    {
        _clipsDirectory = clipsDirectory;
        _preferences = preferences;
        _stopEditPreview = stopEditPreview;
        Directory.CreateDirectory(_clipsDirectory);
        _soundboard = new SoundboardStore(_clipsDirectory);
        _preferredPreviewDeviceId = preferences.Current.PlaybackPreviewDeviceId;
        _preferredStreamDeviceId = preferences.Current.PlaybackStreamDeviceId;
        _preferredClipPath = preferences.Current.PlaybackClipPath
                             ?? preferences.Current.SoundboardClipPath;
        _initialized = true;
        _suppressPreferenceSave = true;
        try
        {
            AlsoHearLocallyCheck.IsChecked = preferences.Current.AlsoHearLocally;
            RefreshDevices();
            RefreshClips(forceSelectPreferred: true);
            RefreshSoundboardPresetCombo();
            RefreshSoundboardPads();
        }
        finally
        {
            _suppressPreferenceSave = false;
        }

        PersistPreferences();
    }

    public void PersistPreferences()
    {
        if (_preferences is null || !_initialized)
        {
            return;
        }

        _preferences.Current.PlaybackPreviewDeviceId =
            (PreviewOutputCombo.SelectedItem as AudioOutputDeviceInfo)?.Id ?? _preferredPreviewDeviceId;
        _preferences.Current.PlaybackStreamDeviceId =
            (StreamTargetCombo.SelectedItem as AudioOutputDeviceInfo)?.Id ?? _preferredStreamDeviceId;
        var selectedPath = (ClipCombo.SelectedItem as ClipInfo)?.Path ?? _preferredClipPath;
        _preferences.Current.PlaybackClipPath = selectedPath;
        _preferences.Current.SoundboardClipPath = selectedPath;
        _preferences.Current.AlsoHearLocally = AlsoHearLocallyCheck.IsChecked == true;
        _preferences.Save();
    }

    public void NotifyClipSaved(string path)
    {
        if (!_initialized)
        {
            return;
        }

        var selectedPath = (ClipCombo.SelectedItem as ClipInfo)?.Path;
        RefreshClips(preservePath: selectedPath, forceSelectPreferred: false);
        RefreshSoundboardPads();
        SetStatus($"Library updated · new clip {Path.GetFileName(path)}");
    }

    public void StopPlayback()
    {
        _externalProgressActive = false;
        _player.Stop();
        OnPlaybackStoppedUi();
    }

    public void ReportExternalPreviewProgress(TimeSpan elapsed, TimeSpan duration, string label)
    {
        if (!_initialized)
        {
            return;
        }

        _externalProgressActive = true;
        _playbackTimer.Stop();
        var span = Math.Max(0.01, duration.TotalSeconds);
        PlaybackProgress.Value = Math.Clamp(elapsed.TotalSeconds / span, 0, 1);
        PlaybackLabel.Text = string.IsNullOrWhiteSpace(label)
            ? $"{elapsed.TotalSeconds:0.00}s"
            : $"{label} · {elapsed.TotalSeconds:0.00}s";
    }

    public void ClearExternalPreviewProgress()
    {
        if (!_externalProgressActive)
        {
            return;
        }

        _externalProgressActive = false;
        if (_player.State == PlaybackState.Playing)
        {
            return;
        }

        PlaybackProgress.Value = 0;
        PlaybackLabel.Text = "Stopped";
    }

    private void BuildSoundboardPads()
    {
        SoundboardGrid.Children.Clear();
        for (var i = 0; i < SoundboardStore.SlotCount; i++)
        {
            var index = i;
            var button = new Button
            {
                Style = (Style)FindResource("SoundboardPadButton"),
                Margin = new Thickness(4),
                Tag = index
            };

            var assignItem = new MenuItem { Header = "Assign soundboard clip" };
            assignItem.Click += (_, _) => AssignPad(index);

            var clearItem = new MenuItem { Header = "Clear" };
            clearItem.Click += (_, _) => ClearPad(index);

            var previewItem = new MenuItem { Header = "Preview pad" };
            previewItem.Click += (_, _) => PreviewPad(index);

            button.ContextMenu = new ContextMenu
            {
                Items = { assignItem, clearItem, new Separator(), previewItem }
            };
            button.Click += (_, _) => StreamPad(index);

            _padButtons[i] = button;
            SoundboardGrid.Children.Add(button);
        }
    }

    private void RefreshSoundboardPads()
    {
        if (_soundboard is null)
        {
            return;
        }

        for (var i = 0; i < SoundboardStore.SlotCount; i++)
        {
            UpdatePadVisual(i);
        }
    }

    private void RefreshSoundboardPresetCombo()
    {
        if (_soundboard is null)
        {
            return;
        }

        var names = _soundboard.ListPresetNames();
        var active = _soundboard.ActivePresetName;
        _suppressPreferenceSave = true;
        try
        {
            SoundboardPresetPickCombo.ItemsSource = names;
            SoundboardPresetPickCombo.SelectedItem = names.FirstOrDefault(n =>
                string.Equals(n, active, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressPreferenceSave = false;
        }
    }

    private void ClearSoundboardPresetNameBox()
    {
        SoundboardPresetNameBox.Text = string.Empty;
    }

    private string? GetSoundboardPresetNameFromUi()
    {
        var typed = SoundboardPresetNameBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(typed))
        {
            return typed;
        }

        return SoundboardPresetPickCombo.SelectedItem as string is { Length: > 0 } selected
            ? selected.Trim()
            : null;
    }

    private void SoundboardPresetLoad_Click(object sender, RoutedEventArgs e)
    {
        if (_soundboard is null)
        {
            return;
        }

        var name = GetSoundboardPresetNameFromUi();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetStatus("Type a name or pick a preset to load.", isError: true);
            return;
        }

        if (!_soundboard.LoadPreset(name))
        {
            SetStatus($"Preset not found: {name}", isError: true);
            RefreshSoundboardPresetCombo();
            return;
        }

        ClearSoundboardPresetNameBox();
        RefreshSoundboardPresetCombo();
        RefreshSoundboardPads();
        SetStatus($"Loaded soundboard preset “{name}”.");
    }

    private void SoundboardPresetSave_Click(object sender, RoutedEventArgs e)
    {
        if (_soundboard is null)
        {
            return;
        }

        // Typed name creates/overwrites that preset; empty box saves into the selected preset.
        var name = GetSoundboardPresetNameFromUi();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetStatus("Type a new preset name, or pick a preset to overwrite.", isError: true);
            return;
        }

        if (!_soundboard.SavePreset(name))
        {
            SetStatus("Enter a valid preset name.", isError: true);
            return;
        }

        ClearSoundboardPresetNameBox();
        RefreshSoundboardPresetCombo();
        SetStatus($"Saved soundboard preset “{name}”.");
    }

    private void SoundboardPresetDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_soundboard is null)
        {
            return;
        }

        var name = GetSoundboardPresetNameFromUi();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetStatus("Type a name or pick a preset to delete.", isError: true);
            return;
        }

        if (string.Equals(name, SoundboardStore.DefaultPresetName, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus($"Cannot delete the “{SoundboardStore.DefaultPresetName}” preset.", isError: true);
            return;
        }

        var owner = Window.GetWindow(this);
        var confirm = MessageBox.Show(
            owner,
            $"Delete soundboard preset “{name}”?",
            "Delete preset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        if (!_soundboard.DeletePreset(name))
        {
            SetStatus($"Could not delete preset “{name}”.", isError: true);
            RefreshSoundboardPresetCombo();
            return;
        }

        ClearSoundboardPresetNameBox();
        RefreshSoundboardPresetCombo();
        RefreshSoundboardPads();
        SetStatus($"Deleted soundboard preset “{name}”.");
    }

    private void UpdatePadVisual(int index)
    {
        var button = _padButtons[index];
        var path = _soundboard?.GetPath(index);

        string label;
        Brush foreground;

        if (string.IsNullOrWhiteSpace(path))
        {
            label = "Unassigned";
            foreground = (Brush)FindResource("MutedBrush");
        }
        else if (!File.Exists(path))
        {
            label = "Missing";
            foreground = (Brush)FindResource("DangerBrush");
        }
        else
        {
            label = Path.GetFileNameWithoutExtension(path);
            foreground = (Brush)FindResource("TextBrush");
        }

        button.Content = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxHeight = 32,
            ToolTip = string.IsNullOrWhiteSpace(path) ? "Right-click to assign the soundboard clip" : path
        };
        button.ToolTip = string.IsNullOrWhiteSpace(path)
            ? "Right-click to assign the soundboard clip"
            : path;
    }

    private void AssignPad(int index)
    {
        if (_soundboard is null)
        {
            return;
        }

        if (ClipCombo.SelectedItem is not ClipInfo clip)
        {
            SetStatus("Select a clip in the shared picker first, then Assign.", isError: true);
            return;
        }

        _soundboard.SetPath(index, clip.Path);
        UpdatePadVisual(index);
        SetStatus($"Pad {index + 1} ← {clip.FileName}");
    }

    private void ClearPad(int index)
    {
        if (_soundboard is null)
        {
            return;
        }

        _soundboard.SetPath(index, null);
        UpdatePadVisual(index);
        SetStatus($"Pad {index + 1} cleared.");
    }

    private void StreamPad(int index) => PlayPad(index, stream: true);

    private void PreviewPad(int index) => PlayPad(index, stream: false);

    private void PlayPad(int index, bool stream)
    {
        if (_soundboard is null)
        {
            return;
        }

        var path = _soundboard.GetPath(index);
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus($"Pad {index + 1} is empty. Right-click → Assign soundboard clip.", isError: true);
            return;
        }

        if (!File.Exists(path))
        {
            SetStatus($"Pad {index + 1} file missing: {Path.GetFileName(path)}", isError: true);
            UpdatePadVisual(index);
            return;
        }

        TimeSpan duration;
        try
        {
            using var reader = new AudioFileReader(path);
            duration = reader.TotalTime;
        }
        catch (Exception ex)
        {
            SetStatus($"Pad {index + 1} unreadable: {ex.Message}", isError: true);
            return;
        }

        if (stream)
        {
            StreamClip(path, duration, Path.GetFileName(path));
        }
        else
        {
            PreviewClip(path, duration, Path.GetFileName(path));
        }
    }

    private void RefreshClips_Click(object sender, RoutedEventArgs e)
    {
        var selectedPath = (ClipCombo.SelectedItem as ClipInfo)?.Path;
        RefreshClips(preservePath: selectedPath, forceSelectPreferred: false);
        RefreshSoundboardPads();
        SetStatus($"Loaded {_allClips.Count} clip(s).");
    }

    private void ClipSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        ApplyClipFilter(preserveSelectedPath: (ClipCombo.SelectedItem as ClipInfo)?.Path, stopIfSelectionChanges: false);
    }

    private void RefreshClips(string? preservePath = null, bool forceSelectPreferred = false)
    {
        _allClips = ClipLibrary.ListClips(_clipsDirectory);
        var previousPath = (ClipCombo.SelectedItem as ClipInfo)?.Path;

        ClipInfo? selected = null;
        if (forceSelectPreferred && !string.IsNullOrWhiteSpace(_preferredClipPath))
        {
            selected = _allClips.FirstOrDefault(c =>
                string.Equals(c.Path, _preferredClipPath, StringComparison.OrdinalIgnoreCase));
        }

        selected ??= ResolveClip(_allClips, preservePath)
                     ?? ResolveClip(_allClips, previousPath)
                     ?? _allClips.FirstOrDefault();

        _preferredClipPath = null;

        var sameSelection = selected is not null
                            && !string.IsNullOrWhiteSpace(previousPath)
                            && string.Equals(selected.Path, previousPath, StringComparison.OrdinalIgnoreCase);

        ApplyClipFilter(preserveSelectedPath: selected?.Path, stopIfSelectionChanges: !sameSelection);

        if (_allClips.Count == 0)
        {
            _clipDuration = TimeSpan.Zero;
            SetStatus("No clips yet for playback.");
        }
    }

    private void ApplyClipFilter(string? preserveSelectedPath, bool stopIfSelectionChanges)
    {
        var filtered = ClipSearch.Filter(_allClips, ClipSearchBox.Text);
        var selected = ResolveClip(filtered, preserveSelectedPath)
                       ?? ResolveClip(_allClips, preserveSelectedPath);
        if (selected is not null && ResolveClip(filtered, selected.Path) is null)
        {
            filtered = filtered.Append(selected).ToList();
        }

        selected = ResolveClip(filtered, preserveSelectedPath) ?? filtered.FirstOrDefault();

        var previousPath = (ClipCombo.SelectedItem as ClipInfo)?.Path;
        var sameSelection = selected is not null
                            && !string.IsNullOrWhiteSpace(previousPath)
                            && string.Equals(selected.Path, previousPath, StringComparison.OrdinalIgnoreCase);

        _suppressSelectionHandler = sameSelection || !stopIfSelectionChanges;
        try
        {
            if (!_suppressSelectionHandler)
            {
                StopPlayback();
            }

            ClipCombo.ItemsSource = filtered;
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

        PersistPreferences();
    }

    private void PlaybackDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPreferenceSave || !_initialized)
        {
            return;
        }

        if (PreviewOutputCombo.SelectedItem is AudioOutputDeviceInfo preview)
        {
            _preferredPreviewDeviceId = preview.Id;
        }

        if (StreamTargetCombo.SelectedItem is AudioOutputDeviceInfo stream)
        {
            _preferredStreamDeviceId = stream.Id;
        }

        PersistPreferences();
    }

    private void AlsoHearLocallyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPreferenceSave || !_initialized)
        {
            return;
        }

        PersistPreferences();
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

        // Separate lists so the two ComboBoxes do not share ItemsSource selection state.
        var previewDevices = devices.ToList();
        var streamDevices = devices.ToList();

        var previewSelectedId = (PreviewOutputCombo.SelectedItem as AudioOutputDeviceInfo)?.Id
                                ?? _preferredPreviewDeviceId
                                ?? defaultId;
        PreviewOutputCombo.ItemsSource = previewDevices;
        var preview = previewDevices.FirstOrDefault(d =>
                          string.Equals(d.Id, previewSelectedId, StringComparison.OrdinalIgnoreCase))
                      ?? previewDevices.FirstOrDefault(d =>
                          string.Equals(d.Id, defaultId, StringComparison.OrdinalIgnoreCase))
                      ?? previewDevices.FirstOrDefault();
        PreviewOutputCombo.SelectedItem = preview;
        _preferredPreviewDeviceId = preview?.Id;

        var streamSelectedId = (StreamTargetCombo.SelectedItem as AudioOutputDeviceInfo)?.Id
                               ?? _preferredStreamDeviceId;
        StreamTargetCombo.ItemsSource = streamDevices;
        var stream = streamDevices.FirstOrDefault(d =>
                         string.Equals(d.Id, streamSelectedId, StringComparison.OrdinalIgnoreCase))
                     ?? streamDevices.FirstOrDefault(d => AudioOutputDevices.LooksLikeVirtualMicFeed(d.FriendlyName))
                     ?? streamDevices.FirstOrDefault();
        StreamTargetCombo.SelectedItem = stream;
        _preferredStreamDeviceId = stream?.Id;
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (ClipCombo.SelectedItem is not ClipInfo clip)
        {
            SetStatus("Select a playback clip first.", isError: true);
            return;
        }

        PreviewClip(clip.Path, clip.Duration, clip.FileName);
    }

    private void Stream_Click(object sender, RoutedEventArgs e)
    {
        if (ClipCombo.SelectedItem is not ClipInfo clip)
        {
            SetStatus("Select a playback clip first.", isError: true);
            return;
        }

        StreamClip(clip.Path, clip.Duration, clip.FileName);
    }

    private void PreviewClip(string path, TimeSpan duration, string fileName)
    {
        if (PreviewOutputCombo.SelectedItem is not AudioOutputDeviceInfo device)
        {
            SetStatus("Pick a preview output device.", isError: true);
            return;
        }

        _preferredPreviewDeviceId = device.Id;
        PersistPreferences();
        StartPlayback(path, duration, device.Id, null, $"Preview → {device.FriendlyName}", fileName);
    }

    private void StreamClip(string path, TimeSpan duration, string fileName)
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
        PersistPreferences();
        StartPlayback(path, duration, device.Id, localId, $"Stream → {device.FriendlyName}{localNote}", fileName);
    }

    private void StartPlayback(
        string path,
        TimeSpan duration,
        string primaryDeviceId,
        string? secondaryDeviceId,
        string status,
        string fileName)
    {
        if (!_initialized)
        {
            SetStatus("Playback panel not ready.", isError: true);
            return;
        }

        try
        {
            var start = TimeSpan.Zero;
            var end = duration;
            if (end <= start)
            {
                SetStatus("Clip has no audio duration.", isError: true);
                return;
            }

            _stopEditPreview?.Invoke();
            _externalProgressActive = false;
            _clipDuration = end;
            // Preview path passes secondaryDeviceId=null; stream may add local-hear only.
            _player.PlayRange(path, start, end, primaryDeviceId, secondaryDeviceId);
            _playbackTimer.Start();
            PreviewButton.Content = "Playing…";
            SetStatus($"{status} · {fileName} ({end.TotalSeconds:0.##}s)");
        }
        catch (Exception ex)
        {
            SetStatus($"Playback failed: {ex.Message}", isError: true);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _stopEditPreview?.Invoke();
        StopPlayback();
    }

    private void OnPlaybackStoppedUi()
    {
        _playbackTimer.Stop();
        PreviewButton.Content = "Preview";
        if (_externalProgressActive)
        {
            return;
        }

        PlaybackProgress.Value = 0;
        PlaybackLabel.Text = "Stopped";
    }

    private void UpdatePlaybackUi()
    {
        if (_externalProgressActive || _player.State != PlaybackState.Playing)
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
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("MutedBrush");
    }
}
