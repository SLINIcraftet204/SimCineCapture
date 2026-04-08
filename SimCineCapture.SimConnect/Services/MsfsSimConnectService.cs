using Microsoft.Extensions.Logging;
using Microsoft.FlightSimulator.SimConnect;
using SimCineCapture.Core.Abstractions;
using SimCineCapture.Core.Enums;
using SimCineCapture.Core.Models;
using SimCineCapture.SimConnect.Models;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using SimConnectClient = Microsoft.FlightSimulator.SimConnect.SimConnect;

namespace SimCineCapture.SimConnect.Services
{
    public sealed class MsfsSimConnectService : ISimConnectService, IDisposable
    {
        private const int WmUserSimConnect = 0x0402;

        private readonly ILogger<MsfsSimConnectService> _logger;
        private readonly object _sync = new();

        private SimConnectClient? _simConnect;
        private nint _windowHandle;
        private bool _dataDefinitionsRegistered;

        private SimulatorConnectionStatus _status = new()
        {
            State = SimulatorConnectionState.Disconnected,
            Message = "Simulator disconnected."
        };

        private SimulatorTelemetry _latestTelemetry = new();

        public event Action<SimulatorTelemetry>? TelemetryUpdated;

        public MsfsSimConnectService(ILogger<MsfsSimConnectService> logger)
        {
            _logger = logger;
        }

        public bool IsConnected
        {
            get
            {
                lock (_sync)
                {
                    return _status.IsConnected;
                }
            }
        }

        public string BackendName => "MSFS SimConnect";

        public void InitializeWindowHandle(nint windowHandle)
        {
            _windowHandle = windowHandle;
            _logger.LogInformation("SimConnect window handle initialized: {WindowHandle}", windowHandle);
        }

        public async Task<SimulatorConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (_windowHandle == 0)
            {
                return UpdateStatus(
                    SimulatorConnectionState.Error,
                    "No window handle available for SimConnect message dispatch.");
            }

            if (_simConnect is not null)
            {
                return GetStatus();
            }

            try
            {
                UpdateStatus(
                    SimulatorConnectionState.Connecting,
                    "Connecting to simulator via SimConnect...");

                _logger.LogInformation("Opening SimConnect connection...");

                _simConnect = new SimConnectClient(
                    "SimCineCapture",
                    _windowHandle,
                    WmUserSimConnect,
                    null,
                    0);

                _simConnect.OnRecvOpen += SimConnect_OnRecvOpen;
                _simConnect.OnRecvQuit += SimConnect_OnRecvQuit;
                _simConnect.OnRecvException += SimConnect_OnRecvException;
                _simConnect.OnRecvSimobjectData += SimConnect_OnRecvSimobjectData;

                RegisterDataDefinitions();

                await Task.Delay(50, cancellationToken);

                return UpdateStatus(
                    SimulatorConnectionState.Connected,
                    "Connected to simulator via SimConnect.");
            }
            catch (COMException ex)
            {
                _logger.LogError(ex, "SimConnect COM exception while connecting.");

                DisconnectInternal("SimConnect connection failed.");

                return UpdateStatus(
                    SimulatorConnectionState.Error,
                    $"SimConnect COM error: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while connecting to SimConnect.");

                DisconnectInternal("SimConnect connection failed.");

                return UpdateStatus(
                    SimulatorConnectionState.Error,
                    $"Unexpected connection error: {ex.Message}");
            }
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectInternal("Disconnected from simulator.");
            return Task.CompletedTask;
        }

        public void ReceiveMessage()
        {
            if (_simConnect is null)
            {
                return;
            }

            try
            {
                _simConnect.ReceiveMessage();
            }
            catch (COMException ex)
            {
                _logger.LogError(ex, "SimConnect ReceiveMessage failed.");

                UpdateStatus(
                    SimulatorConnectionState.Error,
                    $"SimConnect receive error: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected ReceiveMessage error.");

                UpdateStatus(
                    SimulatorConnectionState.Error,
                    $"Unexpected receive error: {ex.Message}");
            }
        }

        public SimulatorConnectionStatus GetStatus()
        {
            lock (_sync)
            {
                return _status;
            }
        }

        public SimulatorTelemetry GetLatestTelemetry()
        {
            lock (_sync)
            {
                return _latestTelemetry;
            }
        }

        public void Dispose()
        {
            DisconnectInternal("SimConnect service disposed.");
        }

        private void SimConnect_OnRecvOpen(SimConnectClient sender, SIMCONNECT_RECV_OPEN data)
        {
            _logger.LogInformation("SimConnect session opened.");

            RequestTelemetryStream();

            UpdateStatus(
                SimulatorConnectionState.Connected,
                "Connected to simulator via SimConnect.");
        }

        private void SimConnect_OnRecvQuit(SimConnectClient sender, SIMCONNECT_RECV data)
        {
            _logger.LogWarning("Simulator sent quit event.");
            DisconnectInternal("Simulator quit detected.");
        }

