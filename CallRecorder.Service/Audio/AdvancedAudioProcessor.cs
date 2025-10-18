using System;
using System.Buffers;
using System.Collections.Generic;
using CallRecorder.Core.Config;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace CallRecorder.Service.Audio;

/// <summary>
/// Advanced audio processor with professional-grade DSP features including
/// multi-band processing, advanced limiting, voice enhancement, and quality metrics
/// </summary>
public sealed class AdvancedAudioProcessor : IDisposable
{
    private readonly ILogger<AdvancedAudioProcessor> _logger;
    private readonly AudioDspConfig _config;
    private readonly int _sampleRate;
    private readonly int _channels;
    
    // Memory pools for efficient allocation
    private readonly ArrayPool<float> _floatPool = ArrayPool<float>.Shared;
    private readonly ArrayPool<byte> _bytePool = ArrayPool<byte>.Shared;
    
    // DC removal filters (per channel)
    private readonly float[] _dcFilterState;
    private readonly float _dcFilterCoeff;
    
    // Limiter with lookahead
    private readonly LookaheadLimiter _limiter;
    
    // Quality metrics
    private readonly QualityMetrics _metrics;
    
    // Voice enhancement processors
    private readonly DeEsser _deEsser;
    private readonly VoiceClarity _voiceClarity;
    
    // AGC (Automatic Gain Control)
    private readonly AutomaticGainControl _agc;
    
    // Dithering
    private readonly Dithering _dithering;
    
    public AdvancedAudioProcessor(ILogger<AdvancedAudioProcessor> logger, AudioDspConfig config, int sampleRate, int channels)
    {
        _logger = logger;
        _config = config;
        _sampleRate = sampleRate;
        _channels = channels;
        
        // Initialize DC removal filter
        _dcFilterState = new float[channels];
        float dcCutoff = config.DcFilterCutoffHz / (sampleRate * 0.5f);
        _dcFilterCoeff = (float)Math.Exp(-2.0 * Math.PI * dcCutoff);
        
        // Initialize processors
        _limiter = new LookaheadLimiter(config, sampleRate, channels);
        _metrics = new QualityMetrics(config, sampleRate, channels);
        _deEsser = new DeEsser(config, sampleRate);
        _voiceClarity = new VoiceClarity(config, sampleRate);
        _agc = new AutomaticGainControl(config, sampleRate);
        _dithering = new Dithering(config);
        
        _logger.LogInformation("Advanced audio processor initialized: {sampleRate}Hz, {channels} channels", sampleRate, channels);
    }
    
    /// <summary>
    /// Process audio with all enabled DSP features
    /// </summary>
    public void ProcessAudio(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != output.Length)
            throw new ArgumentException("Input and output spans must have the same length");
        
        // Copy input to output for in-place processing
        input.CopyTo(output);
        
        // Apply DC removal
        if (_config.DcRemoval)
        {
            ApplyDcRemoval(output);
        }
        
        // Apply AGC
        if (_config.Agc)
        {
            _agc.Process(output);
        }
        
        // Apply voice enhancement
        if (_config.VoiceEnhancement)
        {
            if (_config.DeEsser)
            {
                _deEsser.Process(output);
            }
            
            if (_config.VoiceClarity)
            {
                _voiceClarity.Process(output);
            }
        }
        
        // Apply limiting with lookahead
        if (_config.EnableLimiter)
        {
            _limiter.Process(output);
        }
        
        // Apply dithering for quantization
        if (_config.EnableDithering)
        {
            _dithering.Process(output);
        }
        
        // Update quality metrics
        if (_config.EnableQualityMetrics)
        {
            _metrics.UpdateMetrics(output);
        }
    }
    
    private void ApplyDcRemoval(Span<float> audio)
    {
        int samplesPerChannel = audio.Length / _channels;
        
        for (int ch = 0; ch < _channels; ch++)
        {
            float state = _dcFilterState[ch];
            
            for (int i = ch; i < audio.Length; i += _channels)
            {
                float input = audio[i];
                float output = input - state;
                state = input * (1.0f - _dcFilterCoeff) + state * _dcFilterCoeff;
                audio[i] = output;
            }
            
            _dcFilterState[ch] = state;
        }
    }
    
    public QualityMetricsResult GetQualityMetrics() => _config.EnableQualityMetrics ? _metrics.GetResults() : new QualityMetricsResult();
    
    public void Dispose()
    {
        _limiter?.Dispose();
        _metrics?.Dispose();
        _deEsser?.Dispose();
        _voiceClarity?.Dispose();
        _agc?.Dispose();
        _dithering?.Dispose();
    }
}

