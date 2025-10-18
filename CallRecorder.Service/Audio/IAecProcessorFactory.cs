using CallRecorder.Core.Config;

namespace CallRecorder.Service.Audio;

public interface IAecProcessorFactory
{
    /// <summary>
    /// Create a new IAecProcessor instance per recording.
    /// May return a WebRTC-based processor if available/configured,
    /// else a managed fallback (NLMS).
    /// </summary>
    IAecProcessor Create(AudioDspConfig cfg);
}
