using System;
using CallRecorder.Core.Config;

namespace CallRecorder.Service.Audio;

// Enhanced NLMS AEC with improved noise suppression and echo cancellation
// Includes spectral subtraction for noise reduction and better double-talk detection
public sealed class NlmsAecProcessor : IAecProcessor
{
    private AudioDspConfig _cfg = new AudioDspConfig();
    private int _rate = 48000;
    private int _frameMs = 10;

    // Delay alignment (approximate) for far-end reverse stream
    private int _delaySamples = 0;
    private float[] _delayLine = Array.Empty<float>();
    private int _delayIdx = 0;

    // Enhanced NLMS state with longer filter for better echo suppression
    private int _len = 2048;              // ~42 ms @ 48kHz for better echo coverage
    private float _mu = 0.25f;            // Increased step size for faster convergence
    private const float Eps = 1e-8f;      // Smaller epsilon for better precision

    private float[] _w = Array.Empty<float>();  // filter weights
    private float[] _x = Array.Empty<float>();  // ring buffer of far-end
    private int _xIdx;
    
    // Adaptive filter state tracking
    private float _adaptRate = 0.0f;
    private float _errorPower = 0.0f;
    private float _farPower = 0.0f;
    
    // Noise estimation for spectral subtraction
    private float[] _noiseEstimate = Array.Empty<float>();
    private float[] _smoothedSpectrum = Array.Empty<float>();
    private int _noiseUpdateCounter = 0;
    private const int NoiseEstimationFrames = 20;
    
    // Double-talk detection
    private float _nearPower = 0.0f;
    private float _echoReturn = 0.0f;

    // Enhanced high-pass filter (2nd-order Butterworth) for better low-freq noise removal
    private float _hpfPrevIn1, _hpfPrevIn2, _hpfPrevOut1, _hpfPrevOut2;
    private float _hpfA0, _hpfA1, _hpfA2, _hpfB1, _hpfB2;

    public void Configure(AudioDspConfig cfg, int sampleRate, int frameMs)
    {
        _cfg = cfg;
        _rate = sampleRate;
        _frameMs = frameMs;

        // Enhanced filter length for better echo suppression (40-50ms coverage)
        _len = Math.Max(512, Math.Min(4096, (int)Math.Round(_rate * 0.045))); // ~45ms
        _w = new float[_len];
        _x = new float[_len];
        Array.Fill(_w, 0f);
        Array.Fill(_x, 0f);
        _xIdx = 0;
        
        // Initialize noise estimation arrays
        int frameSize = (_rate * _frameMs) / 1000;
        _noiseEstimate = new float[frameSize];
        _smoothedSpectrum = new float[frameSize];
        Array.Fill(_noiseEstimate, 0.001f); // Initial small noise floor

        // Initialize delay line (approximate initial delay)
        _delaySamples = Math.Max(0, (int)Math.Round((_cfg.InitialDelayMs) * (_rate / 1000.0)));
        _delayLine = new float[Math.Max(1, _delaySamples == 0 ? 1 : _delaySamples)];
        _delayIdx = 0;

        // High-pass (2nd-order Butterworth) for better rumble removal
        if (_cfg.HighPass)
        {
            SetHighPass(_cfg.HighPassHz, _rate);
        }
    }

    public void FeedFar(ReadOnlySpan<float> far)
    {
        // Optionally delay far-end samples to better align with near-end path
        for (int i = 0; i < far.Length; i++)
        {
            float s = far[i];
            if (_delaySamples > 0 && _delayLine.Length > 0)
            {
                // Write incoming to delay line and read delayed sample out
                float delayed = _delayLine[_delayIdx];
                _delayLine[_delayIdx] = s;
                _delayIdx++;
                if (_delayIdx == _delayLine.Length) _delayIdx = 0;
                s = delayed;
            }

            // Push (potentially delayed) far-end into adaptive filter ring buffer
            _x[_xIdx] = s;
            _xIdx++;
            if (_xIdx == _len) _xIdx = 0;
        }

        // Update far-end power for double-talk detection
        float power = 0f;
        foreach (var sample in far)
        {
            power += sample * sample;
        }
        _farPower = _farPower * 0.9f + (power / Math.Max(1, far.Length)) * 0.1f;
    }

    public void ProcessNear(ReadOnlySpan<float> near, Span<float> cleanedOut)
    {
        if (cleanedOut.Length != near.Length)
            throw new ArgumentException("cleanedOut length must match near length");

        // Enhanced high-pass filter (2nd-order Butterworth)
        if (_cfg.HighPass)
        {
            for (int i = 0; i < near.Length; i++)
            {
                float y = _hpfA0 * near[i] + _hpfA1 * _hpfPrevIn1 + _hpfA2 * _hpfPrevIn2
                        - _hpfB1 * _hpfPrevOut1 - _hpfB2 * _hpfPrevOut2;
                _hpfPrevIn2 = _hpfPrevIn1;
                _hpfPrevIn1 = near[i];
                _hpfPrevOut2 = _hpfPrevOut1;
                _hpfPrevOut1 = y;
                cleanedOut[i] = y;
            }
        }
        else
        {
            near.CopyTo(cleanedOut);
        }
        
        // Apply AEC processing to remove echo from microphone signal
        if (_cfg.EchoCancellation)
        {
            ProcessAecFrame(cleanedOut);
        }
    }
    
