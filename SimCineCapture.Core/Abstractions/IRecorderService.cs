using SimCineCapture.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Abstractions
{
    public interface IRecorderService
    {
        event Action<RecorderStatus>? RecorderStatusChanged;

        string BackendName { get; }

        CaptureBackendInfo GetBackendInfo();

        IReadOnlyList<CaptureOutputInfo> GetAvailableCaptureOutputs();

        RecorderStatus GetStatus();

        Task<RecorderStatus> StartAsync(RecordingStartRequest request, CancellationToken cancellationToken = default);

        Task<RecorderStatus> StopAsync(CancellationToken cancellationToken = default);

        Task<CapturedFrame> CaptureSnapshotAsync(
            CaptureSnapshotRequest request,
            CancellationToken cancellationToken = default);
    }
}
