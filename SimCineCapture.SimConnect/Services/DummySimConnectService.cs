using System;
using System.Collections.Generic;
using System.Text;
using SimCineCapture.Core.Abstractions;
using SimCineCapture.Core.Enums;
using SimCineCapture.Core.Models;

namespace SimCineCapture.SimConnect.Services
{
    public sealed class DummySimConnectService : ISimConnectService
    {
        private SimulatorConnectionStatus _status = new()
        {
            State = SimulatorConnectionState.Disconnected,
            Message = "No active simulator connection."
        };

        private SimulatorTelemetry _latestTelemetry = new();

        public event Action<SimulatorTelemetry>? TelemetryUpdated;

        public bool IsConnected => _status.IsConnected;

        public string BackendName => "Dummy backend";

        public void InitializeWindowHandle(nint windowHandle)
        {
        }

        public void ReceiveMessage()
        {
        }

        public async Task<SimulatorConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default)
        {
            _status = new SimulatorConnectionStatus
            {
                State = SimulatorConnectionState.Connecting,
                Message = "Connecting to Microsoft Flight Simulator..."
            };

            await Task.Delay(1200, cancellationToken);

            _status = new SimulatorConnectionStatus
            {
                State = SimulatorConnectionState.Connected,
                Message = "Connected to simulator (dummy service)."
            };

            _latestTelemetry = new SimulatorTelemetry
            {
                AircraftTitle = "Dummy Aircraft",
                Latitude = 50.6000,
                Longitude = 8.6700,
                AltitudeFeet = 1234,
                GroundSpeedKnots = 0,
                IsOnGround = true,
                TimestampUtc = DateTimeOffset.UtcNow
            };

            TelemetryUpdated?.Invoke(_latestTelemetry);

            return _status;
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                _status = new SimulatorConnectionStatus
                {
                    State = SimulatorConnectionState.Disconnected,
                    Message = "Simulator already disconnected."
                };

                return;
            }

            await Task.Delay(300, cancellationToken);

            _status = new SimulatorConnectionStatus
            {
                State = SimulatorConnectionState.Disconnected,
                Message = "Disconnected from simulator."
            };

            _latestTelemetry = new SimulatorTelemetry();
        }

        public SimulatorConnectionStatus GetStatus()
        {
            return _status;
        }

        public SimulatorTelemetry GetLatestTelemetry()
        {
            return _latestTelemetry;
        }
    }
}
