using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Models
{
    public sealed class RecordingStartRequest
    {
        public string OutputDirectory { get; init; } = string.Empty;

        public bool HideSimulatorUi { get; init; }

        public string FileNamePrefix { get; init; } = "SimCineCapture";

        public string? AircraftTitle { get; init; }

        public int CaptureAdapterIndex { get; init; }

        public int CaptureOutputIndex { get; init; }

        public int TargetFrameRate { get; init; } = 30;
    }
}
