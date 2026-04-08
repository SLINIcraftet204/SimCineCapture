using Microsoft.Extensions.Logging;
using SimCineCapture.Core.Abstractions;
using SimCineCapture.Core.Models;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Text;
using Vortice.DCommon;
using System.IO;

namespace SimCineCapture.Capture.Services
{
    public sealed class PngSequenceFrameSink : IRecordingFrameSink
    {
        private readonly SemaphoreSlim _sync = new(1, 1);
        private readonly ILogger<PngSequenceFrameSink> _logger;

        private string? _currentOutputDirectory;
        private FrameSinkStartRequest? _sessionRequest;

        public PngSequenceFrameSink(ILogger<PngSequenceFrameSink> logger)
        {
            _logger = logger;
        }

        public bool IsSessionOpen => !string.IsNullOrWhiteSpace(_currentOutputDirectory);

        public async Task StartSessionAsync(
            FrameSinkStartRequest request,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(request.OutputPath))
            {
                throw new InvalidOperationException("Frame sink output path must not be empty.");
            }

            await _sync.WaitAsync(cancellationToken);

            try
            {
                var parentDirectory = Path.GetDirectoryName(request.OutputPath);

                if (string.IsNullOrWhiteSpace(parentDirectory))
                {
                    throw new InvalidOperationException("Unable to determine frame sink parent directory.");
                }

                var baseName = Path.GetFileNameWithoutExtension(request.OutputPath);
                var framesDirectory = Path.Combine(parentDirectory, $"{baseName}_frames");

                Directory.CreateDirectory(framesDirectory);

                _currentOutputDirectory = framesDirectory;
                _sessionRequest = request;

                _logger.LogInformation(
                    "PNG sequence frame sink session started. Directory={Directory}, Width={Width}, Height={Height}, TargetFrameRate={TargetFrameRate}",
                    framesDirectory,
                    request.Width,
                    request.Height,
                    request.TargetFrameRate);
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task WriteFrameAsync(
            CapturedFrame frame,
            long frameNumber,
            CancellationToken cancellationToken = default)
        {
            string outputDirectory;
            FrameSinkStartRequest sessionRequest;

            await _sync.WaitAsync(cancellationToken);

            try
            {
                if (string.IsNullOrWhiteSpace(_currentOutputDirectory) || _sessionRequest is null)
                {
                    throw new InvalidOperationException("Frame sink session is not open.");
                }

                outputDirectory = _currentOutputDirectory;
                sessionRequest = _sessionRequest;
            }
            finally
            {
                _sync.Release();
            }

            if (frame.Width != sessionRequest.Width || frame.Height != sessionRequest.Height)
            {
                throw new InvalidOperationException(
                    $"Frame size changed during session. Expected {sessionRequest.Width}x{sessionRequest.Height}, got {frame.Width}x{frame.Height}.");
            }

            var outputFilePath = Path.Combine(outputDirectory, $"frame_{frameNumber:D8}.png");

            await Task.Run(() =>
            {
                SaveFrameAsPng(frame, outputFilePath);
            }, cancellationToken);
        }

        public async Task CompleteSessionAsync(CancellationToken cancellationToken = default)
        {
            await _sync.WaitAsync(cancellationToken);

            try
            {
                if (!string.IsNullOrWhiteSpace(_currentOutputDirectory))
                {
                    _logger.LogInformation(
                        "PNG sequence frame sink session completed. Directory={Directory}",
                        _currentOutputDirectory);
                }

                _currentOutputDirectory = null;
                _sessionRequest = null;
            }
            finally
            {
                _sync.Release();
            }
        }

        private static void SaveFrameAsPng(CapturedFrame frame, string outputFilePath)
        {
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
        }
    }
}
