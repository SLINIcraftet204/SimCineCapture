using System;
using System.Collections.Generic;
using System.Text;
using SimCineCapture.Core.Models;

namespace SimCineCapture.Core.Abstractions
{
    public interface ISimConnectService
    {
        event Action<SimulatorTelemetry>? TelemetryUpdated;

        bool IsConnected { get; }

        string BackendName { get; }

        void InitializeWindowHandle(nint windowHandle);

        void ReceiveMessage();

        Task<SimulatorConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default);

        Task DisconnectAsync(CancellationToken cancellationToken = default);

        SimulatorConnectionStatus GetStatus();

        SimulatorTelemetry GetLatestTelemetry();
    }
}
