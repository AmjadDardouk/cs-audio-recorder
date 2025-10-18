using System;
using System.IO;
using CallRecorder.Core.Config;

namespace CallRecorder.Service.Audio;

// Stub for WebRTC AudioProcessing-based AEC/NS. This class is structured to allow
// dropping a native webrtc-audio-processing.dll into runtimes/win-x64/native.
// Actual P/Invoke bindings are not included here. Until a valid native DLL and
// bindings are provided, this processor will behave as a simple pass-through.
public sealed class WebRtcAecProcessor : IAecProcessor
{
    private AudioDspConfig _cfg = new AudioDspConfig();
    private int _rate = 48000;
    private int _frameMs = 10;

    // Change this to match the actual DLL name you plan to ship.
    private const string DllName = "webrtc-audio-processing.dll";

    public static bool IsSupported()
    {
        try
        {
            // Detect DLL in typical deployment path (next to exe under RID folder)
            var baseDir = AppContext.BaseDirectory;
            var ridPath = Path.Combine(baseDir, "runtimes", "win-x64", "native", DllName);
            if (File.Exists(ridPath)) return true;

            // Fallback: next to executable
            var exePath = Path.Combine(baseDir, DllName);
            if (File.Exists(exePath)) return true;

            // Also respect an override via environment variable (absolute path)
            var envPath = Environment.GetEnvironmentVariable("WEBRTC_AUDIO_DLL_PATH");
            if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath)) return true;
        }
        catch
        {
            // ignore
        }
        return false;
    }

    public void Configure(AudioDspConfig cfg, int sampleRate, int frameMs)
    {
        _cfg = cfg;
        _rate = sampleRate;
        _frameMs = frameMs;

        // TODO: When native DLL + bindings are available:
        // - Create AudioProcessing instance
        // - Enable AEC, NS, HPF, AGC based on cfg
        // - Configure stream format (48kHz, 10ms frames)
    }

    public void FeedFar(ReadOnlySpan<float> far)
    {
        // TODO: enqueue far-end frames to native AEC reference
    }

    public void ProcessNear(ReadOnlySpan<float> near, Span<float> cleanedOut)
    {
        // TODO: call native API to process near-end with far-end reference.
        // Until native is available, just copy through (no-op).
        near.CopyTo(cleanedOut);
    }

    public void Dispose()
    {
        // TODO: dispose native resources if created
    }

    public void SetStreamDelayMs(int delayMs)
    {
        // TODO: forward delay hint to native APM if supported
        // For stub/fallback, no-op.
    }
}
