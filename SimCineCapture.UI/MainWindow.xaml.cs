using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimCineCapture.Core.Abstractions;
using SimCineCapture.Core.Enums;
using SimCineCapture.Core.Models;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using System.Diagnostics;
using System.Windows.Threading;
using System.Windows.Media.Imaging;

namespace SimCineCapture.UI;

public partial class MainWindow : Window
{
    private const int WmUserSimConnect = 0x0402;

    private readonly ISimConnectService _simConnectService;
    private readonly ILogger<MainWindow> _logger;
    private readonly IOptionsMonitor<AppSettings> _settingsMonitor;
    private readonly IDisposable _settingsSubscription;
    private readonly IRecorderService _recorderService;
    private readonly IAppSettingsPersistenceService _appSettingsPersistenceService;
    private readonly DispatcherTimer _captureInfoRefreshTimer;

    private HwndSource? _hwndSource;

    public MainWindow(
    ISimConnectService simConnectService,
    IRecorderService recorderService,
    IAppSettingsPersistenceService appSettingsPersistenceService,
    ILogger<MainWindow> logger,
    IOptionsMonitor<AppSettings> settingsMonitor)
    {
        InitializeComponent();

        _simConnectService = simConnectService;
        _logger = logger;
        _settingsMonitor = settingsMonitor;

        _settingsSubscription = _settingsMonitor.OnChange(ApplySettings);
        _simConnectService.TelemetryUpdated += OnTelemetryUpdated;

        _recorderService = recorderService;
        _recorderService.RecorderStatusChanged += OnRecorderStatusChanged;

        _appSettingsPersistenceService = appSettingsPersistenceService;

        _captureInfoRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };

        _captureInfoRefreshTimer.Tick += CaptureInfoRefreshTimer_Tick;

        ApplySettings(_settingsMonitor.CurrentValue);
        UpdateSimulatorStatus(_simConnectService.GetStatus());
        UpdateTelemetryDisplay(_simConnectService.GetLatestTelemetry());

