using SimCineCapture.Core.Abstractions;
using SimCineCapture.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SimCineCapture.Capture.Services
{
    public sealed class DummyCaptureBackend : ICaptureBackend
    {
        private readonly object _sync = new();

        private bool _isCapturing;
        private string? _currentOutputFilePath;

        public string BackendName => "Dummy Capture Backend";

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
            return new CaptureBackendInfo
            {
                BackendName = BackendName,
                AdapterName = "Dummy Adapter",
                OutputName = "Dummy Output",
                IsInitialized = _isCapturing,
                IsCapturing = _isCapturing,
                CapturedFrameCount = 0,
                Message = _isCapturing ? "Dummy capture session active." : "Dummy backend idle."
            };
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
                    throw new InvalidOperationException("Capture backend is already capturing.");
                }
            }

            var outputDirectory = Path.GetDirectoryName(request.OutputFilePath);

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new InvalidOperationException("Unable to determine output directory.");
            }

            Directory.CreateDirectory(outputDirectory);

            await Task.Delay(300, cancellationToken);

            lock (_sync)
            {
                _currentOutputFilePath = request.OutputFilePath;
                _isCapturing = true;
            }
        }

        public async Task StopCaptureAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (!_isCapturing)
                {
                    return;
                }
            }

            await Task.Delay(250, cancellationToken);

            lock (_sync)
            {
                _currentOutputFilePath = null;
                _isCapturing = false;
            }
        }

        public async Task<CapturedFrame> CaptureSnapshotAsync(
            CaptureSnapshotRequest request,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(100, cancellationToken);

            const int width = 320;
            const int height = 180;
            const int stride = width * 4;

            var pixelData = new byte[stride * height];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = (y * stride) + (x * 4);
                    var checker = ((x / 16) + (y / 16)) % 2 == 0;

                    pixelData[offset + 0] = checker ? (byte)0x30 : (byte)0x80; // B
                    pixelData[offset + 1] = checker ? (byte)0x60 : (byte)0x20; // G
                    pixelData[offset + 2] = checker ? (byte)0xD0 : (byte)0x40; // R
                    pixelData[offset + 3] = 0xFF;                               // A
                }
            }

            return new CapturedFrame
            {
                Width = width,
                Height = height,
                Stride = stride,
                PixelData = pixelData,
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
        }

        public IReadOnlyList<CaptureOutputInfo> GetAvailableOutputs()
        {
            return new List<CaptureOutputInfo>
            {
                new()
                {
                    AdapterIndex = 0,
                    OutputIndex = 0,
                    AdapterName = "Dummy Adapter",
                    OutputName = "Dummy Output"
                }
            };
        }
    }
}
