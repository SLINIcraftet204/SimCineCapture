using System;
using System.Collections.Generic;
using System.Text;
using SimCineCapture.Core.Enums;

namespace SimCineCapture.Core.Models
{
    public sealed class SimulatorConnectionStatus
    {
        public SimulatorConnectionState State { get; init; } = SimulatorConnectionState.Disconnected;

        public string Message { get; init; } = "Simulator disconnected.";

        public bool IsConnected => State == SimulatorConnectionState.Connected;
    }
}
