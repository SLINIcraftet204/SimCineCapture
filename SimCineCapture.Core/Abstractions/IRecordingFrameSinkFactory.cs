using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Abstractions
{
    public interface IRecordingFrameSinkFactory
    {
        IRecordingFrameSink Create();
    }
}
