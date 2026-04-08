using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SimCineCapture.Core.Abstractions;
using SimCineCapture.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Capture.Services
{
    public sealed class ConfiguredRecordingFrameSinkFactory : IRecordingFrameSinkFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IOptions<AppSettings> _appSettings;

        public ConfiguredRecordingFrameSinkFactory(
            IServiceProvider serviceProvider,
            IOptions<AppSettings> appSettings)
        {
            _serviceProvider = serviceProvider;
            _appSettings = appSettings;
        }

        public IRecordingFrameSink Create()
        {
            var frameSink = _appSettings.Value.Recorder.FrameSink?.Trim().ToLowerInvariant();

            return frameSink switch
            {
                "png" => _serviceProvider.GetRequiredService<PngSequenceFrameSink>(),
                "pngsequence" => _serviceProvider.GetRequiredService<PngSequenceFrameSink>(),
                "png-sequence" => _serviceProvider.GetRequiredService<PngSequenceFrameSink>(),
                _ => _serviceProvider.GetRequiredService<FfmpegProcessFrameSink>()
            };
        }
    }
}
