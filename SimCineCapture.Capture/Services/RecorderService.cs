using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimCineCapture.Core.Abstractions;
using SimCineCapture.Core.Enums;
using SimCineCapture.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SimCineCapture.Capture.Services
{
    public sealed class RecorderService : IRecorderService
    {
        private readonly object _sync = new();
        private readonly ICaptureBackend _captureBackend;
        private readonly ISequenceVideoEncoder _sequenceVideoEncoder;
        private readonly IOptions<AppSettings> _appSettings;
        private readonly ILogger<RecorderService> _logger;

        private RecorderStatus _status = new()
        {
            State = RecorderState.Idle,
            Message = "Recorder idle."
        };

        public RecorderService(
            ICaptureBackend captureBackend,
            ISequenceVideoEncoder sequenceVideoEncoder,
            IOptions<AppSettings> appSettings,
            ILogger<RecorderService> logger)
        {
            _captureBackend = captureBackend;
            _sequenceVideoEncoder = sequenceVideoEncoder;
            _appSettings = appSettings;
            _logger = logger;
        }

        public event Action<RecorderStatus>? RecorderStatusChanged;

        public string BackendName => _captureBackend.BackendName;

        public IReadOnlyList<CaptureOutputInfo> GetAvailableCaptureOutputs()
        {
            return _captureBackend.GetAvailableOutputs();
        }

        public CaptureBackendInfo GetBackendInfo()
        {
            return _captureBackend.GetInfo();
        }

        public RecorderStatus GetStatus()
        {
            lock (_sync)
            {
                return _status;
            }
        }

        public async Task<RecorderStatus> StartAsync(
            RecordingStartRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_status.State is RecorderState.Starting or RecorderState.Recording or RecorderState.Stopping)
                {
                    return _status;
                }

                _status = new RecorderStatus
                {
                    State = RecorderState.Starting,
                    Message = "Preparing recording session..."
                };
            }

            RaiseStatusChanged();

            try
            {
                var expandedDirectory = Environment.ExpandEnvironmentVariables(request.OutputDirectory);
                Directory.CreateDirectory(expandedDirectory);

                var safeAircraftName = SanitizePathPart(request.AircraftTitle);
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

                var fileName = string.IsNullOrWhiteSpace(safeAircraftName)
                    ? $"{request.FileNamePrefix}_{timestamp}.mp4"
                    : $"{request.FileNamePrefix}_{safeAircraftName}_{timestamp}.mp4";

                var fullPath = Path.Combine(expandedDirectory, fileName);

                await _captureBackend.StartCaptureAsync(new CaptureStartRequest
                {
                    OutputFilePath = fullPath,
                    HideSimulatorUi = request.HideSimulatorUi,
                    CaptureAdapterIndex = request.CaptureAdapterIndex,
                    CaptureOutputIndex = request.CaptureOutputIndex,
                    TargetFrameRate = request.TargetFrameRate
                }, cancellationToken);

                lock (_sync)
                {
                    _status = new RecorderStatus
                    {
                        State = RecorderState.Recording,
                        Message = "Recording started (PNG sequence capture active).",
                        OutputFilePath = fullPath,
                        StartedAtUtc = DateTimeOffset.UtcNow
                    };
                }

                _logger.LogInformation("Recording started. Output: {OutputFilePath}", fullPath);

                RaiseStatusChanged();
                return GetStatus();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start recording.");

                lock (_sync)
                {
                    _status = new RecorderStatus
                    {
                        State = RecorderState.Error,
                        Message = $"Failed to start recorder: {ex.Message}"
                    };
                }

                RaiseStatusChanged();
                return GetStatus();
            }
        }

        public async Task<RecorderStatus> StopAsync(CancellationToken cancellationToken = default)
        {
            string? outputFilePath;
            DateTimeOffset? startedAtUtc;

            lock (_sync)
            {
                if (_status.State is RecorderState.Idle or RecorderState.Error)
                {
                    _status = new RecorderStatus
                    {
                        State = RecorderState.Idle,
                        Message = "Recorder already idle.",
                        OutputFilePath = _status.OutputFilePath,
                        StartedAtUtc = _status.StartedAtUtc
                    };

                    RaiseStatusChanged();
                    return _status;
                }

                outputFilePath = _status.OutputFilePath;
                startedAtUtc = _status.StartedAtUtc;

                _status = new RecorderStatus
                {
                    State = RecorderState.Stopping,
                    Message = "Stopping recording...",
                    OutputFilePath = outputFilePath,
                    StartedAtUtc = startedAtUtc
                };
            }

            RaiseStatusChanged();

            try
            {
                await _captureBackend.StopCaptureAsync(cancellationToken);

                var frameSinkMode = _appSettings.Value.Recorder.FrameSink?.Trim().ToLowerInvariant();

                if (frameSinkMode == "ffmpeg" && !string.IsNullOrWhiteSpace(outputFilePath))
                {
                    lock (_sync)
                    {
                        _status = new RecorderStatus
                        {
                            State = RecorderState.Stopping,
                            Message = "Encoding MP4 with FFmpeg...",
                            OutputFilePath = outputFilePath,
                            StartedAtUtc = startedAtUtc
                        };
                    }

                    RaiseStatusChanged();

                    var framesDirectory = BuildFramesDirectoryPath(outputFilePath);

                    await _sequenceVideoEncoder.EncodeAsync(new SequenceVideoEncodingRequest
                    {
                        InputFramesDirectory = framesDirectory,
                        OutputFilePath = outputFilePath,
                        TargetFrameRate = Math.Max(1, _appSettings.Value.Recorder.TargetFrameRate)
                    }, cancellationToken);

                    lock (_sync)
                    {
                        _status = new RecorderStatus
                        {
                            State = RecorderState.Idle,
                            Message = "Recording stopped and MP4 encoded successfully.",
                            OutputFilePath = outputFilePath,
                            StartedAtUtc = startedAtUtc
                        };
                    }
                }
                else
                {
                    lock (_sync)
                    {
                        _status = new RecorderStatus
                        {
                            State = RecorderState.Idle,
                            Message = "Recording stopped. PNG frame sequence saved.",
                            OutputFilePath = outputFilePath,
                            StartedAtUtc = startedAtUtc
                        };
                    }
                }

                _logger.LogInformation("Recording stopped. Output: {OutputFilePath}", outputFilePath);

                RaiseStatusChanged();
                return GetStatus();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop recording.");

                lock (_sync)
                {
                    _status = new RecorderStatus
                    {
                        State = RecorderState.Error,
                        Message = $"Failed to stop recorder: {ex.Message}",
                        OutputFilePath = outputFilePath,
                        StartedAtUtc = startedAtUtc
                    };
                }

                RaiseStatusChanged();
                return GetStatus();
            }
        }

        public async Task<CapturedFrame> CaptureSnapshotAsync(
            CaptureSnapshotRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_status.State is RecorderState.Starting or RecorderState.Recording or RecorderState.Stopping)
                {
                    throw new InvalidOperationException("Snapshots are not available while recording is active.");
                }
            }

            var frame = await _captureBackend.CaptureSnapshotAsync(request, cancellationToken);

            _logger.LogInformation(
                "Snapshot captured. Width={Width}, Height={Height}, Stride={Stride}",
                frame.Width,
                frame.Height,
                frame.Stride);

            return frame;
        }

        private void RaiseStatusChanged()
        {
            RecorderStatusChanged?.Invoke(GetStatus());
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

        private static string BuildFramesDirectoryPath(string outputFilePath)
        {
            var parentDirectory = Path.GetDirectoryName(outputFilePath);

            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                throw new InvalidOperationException("Unable to determine frame sequence directory.");
            }

            var baseName = Path.GetFileNameWithoutExtension(outputFilePath);
            return Path.Combine(parentDirectory, $"{baseName}_frames");
        }
    }
}
