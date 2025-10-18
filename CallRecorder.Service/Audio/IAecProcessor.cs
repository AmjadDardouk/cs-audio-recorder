using CallRecorder.Core.Config;

namespace CallRecorder.Service.Audio;

/// <summary>
/// Acoustic echo canceller/voice pre-processor contract.
/// Far-end = speaker loopback (what PC plays), Near-end = microphone.
/// Implementations may include AEC, NS, HPF, AGC.
/// </summary>
public interface IAecProcessor : IDisposable
{
    /// <summary>
    /// Configure the processor (sampleRate in Hz, frameMs ~10ms typical).
    /// </summary>
    void Configure(AudioDspConfig cfg, int sampleRate, int frameMs);

    /// <summary>
    /// Feed far-end (speaker) samples for use as echo reference.
    /// Values expected in float32 [-1,1].
    /// </summary>
    void FeedFar(ReadOnlySpan<float> far);

    /// <summary>
    /// Process near-end (mic) samples into cleanedOut.
    /// Input/Output arrays may be same-length; cleanedOut is overwritten.
    /// </summary>
    void ProcessNear(ReadOnlySpan<float> near, Span<float> cleanedOut);

    /// <summary>
    /// Hint the estimated overall stream delay (ms) between render and capture paths.
    /// Implementations may ignore if auto delay-agnostic adaptation is enabled.
    /// </summary>
    void SetStreamDelayMs(int delayMs);
}
