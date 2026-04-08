using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Models
{
    public sealed class RecorderSettings
    {
        public bool HideSimulatorUiDuringRecording { get; init; } = true;

        public string OutputDirectory { get; init; } = @"%USERPROFILE%\Videos\SimCineCapture";

        public bool StopRecordingOnCriticalFpsDrop { get; init; } = true;

        public int CriticalFpsThreshold { get; init; } = 10;

        public string CaptureBackend { get; init; } = "DesktopDuplication";

        public int CaptureAdapterIndex { get; init; } = 0;

        public int CaptureOutputIndex { get; init; } = 0;

        public int TargetFrameRate { get; init; } = 30;

        public string FrameSink { get; init; } = "FFmpeg";

        public string FfmpegExecutablePath { get; init; } = "ffmpeg";

        public string VideoCodec { get; init; } = "libx264";

        public string VideoPreset { get; init; } = "fast";

        public int VideoQuality { get; init; } = 18;
    }
}
