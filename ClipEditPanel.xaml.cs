using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.Wave;
using RetroClip.Services;

namespace RetroClip;

public partial class ClipEditPanel : UserControl
{
    private readonly ClipPlayer _previewPlayer = new();
    private readonly DispatcherTimer _previewTimer;
    private string _clipsDirectory = string.Empty;
    private IReadOnlyList<ClipInfo> _allClips = Array.Empty<ClipInfo>();
    private bool _updatingSliders;
    private bool _suppressSelectionHandler;
    private string? _preferredPath;
    private string? _preferredPreviewDeviceId;
    private bool _initialized;
    private bool _suppressPreferenceSave;
    private Action? _stopPlayback;
    private Action<TimeSpan, TimeSpan, string>? _reportTransportProgress;
    private Action? _clearTransportProgress;
    private UiPreferencesStore? _preferences;
    private TimeSpan _previewStart;
    private TimeSpan _previewDuration;

    private ClipInfo? SelectedClip => ClipCombo.SelectedItem as ClipInfo;

    public ClipEditPanel()
    {
        InitializeComponent();

        _previewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _previewTimer.Tick += (_, _) => UpdatePreviewTransportProgress();
        _previewPlayer.PlaybackStopped += (_, _) => Dispatcher.BeginInvoke(OnPreviewStoppedUi);

        Unloaded += (_, _) =>
        {
            _previewTimer.Stop();
            _previewPlayer.Dispose();
        };
    }

    public void Initialize(
        string clipsDirectory,
        UiPreferencesStore preferences,
        Action? stopPlayback = null,
        Action<TimeSpan, TimeSpan, string>? reportTransportProgress = null,
        Action? clearTransportProgress = null)
    {
        _clipsDirectory = clipsDirectory;
        _preferences = preferences;
        _stopPlayback = stopPlayback;
        _reportTransportProgress = reportTransportProgress;
        _clearTransportProgress = clearTransportProgress;
        Directory.CreateDirectory(_clipsDirectory);
        _preferredPreviewDeviceId = preferences.Current.EditPreviewDeviceId;
        _preferredPath = preferences.Current.EditClipPath;
        _initialized = true;
        _suppressPreferenceSave = true;
        try
        {
            RefreshPreviewDevices();
            RefreshClips(forceSelectPreferred: true);
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

        _preferences.Current.EditPreviewDeviceId =
            (PreviewOutputCombo.SelectedItem as AudioOutputDeviceInfo)?.Id ?? _preferredPreviewDeviceId;
        _preferences.Current.EditClipPath = SelectedClip?.Path ?? _preferredPath;
        _preferences.Save();
    }

    public void StopPreview()
    {
        _previewPlayer.Stop();
        OnPreviewStoppedUi();
    }

    private void ClipSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        ApplyClipFilter(preserveSelectedPath: SelectedClip?.Path);
    }

    private void ApplyClipFilter(string? preserveSelectedPath)
    {
        var filtered = ClipSearch.Filter(_allClips, ClipSearchBox.Text);
        var selected = ResolveClip(filtered, preserveSelectedPath)
                       ?? ResolveClip(_allClips, preserveSelectedPath);
        if (selected is not null && ResolveClip(filtered, selected.Path) is null)
        {
            // Keep current selection visible even if it falls outside the filter.
            filtered = filtered.Append(selected).ToList();
        }

        selected = ResolveClip(filtered, preserveSelectedPath) ?? filtered.FirstOrDefault();

        _suppressSelectionHandler = selected is not null
                                    && SelectedClip is not null
                                    && string.Equals(selected.Path, SelectedClip.Path, StringComparison.OrdinalIgnoreCase);
        try
        {
            ClipCombo.ItemsSource = filtered;
            ClipCombo.SelectedItem = selected;
        }
        finally
        {
            _suppressSelectionHandler = false;
        }
    }

