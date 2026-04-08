using Microsoft.Extensions.Logging;
using SimCineCapture.Core.Abstractions;
using SimCineCapture.Core.Models;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;
using System;
using System.Collections.Generic;
using System.Text;
using System.IO;


namespace SimCineCapture.Capture.Services
{
    public sealed class DesktopDuplicationCaptureBackend : ICaptureBackend, IDisposable
    {
        private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
        private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);

        private readonly object _sync = new();
        private readonly ILogger<DesktopDuplicationCaptureBackend> _logger;

        private IDXGIFactory1? _factory;
        private IDXGIAdapter1? _adapter;
        private IDXGIOutput? _output;
        private IDXGIOutput1? _output1;
        private IDXGIOutputDuplication? _duplication;
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _deviceContext;

        private bool _isCapturing;
        private long _capturedFrameCount;

        private CancellationTokenSource? _captureLoopCancellationTokenSource;
        private Task? _captureLoopTask;

        private readonly IRecordingFrameSinkFactory _frameSinkFactory;
        private IRecordingFrameSink? _currentFrameSink;

        private Exception? _captureLoopFatalException;

        private CaptureStartRequest? _currentCaptureStartRequest;
        private DateTimeOffset? _lastWrittenFrameAtUtc;
        private bool _frameSinkSessionStarted;

        private ID3D11Texture2D? _sharedStagingTexture;
        private int _sharedStagingWidth;
        private int _sharedStagingHeight;
        private Format _sharedStagingFormat;

        private CaptureBackendInfo _info = new()
        {
            BackendName = "Desktop Duplication Backend",
            Message = "Not initialized."
        };

        public DesktopDuplicationCaptureBackend(
                IRecordingFrameSinkFactory frameSinkFactory,
                ILogger<DesktopDuplicationCaptureBackend> logger)
        {
            _frameSinkFactory = frameSinkFactory;
            _logger = logger;
        }

        public string BackendName => "Desktop Duplication Backend";

        public bool IsCapturing
        {
            get
            {
                lock (_sync)
                {
                    return _isCapturing;
                }
            }
        }

        public CaptureBackendInfo GetInfo()
        {
            lock (_sync)
            {
                return _info;
            }
        }

        public IReadOnlyList<CaptureOutputInfo> GetAvailableOutputs()
        {
            var outputs = new List<CaptureOutputInfo>();

            using var factory = CreateDXGIFactory1<IDXGIFactory1>();

            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                var adapterResult = factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter);

                if (adapterResult.Failure || adapter is null)
                {
                    break;
                }