        private void SimConnect_OnRecvException(SimConnectClient sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            _logger.LogWarning("SimConnect exception received: {Exception}", data.dwException);

            UpdateStatus(
                SimulatorConnectionState.Error,
                $"SimConnect exception: {data.dwException}");
        }

        private void SimConnect_OnRecvSimobjectData(SimConnectClient sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            if ((DataRequests)data.dwRequestID != DataRequests.AircraftTelemetry)
            {
                return;
            }

            var rawTelemetry = (AircraftTelemetryData)data.dwData[0];

            var mappedTelemetry = new SimulatorTelemetry
            {
                AircraftTitle = string.IsNullOrWhiteSpace(rawTelemetry.Title) ? "Unknown" : rawTelemetry.Title.Trim(),
                Latitude = rawTelemetry.Latitude,
                Longitude = rawTelemetry.Longitude,
                AltitudeFeet = rawTelemetry.AltitudeFeet,
                GroundSpeedKnots = rawTelemetry.GroundSpeedKnots,
                IsOnGround = rawTelemetry.OnGround != 0,
                TimestampUtc = DateTimeOffset.UtcNow
            };

            lock (_sync)
            {
                _latestTelemetry = mappedTelemetry;
            }

            TelemetryUpdated?.Invoke(mappedTelemetry);
        }

        private void RegisterDataDefinitions()
        {
            if (_simConnect is null || _dataDefinitionsRegistered)
            {
                return;
            }

            _simConnect.AddToDataDefinition(
                DataDefinitions.AircraftTelemetry,
                "TITLE",
                null,
                SIMCONNECT_DATATYPE.STRING256,
                0.0f,
                SimConnectClient.SIMCONNECT_UNUSED);

            _simConnect.AddToDataDefinition(
                DataDefinitions.AircraftTelemetry,
                "PLANE LATITUDE",
                "degrees",
                SIMCONNECT_DATATYPE.FLOAT64,
                0.0f,
                SimConnectClient.SIMCONNECT_UNUSED);

            _simConnect.AddToDataDefinition(
                DataDefinitions.AircraftTelemetry,
                "PLANE LONGITUDE",
                "degrees",
                SIMCONNECT_DATATYPE.FLOAT64,
                0.0f,
                SimConnectClient.SIMCONNECT_UNUSED);

            _simConnect.AddToDataDefinition(
                DataDefinitions.AircraftTelemetry,
                "PLANE ALTITUDE",
                "feet",
                SIMCONNECT_DATATYPE.FLOAT64,
                0.0f,
                SimConnectClient.SIMCONNECT_UNUSED);

            _simConnect.AddToDataDefinition(
                DataDefinitions.AircraftTelemetry,
                "GROUND VELOCITY",
                "knots",
                SIMCONNECT_DATATYPE.FLOAT64,
                0.0f,
                SimConnectClient.SIMCONNECT_UNUSED);

            _simConnect.AddToDataDefinition(
                DataDefinitions.AircraftTelemetry,
                "SIM ON GROUND",
                "Bool",
                SIMCONNECT_DATATYPE.INT32,
                0.0f,
                SimConnectClient.SIMCONNECT_UNUSED);

            _simConnect.RegisterDataDefineStruct<AircraftTelemetryData>(DataDefinitions.AircraftTelemetry);

            _dataDefinitionsRegistered = true;

            _logger.LogInformation("SimConnect data definitions registered.");
        }

        private void RequestTelemetryStream()
        {
            if (_simConnect is null)
            {
                return;
            }

            _simConnect.RequestDataOnSimObject(
                DataRequests.AircraftTelemetry,
                DataDefinitions.AircraftTelemetry,
                SimConnectClient.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.SECOND,
                SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
                0,
                0,
                0);

            _logger.LogInformation("SimConnect telemetry stream requested.");
        }

        private void DisconnectInternal(string message)
        {
            if (_simConnect is not null)
            {
                try
                {
                    _simConnect.OnRecvOpen -= SimConnect_OnRecvOpen;
                    _simConnect.OnRecvQuit -= SimConnect_OnRecvQuit;
                    _simConnect.OnRecvException -= SimConnect_OnRecvException;
                    _simConnect.OnRecvSimobjectData -= SimConnect_OnRecvSimobjectData;
                    _simConnect.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error while disposing SimConnect.");
                }
                finally
                {
                    _simConnect = null;
                }
            }

            _dataDefinitionsRegistered = false;

            lock (_sync)
            {
                _latestTelemetry = new SimulatorTelemetry();
            }

            UpdateStatus(SimulatorConnectionState.Disconnected, message);
        }

        private SimulatorConnectionStatus UpdateStatus(
            SimulatorConnectionState state,
            string message)
        {
            lock (_sync)
            {
                _status = new SimulatorConnectionStatus
                {
                    State = state,
                    Message = message
                };

                return _status;
            }
        }

        private enum DataDefinitions
        {
            AircraftTelemetry = 1
        }

        private enum DataRequests
        {
            AircraftTelemetry = 1
        }
    }
}
