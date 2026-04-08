using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Models
{
    public sealed class SimulatorTelemetry
    {
        public string AircraftTitle { get; init; } = "Unknown";

        public double Latitude { get; init; }

        public double Longitude { get; init; }

        public double AltitudeFeet { get; init; }

        public double GroundSpeedKnots { get; init; }

        public bool IsOnGround { get; init; }

        public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    }
}
