using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Enums
{
    public enum RecorderState
    {
        Idle = 0,
        Starting = 1,
        Recording = 2,
        Stopping = 3,
        Error = 4
    }
}