/// <summary>
/// Lookahead limiter with soft-knee compression
/// </summary>
internal sealed class LookaheadLimiter : IDisposable
{
    private readonly AudioDspConfig _config;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _lookaheadSamples;
    private readonly float[] _delayBuffer;
    private readonly float[] _envelope;
    private int _writeIndex;
    private float _gain;
    private readonly float _attack;
    private readonly float _release;
    
    public LookaheadLimiter(AudioDspConfig config, int sampleRate, int channels)
    {
        _config = config;
        _sampleRate = sampleRate;
        _channels = channels;
        _lookaheadSamples = (int)(config.LimiterLookaheadMs * sampleRate / 1000.0);
        _delayBuffer = new float[_lookaheadSamples * channels];
        _envelope = new float[_lookaheadSamples];
        _gain = 1.0f;
        
        // Calculate attack/release coefficients
        _attack = (float)Math.Exp(-1.0 / (config.LimiterLookaheadMs * sampleRate / 1000.0));
        _release = (float)Math.Exp(-1.0 / (config.LimiterReleaseMs * sampleRate / 1000.0));
    }
    
    public void Process(Span<float> audio)
    {
        float threshold = DbToLinear(_config.LimiterThresholdDbfs);
        float ceiling = DbToLinear(_config.LimiterCeilingDbfs);
        
        for (int i = 0; i < audio.Length; i += _channels)
        {
            // Calculate peak across all channels for this sample
            float peak = 0f;
            for (int ch = 0; ch < _channels; ch++)
            {
                peak = Math.Max(peak, Math.Abs(audio[i + ch]));
            }
            
            // Store in delay buffer
            int delayIndex = _writeIndex * _channels;
            for (int ch = 0; ch < _channels; ch++)
            {
                _delayBuffer[delayIndex + ch] = audio[i + ch];
            }
            
            // Calculate required gain reduction
            float targetGain = 1.0f;
            if (peak > threshold)
            {
                targetGain = threshold / peak;
                if (_config.SoftKneeLimiter)
                {
                    // Apply soft knee
                    float ratio = 0.2f; // Soft knee ratio
                    targetGain = (float)Math.Pow(targetGain, ratio);
                }
            }
            
            // Smooth gain changes
            if (targetGain < _gain)
                _gain = targetGain + (_gain - targetGain) * _attack;
            else
                _gain = targetGain + (_gain - targetGain) * _release;
            
            // Apply gain to delayed samples
            int readIndex = (_writeIndex + 1) % _lookaheadSamples;
            int readDelayIndex = readIndex * _channels;
            
            for (int ch = 0; ch < _channels; ch++)
            {
                float sample = _delayBuffer[readDelayIndex + ch] * _gain;
                // Hard ceiling to prevent any overshoot
                sample = Math.Max(-ceiling, Math.Min(ceiling, sample));
                audio[i + ch] = sample;
            }
            
            _writeIndex = (_writeIndex + 1) % _lookaheadSamples;
        }
    }
    
    private static float DbToLinear(float db) => (float)Math.Pow(10.0, db / 20.0);
    
    public void Dispose()
    {
        // No unmanaged resources
    }
}

/// <summary>
/// Quality metrics monitoring
/// </summary>
internal sealed class QualityMetrics : IDisposable
{
    private readonly AudioDspConfig _config;
    private double _rmsSum;
    private double _peakMax;
    private long _sampleCount;
    private readonly object _lockObject = new();
    
    public QualityMetrics(AudioDspConfig config, int sampleRate, int channels)
    {
        _config = config;
    }
    
    public void UpdateMetrics(ReadOnlySpan<float> audio)
    {
        lock (_lockObject)
        {
            foreach (var sample in audio)
            {
                double abs = Math.Abs(sample);
                _rmsSum += sample * sample;
                _peakMax = Math.Max(_peakMax, abs);
                _sampleCount++;
            }
        }
    }
    
    public QualityMetricsResult GetResults()
    {
        lock (_lockObject)
        {
            if (_sampleCount == 0)
                return new QualityMetricsResult();
            
            double rms = Math.Sqrt(_rmsSum / _sampleCount);
            double rmsDb = 20.0 * Math.Log10(Math.Max(rms, 1e-10));
            double peakDb = 20.0 * Math.Log10(Math.Max(_peakMax, 1e-10));
            
            return new QualityMetricsResult
            {
                RmsLevelDb = (float)rmsDb,
                PeakLevelDb = (float)peakDb,
                DynamicRange = (float)(peakDb - rmsDb),
                SampleCount = _sampleCount
            };
        }
    }
    
    public void Dispose()
    {
        // No unmanaged resources
    }
}

/// <summary>
/// De-esser for reducing sibilance
/// </summary>
internal sealed class DeEsser : IDisposable
{
    private readonly float _frequency;
    private readonly float _threshold;
    private readonly BandpassFilter _detector;
    private readonly Compressor _compressor;
    
