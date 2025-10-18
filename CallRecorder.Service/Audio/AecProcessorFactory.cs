using CallRecorder.Core.Config;
using Microsoft.Extensions.Logging;

namespace CallRecorder.Service.Audio;

/// <summary>
/// Factory that returns a WebRTC-based AEC if available/configured, else a managed NLMS fallback.
/// </summary>
public sealed class AecProcessorFactory : IAecProcessorFactory
{
    private readonly ILoggerFactory _loggerFactory;
    
    public AecProcessorFactory(ILoggerFactory loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
    }
    
    public IAecProcessor Create(AudioDspConfig cfg)
    {
        // Prefer native WebRTC AEC3 implementation when DLL is present and AEC enabled
        if (cfg.EchoCancellation && WebRtcAec3Processor.IsSupported())
        {
            var logger = _loggerFactory?.CreateLogger<WebRtcAec3Processor>();
            return new WebRtcAec3Processor(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WebRtcAec3Processor>.Instance);
        }

        // Fallback to managed WebRTC-style AEC implementation
        if (cfg.EchoCancellation)
        {
            var logger = _loggerFactory?.CreateLogger<ManagedWebRtcAecProcessor>();
            return new ManagedWebRtcAecProcessor(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ManagedWebRtcAecProcessor>.Instance);
        }

        // Secondary fallback to original NLMS implementation
        return new NlmsAecProcessor();
    }
}
