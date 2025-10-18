using System;

namespace CallRecorder.Service.Audio;

/// <summary>
/// Simple Voice Activity Detector (VAD) to distinguish voice from noise
/// </summary>
public class VoiceActivityDetector
{
    private readonly int _sampleRate;
    private readonly float _energyThreshold;
    private readonly float _zcThreshold;
    private float[] _buffer;
    private int _bufferSize;
    
    // Moving averages for smoothing
    private float _avgEnergy = 0f;
    private float _avgZcr = 0f;
    private const float SmoothingFactor = 0.95f;

    public VoiceActivityDetector(int sampleRate = 48000)
    {
        _sampleRate = sampleRate;
        _bufferSize = sampleRate / 50; // 20ms frame
        _buffer = new float[_bufferSize];
        
        // Thresholds tuned for voice detection (normalized)
        _energyThreshold = 0.01f; // Energy threshold for voice (RMS^2) - tightened to reduce false positives
        _zcThreshold = 0.2f; // Normalized ZCR target (0..1), not used directly
    }

    public bool DetectVoice(ReadOnlySpan<byte> audioData, int bytesPerSample)
    {
        // Convert bytes to float samples
        int sampleCount = Math.Min(audioData.Length / bytesPerSample, _bufferSize);
        
        for (int i = 0; i < sampleCount && i < _bufferSize; i++)
        {
            if (bytesPerSample == 2) // 16-bit
            {
                short sample = BitConverter.ToInt16(audioData.Slice(i * 2, 2));
                _buffer[i] = sample / 32768f;
            }
            else if (bytesPerSample == 4) // 32-bit float
            {
                _buffer[i] = BitConverter.ToSingle(audioData.Slice(i * 4, 4));
            }
        }

        // Calculate energy
        float energy = CalculateEnergy(_buffer, sampleCount);
        
        // Calculate zero crossing rate
        float zcr = CalculateZeroCrossingRate(_buffer, sampleCount);
        
        // Smooth the values
        _avgEnergy = _avgEnergy * SmoothingFactor + energy * (1 - SmoothingFactor);
        _avgZcr = _avgZcr * SmoothingFactor + zcr * (1 - SmoothingFactor);
        
        // Voice detection logic:
        // - Energy should be above threshold (indicates sound)
        // - ZCR should be in voice range (not too high like noise, not too low like silence)
        bool hasEnergy = _avgEnergy > _energyThreshold;
        // Normalized ZCR (0..1). Voice typically 0.02..0.30, noise/music often higher.
        bool hasVoiceZcr = _avgZcr > 0.02f && _avgZcr < 0.25f;
        
        return hasEnergy && hasVoiceZcr;
    }

    private float CalculateEnergy(float[] samples, int count)
    {
        float sum = 0f;
        for (int i = 0; i < count; i++)
        {
            sum += samples[i] * samples[i];
        }
        return sum / count;
    }

    private float CalculateZeroCrossingRate(float[] samples, int count)
    {
        if (count < 2) return 0f;
        
        int crossings = 0;
        for (int i = 1; i < count; i++)
        {
            if ((samples[i - 1] >= 0 && samples[i] < 0) ||
                (samples[i - 1] < 0 && samples[i] >= 0))
            {
                crossings++;
            }
        }
        
        // Return normalized zero crossing rate per sample (0..1)
        return (float)crossings / Math.Max(1, count - 1);
    }
}