    private void ProcessAecFrame(Span<float> nearSignal)
    {
        // Update near-end power for double-talk detection
        float nearPower = 0f;
        for (int i = 0; i < nearSignal.Length; i++)
        {
            nearPower += nearSignal[i] * nearSignal[i];
        }
        _nearPower = _nearPower * 0.9f + (nearPower / nearSignal.Length) * 0.1f;

        // Enhanced NLMS with double-talk detection for echo cancellation
        for (int n = 0; n < nearSignal.Length; n++)
        {
            float norm = Eps;
            float yhat = 0f;

            int xi = _xIdx - 1;
            if (xi < 0) xi = _len - 1;

            // Compute filter output and normalization
            for (int k = 0; k < _len; k++)
            {
                float xk = _x[xi];
                yhat += _w[k] * xk;
                norm += xk * xk;
                xi--;
                if (xi < 0) xi = _len - 1;
            }

            // Compute error signal (near - predicted echo)
            float e = nearSignal[n] - yhat;
            
            // Update error power for adaptation control
            _errorPower = _errorPower * 0.95f + e * e * 0.05f;
            
            // Enhanced double-talk detection
            float echoReturnLoss = (_farPower > 0.001f) ? _errorPower / _farPower : 1.0f;
            _echoReturn = _echoReturn * 0.9f + echoReturnLoss * 0.1f;
            
            // Adaptive step-size control based on echo characteristics
            float muEff = _mu;
            if (_echoReturn > 0.5f) // Likely double-talk or poor echo path
            {
                muEff *= 0.1f; // Greatly reduce adaptation to preserve speech
            }
            else if (_echoReturn < 0.1f) // Good echo cancellation conditions
            {
                muEff *= 1.5f; // Speed up adaptation for faster convergence
            }
            
            // Apply residual echo suppression
            if (_cfg.NoiseSuppression)
            {
                // Nonlinear processing for residual echo suppression
                float threshold = 0.02f * MathF.Sqrt(_farPower + 0.001f);
                
                if (MathF.Abs(e) < threshold)
                {
                    e *= 0.3f; // Suppress small residuals that are likely echo
                }
                
                // Comfort noise injection to avoid dead silence
                if (MathF.Abs(e) < 0.001f)
                {
                    e += (Random.Shared.NextSingle() - 0.5f) * 0.0001f;
                }
            }
            
            nearSignal[n] = e; // Output the echo-cancelled signal

            // Update filter weights with normalized LMS algorithm
            float g = muEff * e / norm;
            
            // Clip adaptation gain to prevent instability
            g = Math.Max(-0.5f, Math.Min(0.5f, g));
            
            xi = _xIdx - 1;
            if (xi < 0) xi = _len - 1;
            for (int k = 0; k < _len; k++)
            {
                float xk = _x[xi];
                _w[k] += g * xk;
                
                // Weight clipping to prevent overflow
                _w[k] = Math.Max(-2.0f, Math.Min(2.0f, _w[k]));
                
                xi--;
                if (xi < 0) xi = _len - 1;
            }
        }
        
        // Additional spectral noise suppression for enhanced quality
        if (_cfg.NoiseSuppression && _cfg.SuppressionLevel == "High")
        {
            ApplySpectralNoiseReduction(nearSignal);
        }
    }
    
    private void ApplySpectralNoiseReduction(Span<float> audio)
    {
        // Simple spectral subtraction in time domain (approximation)
        for (int i = 0; i < audio.Length; i++)
        {
            float magnitude = MathF.Abs(audio[i]);
            
            // Update noise floor estimate during quiet periods
            if (magnitude < 0.05f)
            {
                _noiseEstimate[i % _noiseEstimate.Length] = 
                    _noiseEstimate[i % _noiseEstimate.Length] * 0.95f + magnitude * 0.05f;
            }
            
            // Subtract noise floor
            float noiseFloor = _noiseEstimate[i % _noiseEstimate.Length] * 2.0f;
            if (magnitude > noiseFloor)
            {
                float sign = audio[i] >= 0 ? 1.0f : -1.0f;
                audio[i] = sign * (magnitude - noiseFloor * 0.5f);
            }
            else
            {
                audio[i] *= 0.1f; // Heavy suppression for noise-only regions
            }
        }
    }

    private void SetHighPass(float cutoffHz, int fs)
    {
        // 2nd-order Butterworth high-pass filter for better low-frequency rejection
        float w = 2.0f * MathF.PI * cutoffHz / fs;
        float cosw = MathF.Cos(w);
        float sinw = MathF.Sin(w);
        float alpha = sinw / MathF.Sqrt(2.0f);
        
        float a0 = 1.0f + alpha;
        _hpfA0 = ((1.0f + cosw) / 2.0f) / a0;
        _hpfA1 = (-(1.0f + cosw)) / a0;
        _hpfA2 = ((1.0f + cosw) / 2.0f) / a0;
        _hpfB1 = (-2.0f * cosw) / a0;
        _hpfB2 = (1.0f - alpha) / a0;
        
        // Reset filter state
        _hpfPrevIn1 = 0f;
        _hpfPrevIn2 = 0f;
        _hpfPrevOut1 = 0f;
        _hpfPrevOut2 = 0f;
    }

    public void Dispose()
    {
        // No unmanaged resources
    }

    public void SetStreamDelayMs(int delayMs)
    {
        // Update target delay and rebuild delay line
        _delaySamples = Math.Max(0, (int)Math.Round(delayMs * (_rate / 1000.0)));
        _delayLine = new float[Math.Max(1, _delaySamples == 0 ? 1 : _delaySamples)];
        _delayIdx = 0;
    }
}
