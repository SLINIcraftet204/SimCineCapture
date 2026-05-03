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

        public ConfiguredRecordingFrameSinkFactory(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public IRecordingFrameSink Create()
        {
            return _serviceProvider.GetRequiredService<PngSequenceFrameSink>();
        }
    }
}