    public DeEsser(AudioDspConfig config, int sampleRate)
    {
        _frequency = config.DeEsserFrequencyHz;
        _threshold = DbToLinear(config.DeEsserThresholdDb);
        _detector = new BandpassFilter(sampleRate, _frequency, 1000f); // 1kHz bandwidth
        _compressor = new Compressor(4.0f, 1.0f, 10.0f); // 4:1 ratio, fast attack/release
    }
    
    public void Process(Span<float> audio)
    {
        for (int i = 0; i < audio.Length; i++)
        {
            float detected = _detector.Process(audio[i]);
            float envelope = Math.Abs(detected);
            
            if (envelope > _threshold)
            {
                float gain = _compressor.Process(envelope / _threshold);
                audio[i] *= gain;
            }
        }
    }
    
    private static float DbToLinear(float db) => (float)Math.Pow(10.0, db / 20.0);
    
    public void Dispose()
    {
        _detector?.Dispose();
        _compressor?.Dispose();
    }
}

/// <summary>
/// Voice clarity enhancement
/// </summary>
internal sealed class VoiceClarity : IDisposable
{
    private readonly PeakingFilter _presence;
    private readonly HighShelfFilter _clarity;
    
    public VoiceClarity(AudioDspConfig config, int sampleRate)
    {
        // Enhance presence frequencies (2-4kHz)
        _presence = new PeakingFilter(sampleRate, 3000f, 2.0f, 2.0f);
        // Add clarity to high frequencies
        _clarity = new HighShelfFilter(sampleRate, 6000f, 1.5f);
    }
    
    public void Process(Span<float> audio)
    {
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] = _clarity.Process(_presence.Process(audio[i]));
        }
    }
    
    public void Dispose()
    {
        _presence?.Dispose();
        _clarity?.Dispose();
    }
}

/// <summary>
/// Automatic Gain Control
/// </summary>
internal sealed class AutomaticGainControl : IDisposable
{
    private readonly float _targetLevel;
    private readonly float _maxGain;
    private readonly float _attack;
    private readonly float _release;
    private float _envelope;
    private float _gain;
    
    public AutomaticGainControl(AudioDspConfig config, int sampleRate)
    {
        _targetLevel = DbToLinear(config.AgcTargetDb);
        _maxGain = DbToLinear(config.AgcMaxGainDb);
        _attack = (float)Math.Exp(-1.0 / (config.AgcAttackMs * sampleRate / 1000.0));
        _release = (float)Math.Exp(-1.0 / (config.AgcReleaseMs * sampleRate / 1000.0));
        _gain = 1.0f;
    }
    
    public void Process(Span<float> audio)
    {
        for (int i = 0; i < audio.Length; i++)
        {
            float input = audio[i];
            float inputLevel = Math.Abs(input);
            
            // Update envelope
            float coeff = inputLevel > _envelope ? _attack : _release;
            _envelope = inputLevel + (_envelope - inputLevel) * coeff;
            
            // Calculate required gain
            float targetGain = 1.0f;
            if (_envelope > 1e-6f)
            {
                targetGain = Math.Min(_targetLevel / _envelope, _maxGain);
            }
            
            // Smooth gain changes
            _gain = targetGain + (_gain - targetGain) * 0.999f;
            
            audio[i] = input * _gain;
        }
    }
    
    private static float DbToLinear(float db) => (float)Math.Pow(10.0, db / 20.0);
    
    public void Dispose()
    {
        // No unmanaged resources
    }
}

/// <summary>
/// Dithering for better quantization
/// </summary>
internal sealed class Dithering : IDisposable
{
    private readonly string _type;
    private readonly float _amount;
    private readonly Random _random;
    
    public Dithering(AudioDspConfig config)
    {
        _type = config.DitherType;
        _amount = DbToLinear(config.DitherAmountDb);
        _random = new Random();
    }
    
    public void Process(Span<float> audio)
    {
        for (int i = 0; i < audio.Length; i++)
        {
            float dither = _type switch
            {
                "TriangularPdf" => ((float)_random.NextDouble() - (float)_random.NextDouble()) * _amount,
                "RectangularPdf" => ((float)_random.NextDouble() - 0.5f) * 2.0f * _amount,
                _ => ((float)_random.NextDouble() - 0.5f) * _amount
            };
            
            audio[i] += dither;
        }
    }
    
    private static float DbToLinear(float db) => (float)Math.Pow(10.0, db / 20.0);
    
    public void Dispose()
    {
        // No unmanaged resources
    }
}

/// <summary>
/// Simple filter implementations
/// </summary>
internal sealed class BandpassFilter : IDisposable
{
    private float _x1, _x2, _y1, _y2;
    private readonly float _a0, _a1, _a2, _b1, _b2;
    
