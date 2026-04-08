using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Models
{
    public sealed class CaptureBackendInfo
    {
        public string BackendName { get; init; } = "Unknown";

        public string AdapterName { get; init; } = "Unknown";

        public string OutputName { get; init; } = "Unknown";

        public bool IsInitialized { get; init; }

        public bool IsCapturing { get; init; }

        public long CapturedFrameCount { get; init; }

        public string Message { get; init; } = "Not initialized.";
    }
}
