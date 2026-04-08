using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Models
{
    public sealed class CaptureSnapshotRequest
    {
        public int CaptureAdapterIndex { get; init; }

        public int CaptureOutputIndex { get; init; }

        public int AcquireTimeoutMilliseconds { get; init; } = 2000;
    }
}
