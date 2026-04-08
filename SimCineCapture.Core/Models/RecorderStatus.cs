using SimCineCapture.Core.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Models
{
    public sealed class RecorderStatus
    {
        public RecorderState State { get; init; } = RecorderState.Idle;

        public string Message { get; init; } = "Recorder idle.";

        public string? OutputFilePath { get; init; }

        public DateTimeOffset? StartedAtUtc { get; init; }

        public bool IsRecording => State == RecorderState.Recording;
    }
}
