using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RetroClip.Services;

namespace RetroClip;

public partial class MainWindow : Window
{
    private readonly RetroCaptureService _capture = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly UiPreferencesStore _preferences = new();
    private HotkeyService? _hotkey;
    private readonly string _clipsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "RetroClip");
    private AudioAppInfo? _activeApp;
    private bool _starting;
    private bool _suppressPreferenceSave;

    public MainWindow()
    {
        InitializeComponent();

        _uiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _uiTimer.Tick += (_, _) => UpdateBufferUi();
        _capture.BufferProgressChanged += (_, _) => Dispatcher.BeginInvoke(UpdateBufferUi);
        _capture.CaptureFaulted += (_, ex) => Dispatcher.BeginInvoke(() => OnCaptureFaulted(ex));

        Loaded += OnLoaded;
        Closed += OnClosed;

        _suppressPreferenceSave = true;
        BufferSlider.Value = _preferences.Current.BufferSeconds
            is int savedBuffer
            && savedBuffer >= RetroCaptureService.MinBufferSeconds
            && savedBuffer <= RetroCaptureService.MaxBufferSeconds
            ? savedBuffer
            : RetroCaptureService.MaxBufferSeconds;
        SyncBufferSecondsBox();
        _suppressPreferenceSave = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_clipsDirectory);
        PlaybackPanel.Initialize(_clipsDirectory, _preferences, EditPanel.StopPreview);
        EditPanel.Initialize(_clipsDirectory, _preferences, PlaybackPanel.StopPlayback);
        RefreshApps();
        ApplyBufferSeconds((int)Math.Round(BufferSlider.Value));
        PersistCapturePreferences();

        try
        {
            _hotkey = new HotkeyService(this);
            _hotkey.HotkeyPressed += () => Dispatcher.BeginInvoke(SaveClip);
            _hotkey.Register();
        }
        catch (Exception ex)
        {
            SetStatus($"Hotkey unavailable: {ex.Message}", isError: true);
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _uiTimer.Stop();
        PersistCapturePreferences();
        EditPanel.PersistPreferences();
        PlaybackPanel.PersistPreferences();
        _hotkey?.Dispose();
        await _capture.DisposeAsync();
    }

    private void PersistCapturePreferences()
    {
        if (_suppressPreferenceSave)
        {
            return;
        }

        _preferences.Current.CaptureProcessName =
            (AppCombo.SelectedItem as AudioAppInfo)?.ProcessName;
        _preferences.Current.BufferSeconds = (int)Math.Round(BufferSlider.Value);
        _preferences.Save();
    }

    private void RefreshApps_Click(object sender, RoutedEventArgs e) => RefreshApps();

    private void AppCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        PersistCapturePreferences();
    }

    private void RefreshApps()
    {
        var selectedId = (AppCombo.SelectedItem as AudioAppInfo)?.ProcessId;
        var preferredName = _preferences.Current.CaptureProcessName;
        var apps = AudioAppEnumerator.ListActiveRenderApps();
        AppCombo.ItemsSource = apps;

        if (selectedId is uint id)
        {
            AppCombo.SelectedItem = apps.FirstOrDefault(a => a.ProcessId == id);
        }

        if (AppCombo.SelectedItem is null
            && !string.IsNullOrWhiteSpace(preferredName))
        {
            AppCombo.SelectedItem = apps.FirstOrDefault(a =>
                string.Equals(a.ProcessName, preferredName, StringComparison.OrdinalIgnoreCase));
        }

        if (AppCombo.SelectedItem is null && apps.Count > 0)
        {
            AppCombo.SelectedIndex = 0;
        }

        PersistCapturePreferences();

        SetStatus(apps.Count == 0
            ? "No apps with active audio sessions found. Start playback in Discord/WoW/etc., then refresh."
            : $"Found {apps.Count} app(s) with audio sessions.");
    }

    private void BufferSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
        {
            return;
        }

        SyncBufferSecondsBox();
        ApplyBufferSeconds((int)Math.Round(BufferSlider.Value));
    }

    private void BufferSecondsBox_LostFocus(object sender, RoutedEventArgs e) => ApplyBufferSecondsFromBox();

    private void BufferSecondsBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        ApplyBufferSecondsFromBox();
        e.Handled = true;
    }

    private void ApplyBufferSecondsFromBox()
    {
        if (!int.TryParse(BufferSecondsBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && !int.TryParse(BufferSecondsBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out seconds))
        {
            SyncBufferSecondsBox();
            SetStatus($"Buffer must be {RetroCaptureService.MinBufferSeconds}–{RetroCaptureService.MaxBufferSeconds} seconds.", isError: true);
            return;
        }

        seconds = Math.Clamp(seconds, RetroCaptureService.MinBufferSeconds, RetroCaptureService.MaxBufferSeconds);
        BufferSlider.Value = seconds;
        ApplyBufferSeconds(seconds);
        SyncBufferSecondsBox();
    }

    private void ApplyBufferSeconds(int seconds)
    {
        if (_capture.IsCapturing)
        {
            return;
        }

        try
        {
            _capture.BufferSeconds = seconds;
            UpdateBufferUi();
            PersistCapturePreferences();
        }
        catch (Exception ex)
        {
            SetStatus($"Buffer length: {ex.Message}", isError: true);
        }
    }

    private void SyncBufferSecondsBox()
    {
        BufferSecondsBox.Text = ((int)Math.Round(BufferSlider.Value)).ToString(CultureInfo.InvariantCulture);
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_starting)
        {
            return;
        }

        if (_capture.IsCapturing)
        {
            await StopCaptureAsync();
            return;
        }

        if (AppCombo.SelectedItem is not AudioAppInfo app)
        {
            SetStatus("Select an application first.", isError: true);
            return;
        }

        _starting = true;
        StartStopButton.IsEnabled = false;
        try
        {
            _capture.BufferSeconds = (int)BufferSlider.Value;
            SetStatus($"Starting process loopback for {app.DisplayName}…");
            await _capture.StartAsync(app.ProcessId);
            _activeApp = app;
            BufferSlider.IsEnabled = false;
            AppCombo.IsEnabled = false;
            SaveButton.IsEnabled = true;
            StartStopButton.Content = "Stop buffering";
            _uiTimer.Start();
            SetStatus($"Buffering audio from {app.ProcessName}. Press Ctrl+Shift+R to save a clip.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to start capture: {ex.Message}", isError: true);
            await _capture.StopAsync();
            ResetCaptureUi();
        }
        finally
        {
            _starting = false;
            StartStopButton.IsEnabled = true;
        }
    }

    private async Task StopCaptureAsync()
    {
        await _capture.StopAsync();
        ResetCaptureUi();
        SetStatus("Buffering stopped. The last buffer contents remain until you start again.");
        SaveButton.IsEnabled = _capture.BufferedDuration > TimeSpan.Zero;
    }

    private void ResetCaptureUi()
    {
        _uiTimer.Stop();
        _activeApp = null;
        BufferSlider.IsEnabled = true;
        AppCombo.IsEnabled = true;
        StartStopButton.Content = "Start buffering";
        UpdateBufferUi();
    }

    private void SaveClip_Click(object sender, RoutedEventArgs e) => SaveClip();

    private void SaveClip()
    {
        try
        {
            var path = _capture.SaveClip(_clipsDirectory, _activeApp?.ProcessName
                ?? (AppCombo.SelectedItem as AudioAppInfo)?.ProcessName);
            SetStatus($"Saved {Path.GetFileName(path)} ({_capture.BufferedDuration:mm\\:ss\\.f} buffered)");
            EditPanel.NotifyClipSaved(path);
            PlaybackPanel.NotifyClipSaved(path);
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}", isError: true);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_clipsDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = _clipsDirectory,
            UseShellExecute = true
        });
    }

    private void UpdateBufferUi()
    {
        var maxSeconds = _capture.IsCapturing ? _capture.BufferSeconds : (int)BufferSlider.Value;
        var buffered = _capture.BufferedDuration;
        var ratio = maxSeconds <= 0 ? 0 : Math.Clamp(buffered.TotalSeconds / maxSeconds, 0, 1);
        BufferProgress.Value = ratio;
        BufferStatusLabel.Text = $"{buffered.TotalSeconds:0.0}s / {maxSeconds}s";
        SaveButton.IsEnabled = buffered > TimeSpan.Zero;
    }

    private void OnCaptureFaulted(Exception ex)
    {
        ResetCaptureUi();
        SetStatus($"Capture stopped unexpectedly: {ex.Message}", isError: true);
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }
}
