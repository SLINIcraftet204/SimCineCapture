using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimCineCapture.Core.Abstractions;
using SimCineCapture.Core.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace SimCineCapture.Capture.Services
{
    public sealed class FfmpegProcessFrameSink : IRecordingFrameSink, IDisposable
    {
        private readonly SemaphoreSlim _sync = new(1, 1);
        private readonly ILogger<FfmpegProcessFrameSink> _logger;
        private readonly IOptions<AppSettings> _appSettings;

        private readonly ConcurrentQueue<string> _stderrLines = new();

        private Process? _ffmpegProcess;
        private Stream? _ffmpegInputStream;
        private StreamWriter? _ffmpegStandardInputWriter;
        private FrameSinkStartRequest? _sessionRequest;
        private string? _currentOutputPath;

        public FfmpegProcessFrameSink(
            ILogger<FfmpegProcessFrameSink> logger,
            IOptions<AppSettings> appSettings)
        {
            _logger = logger;
            _appSettings = appSettings;
        }

        public bool IsSessionOpen
        {
            get
            {
                var process = _ffmpegProcess;
                return process is not null
                    && !process.HasExited
                    && _ffmpegInputStream is not null
                    && _ffmpegStandardInputWriter is not null;
            }
        }

        public async Task StartSessionAsync(
            FrameSinkStartRequest request,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(request.OutputPath))
            {
                throw new InvalidOperationException("FFmpeg output path must not be empty.");
            }

            if (request.Width <= 0 || request.Height <= 0 || request.Stride <= 0)
            {
                throw new InvalidOperationException("Invalid frame dimensions for FFmpeg session.");
            }

            await _sync.WaitAsync(cancellationToken);

            try
            {
                if (IsSessionOpen)
                {
                    throw new InvalidOperationException("FFmpeg frame sink session is already open.");
                }

                DrainStderrLines();

                var outputDirectory = Path.GetDirectoryName(request.OutputPath);

                if (string.IsNullOrWhiteSpace(outputDirectory))
                {
                    throw new InvalidOperationException("Unable to determine FFmpeg output directory.");
                }

                Directory.CreateDirectory(outputDirectory);

                var recorderSettings = _appSettings.Value.Recorder;
                var ffmpegExecutablePath = string.IsNullOrWhiteSpace(recorderSettings.FfmpegExecutablePath)
                    ? "ffmpeg"
                    : recorderSettings.FfmpegExecutablePath.Trim();

                var arguments = BuildArguments(request, recorderSettings);

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegExecutablePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = outputDirectory
                };

                var process = new Process
                {
                    StartInfo = processStartInfo,
                    EnableRaisingEvents = true
                };

                process.ErrorDataReceived += OnFfmpegErrorDataReceived;

                try
                {
                    if (!process.Start())
                    {
                        process.ErrorDataReceived -= OnFfmpegErrorDataReceived;
                        process.Dispose();
                        throw new InvalidOperationException("FFmpeg process could not be started.");
                    }
                }
                catch (Exception ex)
                {
                    process.ErrorDataReceived -= OnFfmpegErrorDataReceived;
                    process.Dispose();

                    throw new InvalidOperationException(
                        $"Failed to start FFmpeg. Check 'Recorder:FfmpegExecutablePath'. Details: {ex.Message}",
                        ex);
                }

                process.BeginErrorReadLine();

                _ffmpegProcess = process;
                _ffmpegStandardInputWriter = process.StandardInput;
                _ffmpegInputStream = process.StandardInput.BaseStream;
                _sessionRequest = request;
                _currentOutputPath = request.OutputPath;

                _logger.LogInformation(
                    "FFmpeg frame sink session started. Output={OutputPath}, Width={Width}, Height={Height}, FrameRate={FrameRate}",
                    request.OutputPath,
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
            await _sync.WaitAsync(cancellationToken);

            try
            {
                if (_ffmpegProcess is null || _ffmpegInputStream is null || _sessionRequest is null)
                {
                    throw new InvalidOperationException("FFmpeg frame sink session is not open.");
                }

                if (_ffmpegProcess.HasExited)
                {
                    throw new InvalidOperationException(
                        $"FFmpeg exited unexpectedly before frame {frameNumber} could be written. {BuildRecentStderrSummary()}");
                }

                if (frame.Width != _sessionRequest.Width || frame.Height != _sessionRequest.Height)
                {
                    throw new InvalidOperationException(
                        $"Frame size changed during recording. Expected {_sessionRequest.Width}x{_sessionRequest.Height}, got {frame.Width}x{frame.Height}.");
                }

                if (frame.PixelData.Length != frame.Stride * frame.Height)
                {
                    throw new InvalidOperationException(
                        $"Frame buffer size is invalid. Expected {frame.Stride * frame.Height} bytes, got {frame.PixelData.Length}.");
                }

                await _ffmpegInputStream.WriteAsync(
                        frame.PixelData.AsMemory(0, frame.PixelData.Length),
                        cancellationToken);

                await _ffmpegInputStream.FlushAsync(cancellationToken);
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task CompleteSessionAsync(CancellationToken cancellationToken = default)
        {
            Process? process;
            Stream? inputStream;
            StreamWriter? standardInputWriter;
            string? outputPath;

            await _sync.WaitAsync(cancellationToken);

            try
            {
                process = _ffmpegProcess;
                inputStream = _ffmpegInputStream;
                standardInputWriter = _ffmpegStandardInputWriter;
                outputPath = _currentOutputPath;

                _ffmpegProcess = null;
                _ffmpegInputStream = null;
                _ffmpegStandardInputWriter = null;
                _sessionRequest = null;
                _currentOutputPath = null;
            }
            finally
            {
                _sync.Release();
            }

            if (process is null)
            {
                return;
            }

            try
            {
                if (inputStream is not null)
                {
                    await inputStream.FlushAsync(cancellationToken);
                }

                if (standardInputWriter is not null)
                {
                    await standardInputWriter.FlushAsync();
                    standardInputWriter.Close(); // WICHTIG: signalisiert EOF an FFmpeg
                }

                await process.WaitForExitAsync(cancellationToken);

                var stderrSummary = BuildRecentStderrSummary();

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"FFmpeg exited with code {process.ExitCode}. {stderrSummary}");
                }

                _logger.LogInformation(
                    "FFmpeg frame sink session completed successfully. Output={OutputPath}",
                    outputPath);
            }
            finally
            {
                try
                {
                    process.ErrorDataReceived -= OnFfmpegErrorDataReceived;
                    process.CancelErrorRead();
                }
                catch
                {
                }

                try
                {
                    inputStream?.Dispose();
                }
                catch
                {
                }

                try
                {
                    standardInputWriter?.Dispose();
                }
                catch
                {
                }

                process.Dispose();
            }
        }

        public void Dispose()
        {
            try
            {
                _ffmpegInputStream?.Dispose();
            }
            catch
            {
            }

            try
            {
                _ffmpegStandardInputWriter?.Dispose();
            }
            catch
            {
            }

            try
            {
                if (_ffmpegProcess is not null)
                {
                    if (!_ffmpegProcess.HasExited)
                    {
                        _ffmpegProcess.Kill(entireProcessTree: true);
                    }

                    _ffmpegProcess.Dispose();
                }
            }
            catch
            {
            }
        }

        private void OnFfmpegErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _stderrLines.Enqueue(e.Data);
            }
        }

        private static string BuildArguments(
            FrameSinkStartRequest request,
            RecorderSettings recorderSettings)
        {
            var targetFrameRate = Math.Max(1, request.TargetFrameRate);

            var codec = string.IsNullOrWhiteSpace(recorderSettings.VideoCodec)
                ? "libx264"
                : recorderSettings.VideoCodec.Trim();

            var preset = string.IsNullOrWhiteSpace(recorderSettings.VideoPreset)
                ? "fast"
                : recorderSettings.VideoPreset.Trim();

            var quality = recorderSettings.VideoQuality <= 0
                ? 18
                : recorderSettings.VideoQuality;

            var qualityArgumentName = codec.Contains("nvenc", StringComparison.OrdinalIgnoreCase)
                ? "-cq"
                : "-crf";

            return string.Join(" ", new[]
            {
                "-hide_banner",
                "-loglevel error",
                "-y",
                "-f rawvideo",
                "-pix_fmt bgra",
                $"-video_size {request.Width}x{request.Height}",
                $"-framerate {targetFrameRate.ToString(CultureInfo.InvariantCulture)}",
                "-i pipe:0",
                "-an",
                $"-c:v {codec}",
                $"-preset {preset}",
                $"{qualityArgumentName} {quality.ToString(CultureInfo.InvariantCulture)}",
                $"-r {targetFrameRate.ToString(CultureInfo.InvariantCulture)}",
                "-pix_fmt yuv420p",
                "-movflags +faststart",
                QuoteArgument(request.OutputPath)
            });
        }

        private static string QuoteArgument(string value)
        {
            return $"\"{value.Replace("\"", "\\\"")}\"";
        }

        private void DrainStderrLines()
        {
            while (_stderrLines.TryDequeue(out _))
            {
            }
        }

        private string BuildRecentStderrSummary()
        {
            var lines = _stderrLines.ToArray();

            if (lines.Length == 0)
            {
                return "No FFmpeg error output was captured.";
            }

            var startIndex = Math.Max(0, lines.Length - 20);
            var lastLines = lines[startIndex..];

            return "FFmpeg stderr: " + string.Join(Environment.NewLine, lastLines);
        }
    }
}