    public BandpassFilter(int sampleRate, float centerFreq, float bandwidth)
    {
        float w = 2.0f * MathF.PI * centerFreq / sampleRate;
        float cosw = MathF.Cos(w);
        float sinw = MathF.Sin(w);
        float alpha = sinw * MathF.Sinh(MathF.Log(2.0f) / 2.0f * bandwidth * w / sinw);
        
        float norm = 1.0f / (1.0f + alpha);
        _a0 = alpha * norm;
        _a1 = 0.0f;
        _a2 = -alpha * norm;
        _b1 = -2.0f * cosw * norm;
        _b2 = (1.0f - alpha) * norm;
    }
    
    public float Process(float input)
    {
        float output = _a0 * input + _a1 * _x1 + _a2 * _x2 - _b1 * _y1 - _b2 * _y2;
        _x2 = _x1; _x1 = input;
        _y2 = _y1; _y1 = output;
        return output;
    }
    
    public void Dispose() { }
}

internal sealed class PeakingFilter : IDisposable
{
    private float _x1, _x2, _y1, _y2;
    private readonly float _a0, _a1, _a2, _b1, _b2;
    
    public PeakingFilter(int sampleRate, float freq, float q, float gainDb)
    {
        float A = MathF.Pow(10.0f, gainDb / 40.0f);
        float w = 2.0f * MathF.PI * freq / sampleRate;
        float cosw = MathF.Cos(w);
        float sinw = MathF.Sin(w);
        float alpha = sinw / (2.0f * q);
        
        float norm = 1.0f / (1.0f + alpha / A);
        _a0 = (1.0f + alpha * A) * norm;
        _a1 = (-2.0f * cosw) * norm;
        _a2 = (1.0f - alpha * A) * norm;
        _b1 = (-2.0f * cosw) * norm;
        _b2 = (1.0f - alpha / A) * norm;
    }
    
    public float Process(float input)
    {
        float output = _a0 * input + _a1 * _x1 + _a2 * _x2 - _b1 * _y1 - _b2 * _y2;
        _x2 = _x1; _x1 = input;
        _y2 = _y1; _y1 = output;
        return output;
    }
    
    public void Dispose() { }
}

internal sealed class HighShelfFilter : IDisposable
{
    private float _x1, _x2, _y1, _y2;
    private readonly float _a0, _a1, _a2, _b1, _b2;
    
    public HighShelfFilter(int sampleRate, float freq, float gainDb)
    {
        float A = MathF.Pow(10.0f, gainDb / 40.0f);
        float w = 2.0f * MathF.PI * freq / sampleRate;
        float cosw = MathF.Cos(w);
        float sinw = MathF.Sin(w);
        float S = 1.0f;
        float beta = MathF.Sqrt(A) / 1.0f;
        
        float norm = 1.0f / ((A + 1.0f) - (A - 1.0f) * cosw + beta * sinw);
        _a0 = A * ((A + 1.0f) + (A - 1.0f) * cosw + beta * sinw) * norm;
        _a1 = -2.0f * A * ((A - 1.0f) + (A + 1.0f) * cosw) * norm;
        _a2 = A * ((A + 1.0f) + (A - 1.0f) * cosw - beta * sinw) * norm;
        _b1 = 2.0f * ((A - 1.0f) - (A + 1.0f) * cosw) * norm;
        _b2 = ((A + 1.0f) - (A - 1.0f) * cosw - beta * sinw) * norm;
    }
    
    public float Process(float input)
    {
        float output = _a0 * input + _a1 * _x1 + _a2 * _x2 - _b1 * _y1 - _b2 * _y2;
        _x2 = _x1; _x1 = input;
        _y2 = _y1; _y1 = output;
        return output;
    }
    
    public void Dispose() { }
}

internal sealed class Compressor : IDisposable
{
    private readonly float _ratio;
    private readonly float _attack;
    private readonly float _release;
    private float _envelope;
    
    public Compressor(float ratio, float attackMs, float releaseMs)
    {
        _ratio = ratio;
        _attack = (float)Math.Exp(-1.0 / (attackMs * 48000 / 1000.0)); // Assume 48kHz for simplicity
        _release = (float)Math.Exp(-1.0 / (releaseMs * 48000 / 1000.0));
    }
    
    public float Process(float level)
    {
        // Update envelope
        float coeff = level > _envelope ? _attack : _release;
        _envelope = level + (_envelope - level) * coeff;
        
        // Apply compression
        if (_envelope > 1.0f)
        {
            float excess = _envelope - 1.0f;
            float compressed = 1.0f + excess / _ratio;
            return compressed / _envelope;
        }
        
        return 1.0f;
    }
    
    public void Dispose() { }
}

/// <summary>
/// Quality metrics result
/// </summary>
public struct QualityMetricsResult
{
    public float RmsLevelDb { get; set; }
    public float PeakLevelDb { get; set; }
    public float DynamicRange { get; set; }
    public long SampleCount { get; set; }
}