    public void NotifyClipSaved(string path)
    {
        if (!_initialized)
        {
            return;
        }

        var currentPath = SelectedClip?.Path;
        RefreshClips(preservePath: currentPath, forceSelectPreferred: false);
        SetStatus($"New clip saved: {Path.GetFileName(path)}. Still editing current selection.");
    }

    private void RefreshClips_Click(object sender, RoutedEventArgs e)
    {
        var currentPath = SelectedClip?.Path;
        RefreshClips(preservePath: currentPath, forceSelectPreferred: false);
        SetStatus($"Loaded {ClipLibrary.ListClips(_clipsDirectory).Count} clip(s).");
    }

    private void RefreshClips(string? preservePath = null, bool forceSelectPreferred = false)
    {
        _allClips = ClipLibrary.ListClips(_clipsDirectory);
        var previousPath = SelectedClip?.Path;

        ClipInfo? selected = null;
        if (forceSelectPreferred && !string.IsNullOrWhiteSpace(_preferredPath))
        {
            selected = _allClips.FirstOrDefault(c =>
                string.Equals(c.Path, _preferredPath, StringComparison.OrdinalIgnoreCase));
        }

        selected ??= ResolveClip(_allClips, preservePath)
                     ?? ResolveClip(_allClips, previousPath)
                     ?? _allClips.FirstOrDefault();

        _preferredPath = null;
        ApplyClipFilter(preserveSelectedPath: selected?.Path);

        if (_allClips.Count == 0)
        {
            ResetEditor();
            SetStatus("No WAV clips found yet. Save a clip from Capture.");
        }
        else if (forceSelectPreferred)
        {
            SetStatus($"Loaded {_allClips.Count} clip(s).");
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

    private void PreviewOutputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPreferenceSave || !_initialized)
        {
            return;
        }

        if (PreviewOutputCombo.SelectedItem is AudioOutputDeviceInfo device)
        {
            _preferredPreviewDeviceId = device.Id;
        }

        PersistPreferences();
    }