        UpdateCaptureBackendInfo();
        RecorderStateTextBlock.Text = "Idle";

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void CaptureInfoRefreshTimer_Tick(object? sender, EventArgs e)
    {
        UpdateCaptureBackendInfo();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwndSource.AddHook(WndProc);

        _simConnectService.InitializeWindowHandle(_hwndSource.Handle);

        _logger.LogInformation("WPF window source initialized.");
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Main window loaded.");

        _captureInfoRefreshTimer.Start();

        if (_settingsMonitor.CurrentValue.Simulator.AutoConnectOnStartup)
        {
            await ConnectToSimulatorAsync();
        }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _settingsSubscription.Dispose();
        _simConnectService.TelemetryUpdated -= OnTelemetryUpdated;
        _recorderService.RecorderStatusChanged -= OnRecorderStatusChanged;

        _captureInfoRefreshTimer.Stop();
        _captureInfoRefreshTimer.Tick -= CaptureInfoRefreshTimer_Tick;

        try
        {
            await _simConnectService.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while disconnecting SimConnect during window close.");
        }

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    private IntPtr WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WmUserSimConnect)
        {
            _simConnectService.ReceiveMessage();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ApplySettings(AppSettings settings)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ApplySettings(settings));
            return;
        }

        HideUiCheckBox.IsChecked = settings.Recorder.HideSimulatorUiDuringRecording;

        var expandedOutputPath = Environment.ExpandEnvironmentVariables(settings.Recorder.OutputDirectory);
        OutputDirectoryTextBox.Text = expandedOutputPath;

        RefreshCaptureOutputs(
            settings.Recorder.CaptureAdapterIndex,
            settings.Recorder.CaptureOutputIndex);

        StatusTextBlock.Text = $"Settings loaded. Output path: {expandedOutputPath}";
        _logger.LogInformation("Settings applied. Output path: {OutputPath}", expandedOutputPath);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_simConnectService.IsConnected)
        {
            await DisconnectFromSimulatorAsync();
            return;
        }

        await ConnectToSimulatorAsync();
    }

    private async Task ConnectToSimulatorAsync()
    {
        try
        {
            ConnectButton.IsEnabled = false;
            StatusTextBlock.Text = "Attempting simulator connection...";

            _logger.LogInformation("Connecting to simulator...");

            var status = await _simConnectService.ConnectAsync();

            UpdateSimulatorStatus(status);

            if (status.IsConnected)
            {
                ConnectButton.Content = "Disconnect from MSFS";
                StartRecordingButton.IsEnabled = true;
                StopRecordingButton.IsEnabled = false;

                StatusTextBlock.Text = status.Message;
                _logger.LogInformation("Simulator connected.");
                UpdateRecorderDisplay(_recorderService.GetStatus());
                UpdateCaptureBackendInfo();
            }
            else
            {
                ConnectButton.Content = "Connect to MSFS";
                StartRecordingButton.IsEnabled = false;
                StopRecordingButton.IsEnabled = false;

                StatusTextBlock.Text = status.Message;
                _logger.LogWarning("Simulator connection did not succeed.");
            }
        }
        catch (Exception ex)
        {
            StartRecordingButton.IsEnabled = false;
            StopRecordingButton.IsEnabled = false;
            ConnectButton.Content = "Connect to MSFS";

            UpdateSimulatorStatus(new SimulatorConnectionStatus
            {
                State = SimulatorConnectionState.Error,
                Message = $"Connection error: {ex.Message}"
            });

            StatusTextBlock.Text = $"Connection error: {ex.Message}";
            _logger.LogError(ex, "Connection to simulator failed.");
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async Task DisconnectFromSimulatorAsync()
    {
        try
        {
            ConnectButton.IsEnabled = false;
            StatusTextBlock.Text = "Disconnecting from simulator...";

            _logger.LogInformation("Disconnecting from simulator...");

            await _simConnectService.DisconnectAsync();

            var status = _simConnectService.GetStatus();
            UpdateSimulatorStatus(status);
            UpdateTelemetryDisplay(_simConnectService.GetLatestTelemetry());

            UpdateCaptureBackendInfo();

            ConnectButton.Content = "Connect to MSFS";
            StartRecordingButton.IsEnabled = false;
            StopRecordingButton.IsEnabled = false;

            RecorderStateTextBlock.Text = "Idle";
            RecorderStateTextBlock.Foreground = CreateBrush("#E5C07B");

            StatusTextBlock.Text = status.Message;
            _logger.LogInformation("Simulator disconnected.");
            UpdateRecorderDisplay(_recorderService.GetStatus());
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Disconnect error: {ex.Message}";
            _logger.LogError(ex, "Disconnect from simulator failed.");
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async void StartRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartRecordingButton.IsEnabled = false;

            var outputDirectory = OutputDirectoryTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                StatusTextBlock.Text = "Please define a valid output directory before starting the recording.";
                UpdateRecorderDisplay(new RecorderStatus
                {
                    State = RecorderState.Error,
                    Message = "No output directory defined."
                });
                return;
            }

            if (CaptureOutputComboBox.SelectedItem is not CaptureOutputInfo selectedOutput)
            {
                StatusTextBlock.Text = "Please select a valid capture output before starting the recording.";
                UpdateRecorderDisplay(new RecorderStatus
                {
                    State = RecorderState.Error,
                    Message = "No capture output selected."
                });
                return;
            }

            var telemetry = _simConnectService.GetLatestTelemetry();

            var request = new RecordingStartRequest
            {
                OutputDirectory = outputDirectory,
                HideSimulatorUi = HideUiCheckBox.IsChecked == true,
                FileNamePrefix = "SimCineCapture",
                AircraftTitle = telemetry.AircraftTitle,
                CaptureAdapterIndex = selectedOutput.AdapterIndex,
                CaptureOutputIndex = selectedOutput.OutputIndex,
                TargetFrameRate = Math.Max(1, _settingsMonitor.CurrentValue.Recorder.TargetFrameRate)
            };

            var status = await _recorderService.StartAsync(request);

            UpdateRecorderDisplay(status);

            _logger.LogInformation(
                "Recorder start requested. State: {State}, Output: {OutputFilePath}",
                status.State,
                status.OutputFilePath);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Recorder start error: {ex.Message}";
            _logger.LogError(ex, "Failed to start recorder.");
        }
    }

    private async void StopRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StopRecordingButton.IsEnabled = false;

            var status = await _recorderService.StopAsync();

            UpdateRecorderDisplay(status);

            _logger.LogInformation(
                "Recorder stop requested. State: {State}, Output: {OutputFilePath}",
                status.State,
                status.OutputFilePath);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Recorder stop error: {ex.Message}";
            _logger.LogError(ex, "Failed to stop recorder.");
        }
    }

    private async void TakeSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TakeSnapshotButton.IsEnabled = false;

            var outputDirectory = OutputDirectoryTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                StatusTextBlock.Text = "Please define a valid output directory before saving a snapshot.";
                return;
            }

            if (CaptureOutputComboBox.SelectedItem is not CaptureOutputInfo selectedOutput)
            {
                StatusTextBlock.Text = "Please select a valid capture output before saving a snapshot.";
                return;
            }

            Directory.CreateDirectory(outputDirectory);

            StatusTextBlock.Text = "Capturing snapshot...";

            var frame = await _recorderService.CaptureSnapshotAsync(new CaptureSnapshotRequest
            {
                CaptureAdapterIndex = selectedOutput.AdapterIndex,
                CaptureOutputIndex = selectedOutput.OutputIndex
            });

            var outputFilePath = BuildSnapshotOutputFilePath(
                outputDirectory,
                _simConnectService.GetLatestTelemetry().AircraftTitle,
                frame.CapturedAtUtc);

            await SaveCapturedFrameAsPngAsync(frame, outputFilePath);

            StatusTextBlock.Text = $"Snapshot saved: {outputFilePath}";
            _logger.LogInformation("Snapshot saved: {OutputFilePath}", outputFilePath);

            UpdateCaptureBackendInfo();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Snapshot error: {ex.Message}";
            _logger.LogError(ex, "Failed to capture snapshot.");
        }
        finally
        {
            UpdateRecordingInputLocks(_recorderService.GetStatus());
        }
    }

    private static async Task SaveCapturedFrameAsPngAsync(CapturedFrame frame, string outputFilePath)
    {
        await Task.Run(() =>
        {
            var outputDirectory = Path.GetDirectoryName(outputFilePath);

            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var bitmapSource = BitmapSource.Create(
                frame.Width,
                frame.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                frame.PixelData,
                frame.Stride);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

            using var stream = new FileStream(
                outputFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);

            encoder.Save(stream);
        });
    }

    private static string BuildSnapshotOutputFilePath(
        string outputDirectory,
        string? aircraftTitle,
        DateTimeOffset capturedAtUtc)
    {
        var safeAircraftName = SanitizePathPart(aircraftTitle);
        var timestamp = capturedAtUtc.LocalDateTime.ToString("yyyy-MM-dd_HH-mm-ss");

        var fileName = string.IsNullOrWhiteSpace(safeAircraftName)
            ? $"SimCineCapture_Snapshot_{timestamp}.png"
            : $"SimCineCapture_Snapshot_{safeAircraftName}_{timestamp}.png";

        return Path.Combine(outputDirectory, fileName);
    }

    private static string SanitizePathPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray());

        return sanitized.Trim();
    }

    private void OnTelemetryUpdated(SimulatorTelemetry telemetry)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateTelemetryDisplay(telemetry);
        });
    }

    private void UpdateTelemetryDisplay(SimulatorTelemetry telemetry)
    {
        AircraftTitleValueTextBlock.Text = string.IsNullOrWhiteSpace(telemetry.AircraftTitle)
            ? "No telemetry yet"
            : telemetry.AircraftTitle;

        if (string.IsNullOrWhiteSpace(telemetry.AircraftTitle) || telemetry.TimestampUtc == default)
        {
            PositionValueTextBlock.Text = "Lat -- | Lon -- | Alt --";
            FlightStateValueTextBlock.Text = "No data";
            return;
        }

        PositionValueTextBlock.Text =
            $"Lat {telemetry.Latitude:F5} | Lon {telemetry.Longitude:F5} | Alt {telemetry.AltitudeFeet:F0} ft";

        FlightStateValueTextBlock.Text =
            $"{(telemetry.IsOnGround ? "On Ground" : "Airborne")} | GS {telemetry.GroundSpeedKnots:F1} kt";
    }

    private void UpdateSimulatorStatus(SimulatorConnectionStatus status)
    {
        MsfsConnectionStateTextBlock.Text = status.State.ToString();

        MsfsConnectionStateTextBlock.Foreground = status.State switch
        {
            SimulatorConnectionState.Connected => CreateBrush("#98C379"),
            SimulatorConnectionState.Connecting => CreateBrush("#61AFEF"),
            SimulatorConnectionState.Error => CreateBrush("#E06C75"),
            _ => CreateBrush("#E07A7A")
        };
    }

    private static SolidColorBrush CreateBrush(string hexColor)
    {
        return (SolidColorBrush)new BrushConverter().ConvertFromString(hexColor)!;
    }

    private void OnRecorderStatusChanged(RecorderStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateRecorderDisplay(status);
        });
    }

    private void UpdateRecorderDisplay(RecorderStatus status)
    {
        RecorderStateTextBlock.Text = status.State.ToString();

        RecorderStateTextBlock.Foreground = status.State switch
        {
            RecorderState.Recording => CreateBrush("#98C379"),
            RecorderState.Starting => CreateBrush("#61AFEF"),
            RecorderState.Stopping => CreateBrush("#E5C07B"),
            RecorderState.Error => CreateBrush("#E06C75"),
            _ => CreateBrush("#E5C07B")
        };

        StartRecordingButton.IsEnabled =
            _simConnectService.IsConnected &&
            status.State is RecorderState.Idle or RecorderState.Error;

        StopRecordingButton.IsEnabled =
            status.State is RecorderState.Recording or RecorderState.Starting or RecorderState.Stopping;

        UpdateRecordingInputLocks(status);

        StatusTextBlock.Text = status.OutputFilePath is null
            ? status.Message
            : $"{status.Message} Output: {status.OutputFilePath}";
        UpdateCaptureBackendInfo();
    }

    private void BrowseOutputDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select the output folder for recordings.",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(OutputDirectoryTextBox.Text)
                ? OutputDirectoryTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            OutputDirectoryTextBox.Text = dialog.SelectedPath;
            StatusTextBlock.Text = $"Output directory set to: {dialog.SelectedPath}";
            _logger.LogInformation("Output directory changed to: {OutputDirectory}", dialog.SelectedPath);
        }
    }

    private void UpdateRecordingInputLocks(RecorderStatus status)
    {
        var isLocked = status.State is RecorderState.Starting
            or RecorderState.Recording
            or RecorderState.Stopping;

        HideUiCheckBox.IsEnabled = !isLocked;
        CaptureOutputComboBox.IsEnabled = !isLocked;
        RefreshCaptureOutputsButton.IsEnabled = !isLocked;
        OutputDirectoryTextBox.IsEnabled = !isLocked;
        BrowseOutputDirectoryButton.IsEnabled = !isLocked;
        SaveOutputSettingsButton.IsEnabled = !isLocked;
        TakeSnapshotButton.IsEnabled = !isLocked;

        OpenOutputDirectoryButton.IsEnabled = true;
    }

    private async void SaveOutputSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var outputDirectory = OutputDirectoryTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                StatusTextBlock.Text = "Please enter a valid output directory before saving.";
                return;
            }

            if (CaptureOutputComboBox.SelectedItem is not CaptureOutputInfo selectedOutput)
            {
                StatusTextBlock.Text = "Please select a valid capture output before saving.";
                return;
            }

            await _appSettingsPersistenceService.SaveRecorderSettingsAsync(
                outputDirectory,
                HideUiCheckBox.IsChecked == true,
                selectedOutput.AdapterIndex,
                selectedOutput.OutputIndex);

            StatusTextBlock.Text = $"Recorder settings saved to: {_appSettingsPersistenceService.GetConfigFilePath()}";
            _logger.LogInformation("Recorder settings saved.");
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Failed to save recorder settings: {ex.Message}";
            _logger.LogError(ex, "Failed to save recorder settings.");
        }
    }

    private void OpenOutputDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var outputDirectory = OutputDirectoryTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                StatusTextBlock.Text = "Please define an output directory first.";
                return;
            }

            Directory.CreateDirectory(outputDirectory);

            Process.Start(new ProcessStartInfo
            {
                FileName = outputDirectory,
                UseShellExecute = true
            });

            StatusTextBlock.Text = $"Opened output directory: {outputDirectory}";
            _logger.LogInformation("Opened output directory: {OutputDirectory}", outputDirectory);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Failed to open output directory: {ex.Message}";
            _logger.LogError(ex, "Failed to open output directory.");
        }
    }
    private void UpdateCaptureBackendInfo()
    {
        var info = _recorderService.GetBackendInfo();

        CaptureBackendStateTextBlock.Text = info.BackendName;

        CaptureDeviceValueTextBlock.Text = info.IsInitialized
            ? $"{info.AdapterName} | {info.OutputName} | Frames {info.CapturedFrameCount}"
            : info.Message;
    }
    private void RefreshCaptureOutputs(int preferredAdapterIndex, int preferredOutputIndex)
    {
        var outputs = _recorderService.GetAvailableCaptureOutputs();

        CaptureOutputComboBox.ItemsSource = outputs;

        if (outputs.Count == 0)
        {
            CaptureOutputComboBox.SelectedItem = null;
            return;
        }

        var preferred = outputs.FirstOrDefault(x =>
            x.AdapterIndex == preferredAdapterIndex &&
            x.OutputIndex == preferredOutputIndex);

        CaptureOutputComboBox.SelectedItem = preferred ?? outputs[0];
    }
    private void RefreshCaptureOutputsButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = _settingsMonitor.CurrentValue;
        RefreshCaptureOutputs(settings.Recorder.CaptureAdapterIndex, settings.Recorder.CaptureOutputIndex);
        StatusTextBlock.Text = "Capture outputs refreshed.";
    }
}