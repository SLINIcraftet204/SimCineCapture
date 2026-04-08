using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Models
{
    public sealed class SimulatorSettings
    {
        public bool AutoConnectOnStartup { get; init; } = false;
        public int ConnectionTimeoutSeconds { get; init; } = 5;
    }
}