    private void ClipCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionHandler)
        {
            return;
        }

        _stopPlayback?.Invoke();
        StopPreview();

        if (SelectedClip is not ClipInfo clip)
        {
            ResetEditor();
            return;
        }

        _updatingSliders = true;
        try
        {
            var seconds = Math.Max(clip.Duration.TotalSeconds, 0.01);
            StartSlider.Minimum = 0;
            StartSlider.Maximum = seconds;
            EndSlider.Minimum = 0;
            EndSlider.Maximum = seconds;
            StartSlider.Value = 0;
            EndSlider.Value = seconds;
        }
        finally
        {
            _updatingSliders = false;
        }

        SaveNameBox.Text = Path.GetFileNameWithoutExtension(clip.FileName) + "_trim";
        UpdateSelectionLabels();
        SetStatus($"Ready: {clip.FileName} ({clip.Duration.TotalSeconds:0.##}s)");
        PersistPreferences();
    }

    private void TrimSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSliders || !_initialized)
        {
            return;
        }

        _updatingSliders = true;
        try
        {
            const double minSpan = 0.01;
            if (ReferenceEquals(sender, StartSlider) && StartSlider.Value > EndSlider.Value - minSpan)
            {
                StartSlider.Value = Math.Max(0, EndSlider.Value - minSpan);
            }
            else if (ReferenceEquals(sender, EndSlider) && EndSlider.Value < StartSlider.Value + minSpan)
            {
                EndSlider.Value = Math.Min(EndSlider.Maximum, StartSlider.Value + minSpan);
            }
        }
        finally
        {
            _updatingSliders = false;
        }

        UpdateSelectionLabels();
    }

    private void StartSecondsBox_LostFocus(object sender, RoutedEventArgs e) => ApplyTrimTextBox(StartSecondsBox, isStart: true);

    private void EndSecondsBox_LostFocus(object sender, RoutedEventArgs e) => ApplyTrimTextBox(EndSecondsBox, isStart: false);

    private void TrimSecondsBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box)
        {
            return;
        }

        ApplyTrimTextBox(box, isStart: ReferenceEquals(box, StartSecondsBox));
        e.Handled = true;
    }

    private void ApplyTrimTextBox(TextBox box, bool isStart)
    {
        if (!_initialized)
        {
            return;
        }

        if (!TryParseSeconds(box.Text, out var seconds))
        {
            UpdateSelectionLabels();
            SetStatus("Enter trim time like 5.27", isError: true);
            return;
        }

        const double minSpan = 0.01;
        _updatingSliders = true;
        try
        {
            if (isStart)
            {
                seconds = Math.Clamp(seconds, 0, Math.Max(0, EndSlider.Value - minSpan));
                StartSlider.Value = seconds;
            }
            else
            {
                seconds = Math.Clamp(seconds, Math.Min(StartSlider.Value + minSpan, EndSlider.Maximum), EndSlider.Maximum);
                EndSlider.Value = seconds;
            }
        }
        finally
        {
            _updatingSliders = false;
        }

        UpdateSelectionLabels();
    }

    private static bool TryParseSeconds(string? text, out double seconds)
    {
        return double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
               || double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out seconds);
    }

    private void UpdateSelectionLabels()
    {
        StartSecondsBox.Text = $"{StartSlider.Value:0.##}";
        EndSecondsBox.Text = $"{EndSlider.Value:0.##}";
        SelectionLabel.Text = $"{Math.Max(0, EndSlider.Value - StartSlider.Value):0.##}s selected";
    }

    private void RefreshPreviewDevices()
    {
        var devices = AudioOutputDevices.ListActiveRenderDevices();
        var defaultId = AudioOutputDevices.GetDefaultRenderDeviceId();
        var selectedId = (PreviewOutputCombo.SelectedItem as AudioOutputDeviceInfo)?.Id
                         ?? _preferredPreviewDeviceId
                         ?? defaultId;

        PreviewOutputCombo.ItemsSource = devices;
        var preferred = devices.FirstOrDefault(d =>
                            string.Equals(d.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                        ?? devices.FirstOrDefault(d =>
                            string.Equals(d.Id, defaultId, StringComparison.OrdinalIgnoreCase))
                        ?? devices.FirstOrDefault();
        PreviewOutputCombo.SelectedItem = preferred;
        _preferredPreviewDeviceId = preferred?.Id;
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedClip is not ClipInfo clip)
        {
            SetStatus("Select a clip first.", isError: true);
            return;
        }

        if (PreviewOutputCombo.SelectedItem is not AudioOutputDeviceInfo device)
        {
            SetStatus("Pick a preview output device.", isError: true);
            return;
        }

        var start = TimeSpan.FromSeconds(StartSlider.Value);
        var end = TimeSpan.FromSeconds(EndSlider.Value);
        if (end <= start)
        {
            SetStatus("Trim end must be after trim start.", isError: true);
            return;
        }

        try
        {
            _stopPlayback?.Invoke();
            _preferredPreviewDeviceId = device.Id;
            PersistPreferences();
            // Preview only — never pass a stream/secondary device.
            _previewStart = start;
            _previewDuration = end - start;
            _previewPlayer.PlayRange(clip.Path, start, end, device.Id, secondaryOutputDeviceId: null);
            _previewTimer.Start();
            PreviewButton.Content = "Playing…";
            UpdatePreviewTransportProgress();
            SetStatus($"Preview trim → {device.FriendlyName} ({_previewDuration.TotalSeconds:0.##}s)");
        }
        catch (Exception ex)
        {
            SetStatus($"Preview failed: {ex.Message}", isError: true);
        }
    }

    private void StopPreview_Click(object sender, RoutedEventArgs e) => StopPreview();

    private void UpdatePreviewTransportProgress()
    {
        if (_previewPlayer.State != PlaybackState.Playing)
        {
            OnPreviewStoppedUi();
            return;
        }

        var elapsed = _previewPlayer.CurrentTime - _previewStart;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed > _previewDuration)
        {
            elapsed = _previewDuration;
        }

        _reportTransportProgress?.Invoke(elapsed, _previewDuration, "Edit preview");
    }

    private void OnPreviewStoppedUi()
    {
        _previewTimer.Stop();
        PreviewButton.Content = "Preview";
        _clearTransportProgress?.Invoke();
    }

    private void DeleteClip_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedClip is not ClipInfo clip)
        {
            SetStatus("Select a clip first.", isError: true);
            return;
        }

        var owner = Window.GetWindow(this);
        var confirm = MessageBox.Show(
            owner,
            $"Delete {clip.FileName} permanently?",
            "Delete clip",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _stopPlayback?.Invoke();
            StopPreview();

            if (File.Exists(clip.Path))
            {
                File.SetAttributes(clip.Path, FileAttributes.Normal);
                File.Delete(clip.Path);
            }

            if (File.Exists(clip.Path))
            {
                throw new IOException($"Could not delete {clip.FileName}");
            }

            RefreshClips(forceSelectPreferred: true);
            SetStatus($"Deleted {clip.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus($"Delete failed: {ex.Message}", isError: true);
        }
    }

    private void SaveTrimmed_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedClip is not ClipInfo clip)
        {
            SetStatus("Select a clip first.", isError: true);
            return;
        }

        var saveName = SaveNameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(saveName))
        {
            SetStatus("Enter a file name before saving.", isError: true);
            return;
        }

        var outputPath = ClipTrimmer.BuildOutputPath(_clipsDirectory, saveName);
        var sameAsSource = string.Equals(
            Path.GetFullPath(outputPath),
            Path.GetFullPath(clip.Path),
            StringComparison.OrdinalIgnoreCase);
        var deleteOriginal = DeleteOriginalCheck.IsChecked == true;
        var owner = Window.GetWindow(this);

        if (sameAsSource)
        {
            var confirm = MessageBox.Show(
                owner,
                $"Save as replaces the original file:\n{clip.FileName}\n\nContinue?",
                "Replace original",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }
        else if (File.Exists(outputPath))
        {
            var confirm = MessageBox.Show(
                owner,
                $"A file named {Path.GetFileName(outputPath)} already exists. Overwrite it?",
                "File exists",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            _stopPlayback?.Invoke();
            StopPreview();

            var start = TimeSpan.FromSeconds(StartSlider.Value);
            var end = TimeSpan.FromSeconds(EndSlider.Value);
            var sourcePath = clip.Path;
            var path = ClipTrimmer.SaveTrimmed(
                sourcePath,
                start,
                end,
                outputPath: outputPath,
                overwriteSource: sameAsSource,
                deleteOriginal: deleteOriginal && !sameAsSource);

            if (deleteOriginal && !sameAsSource && File.Exists(sourcePath))
            {
                throw new IOException($"Original file still exists after save: {Path.GetFileName(sourcePath)}");
            }

            _preferredPath = path;
            RefreshClips(forceSelectPreferred: true);

            var deletedNote = deleteOriginal && !sameAsSource ? " · original deleted" : string.Empty;
            SetStatus($"Saved {Path.GetFileName(path)}{deletedNote}");
        }
        catch (Exception ex)
        {
            SetStatus($"Trim failed: {ex.Message}", isError: true);
        }
    }

    private void ResetEditor()
    {
        _updatingSliders = true;
        try
        {
            StartSlider.Minimum = 0;
            StartSlider.Maximum = 1;
            EndSlider.Minimum = 0;
            EndSlider.Maximum = 1;
            StartSlider.Value = 0;
            EndSlider.Value = 1;
        }
        finally
        {
            _updatingSliders = false;
        }

        SaveNameBox.Text = string.Empty;
        UpdateSelectionLabels();
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }
}
