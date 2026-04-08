using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Models
{
    public sealed class CaptureStartRequest
    {
        public string OutputFilePath { get; init; } = string.Empty;

        public bool HideSimulatorUi { get; init; }

        public int CaptureAdapterIndex { get; init; }

        public int CaptureOutputIndex { get; init; }

        public int TargetFrameRate { get; init; } = 30;
    }
}
