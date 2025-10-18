using NAudio.Wave;

namespace CallRecorder.Service.Audio;

public interface IAudioCaptureObserver
{
    // Called with raw captured bytes as delivered by NAudio for the microphone device
    void OnMicChunk(byte[] buffer, WaveFormat format);

    // Called with raw captured bytes as delivered by NAudio for the speaker loopback device
    void OnSpeakerChunk(byte[] buffer, WaveFormat format);
}
