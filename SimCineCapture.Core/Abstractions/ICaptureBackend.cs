using SimCineCapture.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Abstractions
{
    public interface ICaptureBackend
    {
        string BackendName { get; }

        bool IsCapturing { get; }

        CaptureBackendInfo GetInfo();

        IReadOnlyList<CaptureOutputInfo> GetAvailableOutputs();

        Task StartCaptureAsync(CaptureStartRequest request, CancellationToken cancellationToken = default);

        Task StopCaptureAsync(CancellationToken cancellationToken = default);

        Task<CapturedFrame> CaptureSnapshotAsync(
            CaptureSnapshotRequest request,
            CancellationToken cancellationToken = default);
    }
}
