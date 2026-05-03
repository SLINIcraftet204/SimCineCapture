using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimCineCapture.Core.Abstractions;
using SimCineCapture.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace SimCineCapture.Capture.Services
{
    public sealed class FfmpegImageSequenceVideoEncoder : ISequenceVideoEncoder
    {
        private readonly ILogger<FfmpegImageSequenceVideoEncoder> _logger;
        private readonly IOptions<AppSettings> _appSettings;

        public FfmpegImageSequenceVideoEncoder(
            ILogger<FfmpegImageSequenceVideoEncoder> logger,
            IOptions<AppSettings> appSettings)
        {
            _logger = logger;
            _appSettings = appSettings;
        }

        public async Task EncodeAsync(
            SequenceVideoEncodingRequest request,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(request.InputFramesDirectory))
            {
                throw new InvalidOperationException("Input frames directory must not be empty.");
            }

            if (string.IsNullOrWhiteSpace(request.OutputFilePath))
            {
                throw new InvalidOperationException("Output video path must not be empty.");
            }

            if (!Directory.Exists(request.InputFramesDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"Input frames directory was not found: {request.InputFramesDirectory}");
            }

            var firstFramePath = Path.Combine(request.InputFramesDirectory, "frame_00000001.png");

            if (!File.Exists(firstFramePath))
            {
                throw new InvalidOperationException(
                    $"No frame sequence was found. Expected first frame at: {firstFramePath}");
            }

            var outputDirectory = Path.GetDirectoryName(request.OutputFilePath);

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new InvalidOperationException("Unable to determine output video directory.");
            }

            Directory.CreateDirectory(outputDirectory);

            var recorderSettings = _appSettings.Value.Recorder;

            var ffmpegExecutablePath = string.IsNullOrWhiteSpace(recorderSettings.FfmpegExecutablePath)
                ? "ffmpeg"
                : recorderSettings.FfmpegExecutablePath.Trim();

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

            var inputPattern = Path.Combine(request.InputFramesDirectory, "frame_%08d.png");

            var arguments = string.Join(" ", new[]
            {
                "-hide_banner",
                "-loglevel error",
                "-y",
                $"-framerate {targetFrameRate.ToString(CultureInfo.InvariantCulture)}",
                "-start_number 1",
                "-i",
                QuoteArgument(inputPattern),
                "-an",
                "-c:v",
                codec,
                "-preset",
                preset,
                qualityArgumentName,
                quality.ToString(CultureInfo.InvariantCulture),
                "-pix_fmt",
                "yuv420p",
                "-movflags",
                "+faststart",
                QuoteArgument(request.OutputFilePath)
            });

            var processStartInfo = new ProcessStartInfo
            {
                FileName = ffmpegExecutablePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = outputDirectory
            };

            using var process = new Process
            {
                StartInfo = processStartInfo
            };

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("FFmpeg process could not be started.");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to start FFmpeg. Check 'Recorder:FfmpegExecutablePath'. Details: {ex.Message}",
                    ex);
            }

            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken);

            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"FFmpeg exited with code {process.ExitCode}.{Environment.NewLine}{stderr}");
            }

            var outputFileInfo = new FileInfo(request.OutputFilePath);

            if (!outputFileInfo.Exists || outputFileInfo.Length == 0)
            {
                throw new InvalidOperationException(
                    "FFmpeg finished without creating a valid MP4 file.");
            }

            _logger.LogInformation(
                "FFmpeg image sequence encoding completed. Output={OutputFilePath}, Size={SizeBytes}",
                request.OutputFilePath,
                outputFileInfo.Length);
        }

        private static string QuoteArgument(string value)
        {
            return $"\"{value.Replace("\"", "\\\"")}\"";
        }
    }
}
