using Microsoft.Extensions.Logging;
using SimCineCapture.Core.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimCineCapture.UI.Services
{
    public sealed class JsonAppSettingsPersistenceService : IAppSettingsPersistenceService
    {
        private readonly ILogger<JsonAppSettingsPersistenceService> _logger;
        private readonly string _configFilePath;

        public JsonAppSettingsPersistenceService(ILogger<JsonAppSettingsPersistenceService> logger)
        {
            _logger = logger;
            _configFilePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        }

        public string GetConfigFilePath()
        {
            return _configFilePath;
        }

        public async Task SaveRecorderSettingsAsync(
        string outputDirectory,
        bool hideSimulatorUiDuringRecording,
        int captureAdapterIndex,
        int captureOutputIndex,
        CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new InvalidOperationException("Output directory must not be empty.");
            }

            JsonObject rootObject;

            if (File.Exists(_configFilePath))
            {
                var json = await File.ReadAllTextAsync(_configFilePath, cancellationToken);
                rootObject = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
            }
            else
            {
                rootObject = new JsonObject();
            }

            var recorderObject = rootObject["Recorder"] as JsonObject ?? new JsonObject();
            rootObject["Recorder"] = recorderObject;

            recorderObject["HideSimulatorUiDuringRecording"] = hideSimulatorUiDuringRecording;
            recorderObject["OutputDirectory"] = outputDirectory;
            recorderObject["CaptureAdapterIndex"] = captureAdapterIndex;
            recorderObject["CaptureOutputIndex"] = captureOutputIndex;

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var updatedJson = rootObject.ToJsonString(options);
            await File.WriteAllTextAsync(_configFilePath, updatedJson, cancellationToken);

            _logger.LogInformation(
                "Recorder settings saved to {ConfigPath}. OutputDirectory={OutputDirectory}, HideSimulatorUi={HideSimulatorUi}, AdapterIndex={AdapterIndex}, OutputIndex={OutputIndex}",
                _configFilePath,
                outputDirectory,
                hideSimulatorUiDuringRecording,
                captureAdapterIndex,
                captureOutputIndex);
        }
    }
}
