using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Models
{
    public sealed class FrameSinkStartRequest
    {
        public string OutputPath { get; init; } = string.Empty;

        public int Width { get; init; }

        public int Height { get; init; }

        public int Stride { get; init; }

        public int TargetFrameRate { get; init; } = 10;
    }
}