                using (adapter)
                {
                    var adapterName = adapter.Description1.Description.Trim();

                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        var outputResult = adapter.EnumOutputs(outputIndex, out IDXGIOutput? output);

                        if (outputResult.Failure || output is null)
                        {
                            break;
                        }

                        using (output)
                        {
                            var outputName = output.Description.DeviceName.Trim();

                            outputs.Add(new CaptureOutputInfo
                            {
                                AdapterIndex = (int)adapterIndex,
                                OutputIndex = (int)outputIndex,
                                AdapterName = adapterName,
                                OutputName = outputName
                            });
                        }
                    }
                }
            }

            return outputs;
        }

        public async Task StartCaptureAsync(CaptureStartRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(request.OutputFilePath))
            {
                throw new InvalidOperationException("Output file path must not be empty.");
            }

            lock (_sync)
            {
                if (_isCapturing)
                {
                    throw new InvalidOperationException("Desktop duplication backend is already capturing.");
                }
            }

            var outputDirectory = Path.GetDirectoryName(request.OutputFilePath);

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new InvalidOperationException("Unable to determine output directory.");
            }

            Directory.CreateDirectory(outputDirectory);

            await Task.Run(() =>
            {
                InitializeDuplicationSession(request.CaptureAdapterIndex, request.CaptureOutputIndex);
            }, cancellationToken);

            lock (_sync)
            {
                _currentCaptureStartRequest = request;
                _lastWrittenFrameAtUtc = null;
                _frameSinkSessionStarted = false;
                _captureLoopFatalException = null;
                _currentFrameSink = _frameSinkFactory.Create();
            }

            var loopCts = new CancellationTokenSource();
            var loopTask = Task.Run(() => CaptureLoopAsync(loopCts.Token), CancellationToken.None);

            lock (_sync)
            {
                _captureLoopCancellationTokenSource = loopCts;
                _captureLoopTask = loopTask;
                _isCapturing = true;

                _info = new CaptureBackendInfo
                {
                    BackendName = _info.BackendName,
                    AdapterName = _info.AdapterName,
                    OutputName = _info.OutputName,
                    IsInitialized = _info.IsInitialized,
                    IsCapturing = true,
                    CapturedFrameCount = _info.CapturedFrameCount,
                    Message = "Capture loop running."
                };
            }

            _logger.LogInformation("Desktop duplication backend started.");
        }

        public async Task StopCaptureAsync(CancellationToken cancellationToken = default)
        {

            CancellationTokenSource? loopCts = null;
            Task? loopTask = null;
            IRecordingFrameSink? currentFrameSink = null;
            Exception? fatalLoopException = null;

            lock (_sync)
            {
                if (!_isCapturing)
                {
                    return;
                }

                loopCts = _captureLoopCancellationTokenSource;
                loopTask = _captureLoopTask;
            }

            loopCts?.Cancel();

            if (loopTask is not null)
            {
                try
                {
                    await loopTask.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Capture loop ended with an exception during stop.");
                }
            }

            if (currentFrameSink is not null)
            {
                try
                {
                    await currentFrameSink.CompleteSessionAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    UpdateBackendInfo(
                        message: $"Frame sink completion error: {ex.Message}",
                        isInitialized: true,
                        isCapturing: false);

                    throw;
                }
                finally
                {
                    if (currentFrameSink is IDisposable disposableFrameSink)
                    {
                        disposableFrameSink.Dispose();
                    }
                }
            }

            loopCts?.Dispose();

            ReleaseResources();

            lock (_sync)
            {
                _captureLoopCancellationTokenSource = null;
                _captureLoopTask = null;
                _isCapturing = false;

                currentFrameSink = _currentFrameSink;
                fatalLoopException = _captureLoopFatalException;

                _currentFrameSink = null;
                _currentCaptureStartRequest = null;
                _lastWrittenFrameAtUtc = null;
                _frameSinkSessionStarted = false;
                _captureLoopFatalException = null;

                _info = new CaptureBackendInfo
                {
                    BackendName = BackendName,
                    AdapterName = _info.AdapterName,
                    OutputName = _info.OutputName,
                    IsInitialized = false,
                    IsCapturing = false,
                    CapturedFrameCount = _capturedFrameCount,
                    Message = "Desktop duplication backend stopped."
                };
            }

            _currentCaptureStartRequest = null;
            _lastWrittenFrameAtUtc = null;
            _frameSinkSessionStarted = false;
            _logger.LogInformation("Desktop duplication backend stopped.");
        }

        public async Task<CapturedFrame> CaptureSnapshotAsync(
            CaptureSnapshotRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_isCapturing)
                {
                    throw new InvalidOperationException("Snapshots are not available while the capture loop is running.");
                }
            }

            if (request.AcquireTimeoutMilliseconds <= 0)
            {
                throw new InvalidOperationException("Acquire timeout must be greater than 0 milliseconds.");
            }

            try
            {
                return await Task.Run(() =>
                {
                    InitializeDuplicationSession(request.CaptureAdapterIndex, request.CaptureOutputIndex);
                    return CaptureSingleSnapshotInternal(request, cancellationToken);
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                UpdateBackendInfo(
                    message: $"Snapshot failed: {ex.Message}",
                    isInitialized: false,
                    isCapturing: false,
                    capturedFrameCount: 0);

                _logger.LogError(ex, "Desktop duplication snapshot capture failed.");
                throw;
            }
            finally
            {
                ReleaseResources();
            }
        }

        public void Dispose()
        {
            ReleaseResources();
            _captureLoopCancellationTokenSource?.Dispose();
        }

        private async Task CaptureLoopAsync(CancellationToken cancellationToken)
        {
            if (_duplication is null)
            {
                throw new InvalidOperationException("Desktop duplication session is not initialized.");
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                bool frameAcquired = false;
                IDXGIResource? desktopResource = null;
                ID3D11Texture2D? sourceTexture = null;
                CapturedFrame? frameToWrite = null;

                try
                {
                    var result = _duplication.AcquireNextFrame(100u, out _, out desktopResource);

                    if (result.Code == DxgiErrorWaitTimeout)
                    {
                        continue;
                    }

                    if (result.Code == DxgiErrorAccessLost)
                    {
                        UpdateBackendInfo(
                            message: "Desktop duplication access lost.",
                            isInitialized: false,
                            isCapturing: false);

                        _logger.LogWarning("Desktop duplication access lost.");
                        break;
                    }

                    if (result.Failure)
                    {
                        throw new InvalidOperationException($"AcquireNextFrame failed: {result.Code}");
                    }

                    frameAcquired = true;

                    if (!ShouldWriteNextFrame(DateTimeOffset.UtcNow))
                    {
                        continue;
                    }

                    sourceTexture = desktopResource?.QueryInterfaceOrNull<ID3D11Texture2D>();

                    if (sourceTexture is null)
                    {
                        throw new InvalidOperationException("Failed to query the duplicated frame texture.");
                    }

                    frameToWrite = CopyTextureToCapturedFrame(sourceTexture);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {

                    _captureLoopFatalException = ex;

                    UpdateBackendInfo(
                        message: $"Capture loop error: {ex.Message}",
                        isInitialized: true,
                        isCapturing: false);

                    _logger.LogError(ex, "Desktop duplication capture loop failed.");
                    break;
                }
                finally
                {
                    sourceTexture?.Dispose();
                    desktopResource?.Dispose();

                    if (frameAcquired)
                    {
                        try
                        {
                            _duplication.ReleaseFrame();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to release duplicated desktop frame.");
                        }
                    }
                }

                if (frameToWrite is not null)
                {
                    try
                    {
                        var frameSink = GetCurrentFrameSink();

                        await EnsureFrameSinkSessionStartedAsync(frameSink, frameToWrite, cancellationToken);

                        var writtenFrameNumber = Interlocked.Increment(ref _capturedFrameCount);

                        await frameSink.WriteFrameAsync(frameToWrite, writtenFrameNumber, cancellationToken);

                        UpdateBackendInfo(
                            message: $"Frames written: {writtenFrameNumber}",
                            isInitialized: true,
                            isCapturing: true,
                            capturedFrameCount: writtenFrameNumber);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {

                        _captureLoopFatalException = ex;

                        UpdateBackendInfo(
                            message: $"Frame sink error: {ex.Message}",
                            isInitialized: true,
                            isCapturing: false);

                        _logger.LogError(ex, "Frame sink processing failed.");
                        break;
                    }
                }

                await Task.Yield();
            }
        }

        private bool ShouldWriteNextFrame(DateTimeOffset nowUtc)
        {
            var targetFrameRate = _currentCaptureStartRequest?.TargetFrameRate ?? 10;

            if (targetFrameRate <= 0)
            {
                targetFrameRate = 10;
            }

            var minimumIntervalMilliseconds = Math.Max(1, 1000 / targetFrameRate);

            if (_lastWrittenFrameAtUtc.HasValue &&
                (nowUtc - _lastWrittenFrameAtUtc.Value).TotalMilliseconds < minimumIntervalMilliseconds)
            {
                return false;
            }

            _lastWrittenFrameAtUtc = nowUtc;
            return true;
        }

        private async Task EnsureFrameSinkSessionStartedAsync(
    IRecordingFrameSink frameSink,
    CapturedFrame frame,
    CancellationToken cancellationToken)
        {
            if (_frameSinkSessionStarted)
            {
                return;
            }

            var currentRequest = _currentCaptureStartRequest
                ?? throw new InvalidOperationException("Capture start request is not available.");

            await frameSink.StartSessionAsync(new FrameSinkStartRequest
            {
                OutputPath = currentRequest.OutputFilePath,
                Width = frame.Width,
                Height = frame.Height,
                Stride = frame.Stride,
                TargetFrameRate = currentRequest.TargetFrameRate
            }, cancellationToken);

            _frameSinkSessionStarted = true;
        }

        private IRecordingFrameSink GetCurrentFrameSink()
        {
            return _currentFrameSink
                ?? throw new InvalidOperationException("No active recording frame sink is available.");
        }

        private CapturedFrame CopyTextureToCapturedFrame(ID3D11Texture2D sourceTexture)
        {
            if (_device is null || _deviceContext is null)
            {
                throw new InvalidOperationException("D3D11 device is not initialized.");
            }

            var sourceDescription = sourceTexture.Description;
            var width = (int)sourceDescription.Width;
            var height = (int)sourceDescription.Height;

            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("The duplicated frame returned an invalid size.");
            }

            EnsureSharedStagingTexture(sourceDescription);

            _deviceContext.CopyResource(_sharedStagingTexture!, sourceTexture);

            var pixelData = CopyMappedTextureToManagedBuffer(_sharedStagingTexture!, width, height);

            return new CapturedFrame
            {
                Width = width,
                Height = height,
                Stride = width * 4,
                PixelData = pixelData,
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
        }

        private void EnsureSharedStagingTexture(Texture2DDescription sourceDescription)
        {
            if (_device is null)
            {
                throw new InvalidOperationException("D3D11 device is not initialized.");
            }

            var width = (int)sourceDescription.Width;
            var height = (int)sourceDescription.Height;
            var format = sourceDescription.Format;

            if (_sharedStagingTexture is not null &&
                _sharedStagingWidth == width &&
                _sharedStagingHeight == height &&
                _sharedStagingFormat == format)
            {
                return;
            }

            _sharedStagingTexture?.Dispose();

            _sharedStagingTexture = _device.CreateTexture2D(
                CreateStagingTextureDescription(sourceDescription));

            _sharedStagingWidth = width;
            _sharedStagingHeight = height;
            _sharedStagingFormat = format;
        }

        private CapturedFrame CaptureSingleSnapshotInternal(
    CaptureSnapshotRequest request,
    CancellationToken cancellationToken)
        {
            if (_duplication is null || _device is null || _deviceContext is null)
            {
                throw new InvalidOperationException("Desktop duplication session is not initialized.");
            }

            IDXGIResource? desktopResource = null;
            ID3D11Texture2D? sourceTexture = null;
            ID3D11Texture2D? stagingTexture = null;
            var frameAcquired = false;

            try
            {
                var startedAt = Environment.TickCount64;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var elapsed = Environment.TickCount64 - startedAt;
                    var remaining = request.AcquireTimeoutMilliseconds - (int)elapsed;

                    if (remaining <= 0)
                    {
                        throw new TimeoutException("No desktop frame was received before the snapshot timed out.");
                    }

                    var waitMilliseconds = Math.Min(250, remaining);
                    var result = _duplication.AcquireNextFrame((uint)waitMilliseconds, out _, out desktopResource);

                    if (result.Code == DxgiErrorWaitTimeout)
                    {
                        desktopResource?.Dispose();
                        desktopResource = null;
                        continue;
                    }

                    if (result.Code == DxgiErrorAccessLost)
                    {
                        throw new InvalidOperationException("Desktop duplication access was lost during snapshot capture.");
                    }

                    if (result.Failure)
                    {
                        throw new InvalidOperationException($"AcquireNextFrame failed: {result.Code}");
                    }

                    frameAcquired = true;
                    break;
                }

                sourceTexture = desktopResource?.QueryInterfaceOrNull<ID3D11Texture2D>();

                if (sourceTexture is null)
                {
                    throw new InvalidOperationException("Failed to query the duplicated frame texture.");
                }

                var sourceDescription = sourceTexture.Description;
                var width = (int)sourceDescription.Width;
                var height = (int)sourceDescription.Height;

                if (width <= 0 || height <= 0)
                {
                    throw new InvalidOperationException("The duplicated frame returned an invalid size.");
                }

                if (sourceDescription.Format != Format.B8G8R8A8_UNorm)
                {
                    _logger.LogWarning("Unexpected desktop duplication format: {Format}", sourceDescription.Format);
                }

                stagingTexture = _device.CreateTexture2D(CreateStagingTextureDescription(sourceDescription));
                _deviceContext.CopyResource(stagingTexture, sourceTexture);

                var pixelData = CopyMappedTextureToManagedBuffer(stagingTexture, width, height);

                Interlocked.Exchange(ref _capturedFrameCount, 1);

                UpdateBackendInfo(
                    message: $"Snapshot captured successfully. {width}x{height}",
                    isInitialized: false,
                    isCapturing: false,
                    capturedFrameCount: 1);

                return new CapturedFrame
                {
                    Width = width,
                    Height = height,
                    Stride = width * 4,
                    PixelData = pixelData,
                    CapturedAtUtc = DateTimeOffset.UtcNow
                };
            }
            finally
            {
                stagingTexture?.Dispose();
                sourceTexture?.Dispose();
                desktopResource?.Dispose();

                if (frameAcquired)
                {
                    try
                    {
                        _duplication.ReleaseFrame();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to release duplicated desktop frame after snapshot capture.");
                    }
                }
            }
        }

        private byte[] CopyMappedTextureToManagedBuffer(ID3D11Texture2D stagingTexture, int width, int height)
        {
            if (_deviceContext is null)
            {
                throw new InvalidOperationException("D3D11 device context is not available.");
            }

            var stride = width * 4;
            var pixelData = new byte[stride * height];
            var mapped = _deviceContext.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            try
            {
                for (var y = 0; y < height; y++)
                {
                    var sourcePointer = IntPtr.Add(mapped.DataPointer, checked((int)(y * mapped.RowPitch)));
                    var destinationOffset = y * stride;

                    Marshal.Copy(sourcePointer, pixelData, destinationOffset, stride);
                }
            }
            finally
            {
                _deviceContext.Unmap(stagingTexture, 0);
            }

            return pixelData;
        }

        private static Texture2DDescription CreateStagingTextureDescription(Texture2DDescription sourceDescription)
        {
            return new Texture2DDescription
            {
                Width = sourceDescription.Width,
                Height = sourceDescription.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = sourceDescription.Format,
                SampleDescription = sourceDescription.SampleDescription,
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };
        }

        private void InitializeDuplicationSession(int captureAdapterIndex, int captureOutputIndex)
        {
            ReleaseResources();

            try
            {
                _capturedFrameCount = 0;

                _factory = CreateDXGIFactory1<IDXGIFactory1>();

                if (_factory is null)
                {
                    throw new InvalidOperationException("Failed to create DXGI factory.");
                }

                var adapterResult = _factory.EnumAdapters1((uint)captureAdapterIndex, out _adapter);

                if (adapterResult.Failure || _adapter is null)
                {
                    throw new InvalidOperationException($"No DXGI adapter found for index {captureAdapterIndex}.");
                }

                var outputResult = _adapter.EnumOutputs((uint)captureOutputIndex, out _output);

                if (outputResult.Failure || _output is null)
                {
                    throw new InvalidOperationException(
                        $"No DXGI output found for adapter {captureAdapterIndex}, output {captureOutputIndex}.");
                }

                _output1 = _output.QueryInterfaceOrNull<IDXGIOutput1>();

                if (_output1 is null)
                {
                    throw new InvalidOperationException("Failed to query IDXGIOutput1.");
                }

                var adapterDescription = _adapter.Description1.Description.Trim();
                var outputDescription = _output.Description.DeviceName.Trim();

                var creationFlags = DeviceCreationFlags.BgraSupport;

#if DEBUG
                creationFlags |= DeviceCreationFlags.Debug;
#endif

                var result = D3D11CreateDevice(
                    _adapter,
                    DriverType.Unknown,
                    creationFlags,
                    null,
                    out _device,
                    out _deviceContext);

                if (result.Code == unchecked((int)0x887A002D))
                {
                    _logger.LogWarning(
                        "D3D11 debug layer is not available. Retrying without DeviceCreationFlags.Debug.");

                    creationFlags = DeviceCreationFlags.BgraSupport;

                    result = D3D11CreateDevice(
                        _adapter,
                        DriverType.Unknown,
                        creationFlags,
                        null,
                        out _device,
                        out _deviceContext);
                }

                if (result.Failure || _device is null || _deviceContext is null)
                {
                    throw new InvalidOperationException($"D3D11CreateDevice failed: {result.Code}");
                }

                _duplication = _output1.DuplicateOutput(_device);

                if (_duplication is null)
                {
                    throw new InvalidOperationException("DuplicateOutput returned null.");
                }

                lock (_sync)
                {
                    _info = new CaptureBackendInfo
                    {
                        BackendName = BackendName,
                        AdapterName = adapterDescription,
                        OutputName = outputDescription,
                        IsInitialized = true,
                        IsCapturing = false,
                        CapturedFrameCount = 0,
                        Message = "Desktop duplication session initialized."
                    };
                }

                _logger.LogInformation(
                    "Desktop duplication initialized. Adapter={AdapterName}, Output={OutputName}",
                    adapterDescription,
                    outputDescription);
            }
            catch
            {
                ReleaseResources();
                throw;
            }
        }

        private void UpdateBackendInfo(
            string message,
            bool isInitialized,
            bool isCapturing,
            long? capturedFrameCount = null)
        {
            lock (_sync)
            {
                _info = new CaptureBackendInfo
                {
                    BackendName = BackendName,
                    AdapterName = _info.AdapterName,
                    OutputName = _info.OutputName,
                    IsInitialized = isInitialized,
                    IsCapturing = isCapturing,
                    CapturedFrameCount = capturedFrameCount ?? _info.CapturedFrameCount,
                    Message = message
                };
            }
        }

        private void ReleaseResources()
        {
            try
            {
                _deviceContext?.ClearState();
                _deviceContext?.Flush();

                _sharedStagingTexture?.Dispose();
                _sharedStagingTexture = null;
                _sharedStagingWidth = 0;
                _sharedStagingHeight = 0;
                _sharedStagingFormat = Format.Unknown;

                _duplication?.Dispose();
                _duplication = null;

                _output1?.Dispose();
                _output1 = null;

                _output?.Dispose();
                _output = null;

                _deviceContext?.Dispose();
                _deviceContext = null;

                _device?.Dispose();
                _device = null;

                _adapter?.Dispose();
                _adapter = null;

                _factory?.Dispose();
                _factory = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clear and flush D3D11 device context.");
            }
        }
    }
}
