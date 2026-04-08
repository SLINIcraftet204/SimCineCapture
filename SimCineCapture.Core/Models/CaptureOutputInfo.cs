using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Models
{
    public sealed class CaptureOutputInfo
    {
        public int AdapterIndex { get; init; }

        public int OutputIndex { get; init; }

        public string AdapterName { get; init; } = "Unknown Adapter";

        public string OutputName { get; init; } = "Unknown Output";

        public string DisplayLabel => $"{AdapterName} | {OutputName}";

        public override string ToString() => DisplayLabel;
    }
}
