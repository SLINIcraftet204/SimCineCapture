using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Abstractions
{
    public interface IAppSettingsPersistenceService
    {
        string GetConfigFilePath();

        Task SaveRecorderSettingsAsync(
            string outputDirectory,
            bool hideSimulatorUiDuringRecording,
            int captureAdapterIndex,
            int captureOutputIndex,
            CancellationToken cancellationToken = default);
    }
}
