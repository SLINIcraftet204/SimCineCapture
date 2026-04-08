using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Models
{
    public sealed class AppSettings
    {
        public SimulatorSettings Simulator { get; init; } = new();
        public RecorderSettings Recorder { get; init; } = new();
    }
}
