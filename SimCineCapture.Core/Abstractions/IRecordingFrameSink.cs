using SimCineCapture.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Abstractions
{
    public interface IRecordingFrameSink
    {
        bool IsSessionOpen { get; }

        Task StartSessionAsync(
            FrameSinkStartRequest request,
            CancellationToken cancellationToken = default);

        Task WriteFrameAsync(
            CapturedFrame frame,
            long frameNumber,
            CancellationToken cancellationToken = default);

        Task CompleteSessionAsync(CancellationToken cancellationToken = default);
    }
}
