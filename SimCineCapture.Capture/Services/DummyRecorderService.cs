using SimCineCapture.Core.Abstractions;
using SimCineCapture.Core.Enums;
using SimCineCapture.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Capture.Services
{
    public sealed class DummyRecorderService : IRecorderService
    {
        private readonly object _sync = new();

        private RecorderStatus _status = new()
        {
            State = RecorderState.Idle,
            Message = "Recorder idle."
        };

        public event Action<RecorderStatus>? RecorderStatusChanged;

        public string BackendName => "Dummy Recorder";

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
                if (_status.IsRecording)
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

                await Task.Delay(500, cancellationToken);

                lock (_sync)
                {
                    _status = new RecorderStatus
                    {
                        State = RecorderState.Recording,
                        Message = "Dummy recording started.",
                        OutputFilePath = fullPath,
                        StartedAtUtc = DateTimeOffset.UtcNow
                    };
                }

                RaiseStatusChanged();
                return _status;
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _status = new RecorderStatus
                    {
                        State = RecorderState.Error,
                        Message = $"Failed to start recorder: {ex.Message}"
                    };
                }

                RaiseStatusChanged();
                return _status;
            }
        }

        public async Task<RecorderStatus> StopAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (!_status.IsRecording)
                {
                    _status = new RecorderStatus
                    {
                        State = RecorderState.Idle,
                        Message = "Recorder already idle."
                    };

                    RaiseStatusChanged();
                    return _status;
                }

                _status = new RecorderStatus
                {
                    State = RecorderState.Stopping,
                    Message = "Stopping recording...",
                    OutputFilePath = _status.OutputFilePath,
                    StartedAtUtc = _status.StartedAtUtc
                };
            }

            RaiseStatusChanged();

            await Task.Delay(350, cancellationToken);

            lock (_sync)
            {
                _status = new RecorderStatus
                {
                    State = RecorderState.Idle,
                    Message = "Recording stopped.",
                    OutputFilePath = _status.OutputFilePath,
                    StartedAtUtc = _status.StartedAtUtc
                };
            }

            RaiseStatusChanged();
            return _status;
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
    }
}
