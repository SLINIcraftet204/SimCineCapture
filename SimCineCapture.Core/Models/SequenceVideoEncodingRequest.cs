using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Models
{
    public sealed class SequenceVideoEncodingRequest
    {
        public string InputFramesDirectory { get; init; } = string.Empty;

        public string OutputFilePath { get; init; } = string.Empty;

        public int TargetFrameRate { get; init; } = 30;
    }
}
