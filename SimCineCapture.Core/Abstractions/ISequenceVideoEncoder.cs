using SimCineCapture.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace SimCineCapture.Core.Abstractions
{
    public interface ISequenceVideoEncoder
    {
        Task EncodeAsync(
            SequenceVideoEncodingRequest request,
            CancellationToken cancellationToken = default);
    }
}
