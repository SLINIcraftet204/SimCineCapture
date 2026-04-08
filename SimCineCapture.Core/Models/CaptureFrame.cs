using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Models
{
    public sealed class CapturedFrame
    {
        public int Width { get; init; }

        public int Height { get; init; }

        public int Stride { get; init; }

        public byte[] PixelData { get; init; } = [];

        public DateTimeOffset CapturedAtUtc { get; init; }
    }
}
