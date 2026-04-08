using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SimCineCapture.SimConnect.Models
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct AircraftTelemetryData
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Title;

        public double Latitude;

        public double Longitude;

        public double AltitudeFeet;

        public double GroundSpeedKnots;

        public int OnGround;
    }
}
