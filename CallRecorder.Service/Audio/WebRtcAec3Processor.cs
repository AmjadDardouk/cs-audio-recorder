using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CallRecorder.Core.Config;
using Microsoft.Extensions.Logging;

namespace CallRecorder.Service.Audio;

/// <summary>
/// WebRTC AEC3 processor with proper native bindings for echo cancellation.
/// Ensures correct call order: ReverseStream before ProcessStream.
/// </summary>
public sealed class WebRtcAec3Processor : IAecProcessor
{
    private readonly ILogger<WebRtcAec3Processor> _logger;
    private IntPtr _apmHandle = IntPtr.Zero;
    private readonly object _apmLock = new();
    
    // Ring buffer for reverse stream (far-end) with timestamps
    private readonly Queue<TimestampedFrame> _reverseBuffer = new();
    private readonly int _reverseBufferMaxFrames = 20; // 200ms at 10ms frames
    
    // Configuration
    private int _sampleRate = 48000;
    private int _frameSamples = 480; // 10ms at 48kHz
    private int _streamDelayMs = 0;
    
    // Diagnostics
    private bool _diagnosticsEnabled = false;
    private long _framesProcessed = 0;
    private long _reverseFramesFed = 0;
    private double _sumEchoReturn = 0;
    private double _sumNearPower = 0;
    private double _maxCorrelation = 0;
    
    // Native WebRTC Audio Processing bindings
    private const string DllName = "webrtc_audio_processing.dll";
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr webrtc_apm_create();
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void webrtc_apm_destroy(IntPtr apm);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int webrtc_apm_initialize(IntPtr apm, int sample_rate_hz);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int webrtc_apm_set_stream_delay_ms(IntPtr apm, int delay_ms);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int webrtc_apm_process_reverse_stream(IntPtr apm, float[] data, int num_samples);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int webrtc_apm_process_stream(IntPtr apm, float[] data, int num_samples);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int webrtc_apm_set_echo_cancellation(IntPtr apm, bool enable);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int webrtc_apm_set_echo_suppression_level(IntPtr apm, int level);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int webrtc_apm_set_noise_suppression(IntPtr apm, bool enable, int level);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int webrtc_apm_set_high_pass_filter(IntPtr apm, bool enable);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int webrtc_apm_set_gain_control(IntPtr apm, bool enable, int target_level_dbfs);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int webrtc_apm_get_echo_return_loss(IntPtr apm, out float erl);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int webrtc_apm_get_echo_return_loss_enhancement(IntPtr apm, out float erle);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int webrtc_apm_get_residual_echo_likelihood(IntPtr apm, out float likelihood);

    public WebRtcAec3Processor(ILogger<WebRtcAec3Processor> logger)
    {
        _logger = logger;
    }

    public static bool IsSupported()
    {
        try
        {
            var handle = webrtc_apm_create();
            if (handle != IntPtr.Zero)
            {
                webrtc_apm_destroy(handle);
                return true;
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
        return false;
    }

    public void Configure(AudioDspConfig cfg, int sampleRate, int frameMs)
    {
        lock (_apmLock)
        {
            _sampleRate = sampleRate;
            _frameSamples = (sampleRate * frameMs) / 1000;
            _diagnosticsEnabled = cfg.DiagnosticsEnableMonoDumps;
            
            try
            {
                // Create APM instance
                if (_apmHandle != IntPtr.Zero)
                {
                    webrtc_apm_destroy(_apmHandle);
                }
                
                _apmHandle = webrtc_apm_create();
                if (_apmHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to create WebRTC APM instance");
                }
                
                // Initialize with sample rate
                int result = webrtc_apm_initialize(_apmHandle, sampleRate);
                if (result != 0)
                {
                    throw new InvalidOperationException($"Failed to initialize APM: {result}");
                }
                
                // Configure AEC3 with aggressive settings
                webrtc_apm_set_echo_cancellation(_apmHandle, true);
                
                // Set suppression level: 0=Low, 1=Moderate, 2=High, 3=VeryHigh
                int suppressionLevel = cfg.EchoSuppressionLevel switch
                {
                    "Low" => 0,
                    "Moderate" => 1,
                    "High" => 2,
                    "VeryHigh" => 3,
                    _ => 3 // Default to VeryHigh
                };
                webrtc_apm_set_echo_suppression_level(_apmHandle, suppressionLevel);
                
                // Configure noise suppression
                if (cfg.NoiseSuppression)
                {
                    int nsLevel = cfg.SuppressionLevel switch
                    {
                        "Low" => 0,
                        "Moderate" => 1,
                        "High" => 2,
                        "VeryHigh" => 3,
                        _ => 2 // Default to High
                    };
                    webrtc_apm_set_noise_suppression(_apmHandle, true, nsLevel);
                }
                
                // High-pass filter
                if (cfg.HighPass)
                {
                    webrtc_apm_set_high_pass_filter(_apmHandle, true);
                }
                
                // Gain control with limiter at -1 dBFS
                if (cfg.Agc)
                {
                    webrtc_apm_set_gain_control(_apmHandle, true, -1);
                }
                
                _logger.LogInformation("WebRTC AEC3 configured: rate={rate}Hz, frameMs={frameMs}, suppression={level}",
                    sampleRate, frameMs, cfg.EchoSuppressionLevel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure WebRTC AEC3");
                _apmHandle = IntPtr.Zero;
                throw;
            }
        }
    }

    public void FeedFar(ReadOnlySpan<float> far)
    {
        lock (_apmLock)
        {
            if (_apmHandle == IntPtr.Zero) return;
            
            // Add to reverse buffer with timestamp
            var frame = new TimestampedFrame(far.ToArray(), DateTime.UtcNow);
            _reverseBuffer.Enqueue(frame);
            
            // Maintain buffer size
            while (_reverseBuffer.Count > _reverseBufferMaxFrames)
            {
                _reverseBuffer.Dequeue();
            }
            
            _reverseFramesFed++;
        }
    }

    public void ProcessNear(ReadOnlySpan<float> near, Span<float> cleanedOut)
    {
        lock (_apmLock)
        {
            if (_apmHandle == IntPtr.Zero)
            {
                near.CopyTo(cleanedOut);
                return;
            }
            
            // CRITICAL: Process reverse stream BEFORE near stream
            // This is the key to proper AEC operation
            
            // Get aligned far-end frame from ring buffer
            TimestampedFrame? farFrame = null;
            if (_reverseBuffer.Count > 0)
            {
                // Use frame aligned with current delay estimate
                int targetIndex = Math.Min(_reverseBuffer.Count - 1, _streamDelayMs / 10); // 10ms frames
                var frames = _reverseBuffer.ToArray();
                if (targetIndex >= 0 && targetIndex < frames.Length)
                {
                    farFrame = frames[targetIndex];
                }
                else if (frames.Length > 0)
                {
                    farFrame = frames[0]; // Fallback to oldest
                }
            }
            
            // Process reverse stream first (critical!)
            if (farFrame != null)
            {
                try
                {
                    webrtc_apm_process_reverse_stream(_apmHandle, farFrame.Samples, farFrame.Samples.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process reverse stream");
                }
            }
            
            // Set stream delay
            try
            {
                webrtc_apm_set_stream_delay_ms(_apmHandle, _streamDelayMs);
            }
            catch { }
            
            // Process near stream
            var nearArray = near.ToArray();
            var processedArray = new float[nearArray.Length];
            Array.Copy(nearArray, processedArray, nearArray.Length);
            
            try
            {
                int result = webrtc_apm_process_stream(_apmHandle, processedArray, processedArray.Length);
                if (result != 0)
                {
                    _logger.LogWarning("APM process stream returned error: {result}", result);
                    near.CopyTo(cleanedOut);
                    return;
                }
                
                processedArray.CopyTo(cleanedOut);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process near stream");
                near.CopyTo(cleanedOut);
                return;
            }
            
            _framesProcessed++;
            
            // Diagnostics
            if (_diagnosticsEnabled && (_framesProcessed % 100) == 0)
            {
                try
                {
                    float erl, erle, likelihood;
                    webrtc_apm_get_echo_return_loss(_apmHandle, out erl);
                    webrtc_apm_get_echo_return_loss_enhancement(_apmHandle, out erle);
                    webrtc_apm_get_residual_echo_likelihood(_apmHandle, out likelihood);
                    
                    _logger.LogDebug("AEC metrics: ERL={erl:F1}dB, ERLE={erle:F1}dB, ResidualEcho={likelihood:F2}",
                        erl, erle, likelihood);
                }
                catch { }
            }
        }
    }

    public void SetStreamDelayMs(int delayMs)
    {
        lock (_apmLock)
        {
            _streamDelayMs = Math.Max(0, Math.Min(200, delayMs)); // Clamp to [0, 200]ms
            
            if (_apmHandle != IntPtr.Zero)
            {
                try
                {
                    webrtc_apm_set_stream_delay_ms(_apmHandle, _streamDelayMs);
                }
                catch { }
            }
        }
    }

    public void Dispose()
    {
        lock (_apmLock)
        {
            if (_apmHandle != IntPtr.Zero)
            {
                try
                {
                    webrtc_apm_destroy(_apmHandle);
                }
                catch { }
                _apmHandle = IntPtr.Zero;
            }
            
            _reverseBuffer.Clear();
        }
    }

    private class TimestampedFrame
    {
        public float[] Samples { get; }
        public DateTime Timestamp { get; }
        
        public TimestampedFrame(float[] samples, DateTime timestamp)
        {
            Samples = samples;
            Timestamp = timestamp;
        }
    }
}

/// <summary>
/// Fallback WebRTC AEC processor using managed C# implementation
/// when native DLL is not available. Implements NLMS-based AEC.
/// </summary>
public sealed class ManagedWebRtcAecProcessor : IAecProcessor
{
    private readonly ILogger<ManagedWebRtcAecProcessor> _logger;
    private AudioDspConfig _cfg = new();
    private int _sampleRate = 48000;
    private int _frameSamples = 480;
    
    // Ring buffer for far-end reference
    private readonly Queue<float[]> _farBuffer = new();
    private readonly int _maxFarFrames = 20; // 200ms at 10ms
    
    // NLMS adaptive filter
    private float[] _adaptiveFilter;
    private readonly int _filterLength = 512; // ~10ms at 48kHz
    private readonly float _stepSize = 0.1f;
    private readonly float _regularization = 0.01f;
    
    // Residual echo suppressor
    private float[] _suppressionGains;
    private readonly int _fftSize = 512;
    
    // Stream delay
    private int _streamDelayMs = 0;
    
    public ManagedWebRtcAecProcessor(ILogger<ManagedWebRtcAecProcessor> logger)
    {
        _logger = logger;
        _adaptiveFilter = new float[_filterLength];
        _suppressionGains = new float[_fftSize / 2 + 1];
        Array.Fill(_suppressionGains, 1.0f);
    }

    public void Configure(AudioDspConfig cfg, int sampleRate, int frameMs)
    {
        _cfg = cfg;
        _sampleRate = sampleRate;
        _frameSamples = (sampleRate * frameMs) / 1000;
        
        _logger.LogInformation("Managed AEC configured: rate={rate}Hz, frameMs={frameMs}",
            sampleRate, frameMs);
    }

    public void FeedFar(ReadOnlySpan<float> far)
    {
        var farArray = far.ToArray();
        
        lock (_farBuffer)
        {
            _farBuffer.Enqueue(farArray);
            
            // Keep buffer bounded
            while (_farBuffer.Count > _maxFarFrames)
            {
                _farBuffer.Dequeue();
            }
        }
    }

    public void ProcessNear(ReadOnlySpan<float> near, Span<float> cleanedOut)
    {
        // Get aligned far-end reference
        float[] farReference = null;
        lock (_farBuffer)
        {
            if (_farBuffer.Count > 0)
            {
                // Calculate frame index based on delay
                int delayFrames = _streamDelayMs / 10; // 10ms frames
                int targetIndex = Math.Min(_farBuffer.Count - 1, delayFrames);
                
                var frames = _farBuffer.ToArray();
                if (targetIndex >= 0 && targetIndex < frames.Length)
                {
                    farReference = frames[targetIndex];
                }
            }
        }
        
        if (farReference == null)
        {
            // No far-end reference, pass through with basic noise suppression
            near.CopyTo(cleanedOut);
            if (_cfg.NoiseSuppression)
            {
                ApplyNoiseSuppressionInPlace(cleanedOut);
            }
            return;
        }
        
        // NLMS adaptive filtering
        var nearArray = near.ToArray();
        var echoEstimate = new float[nearArray.Length];
        
        for (int i = 0; i < nearArray.Length; i++)
        {
            // Compute echo estimate
            float echo = 0;
            for (int j = 0; j < Math.Min(_filterLength, farReference.Length); j++)
            {
                if (i - j >= 0 && i - j < farReference.Length)
                {
                    echo += _adaptiveFilter[j] * farReference[i - j];
                }
            }
            echoEstimate[i] = echo;
            
            // Compute error (echo-cancelled signal)
            float error = nearArray[i] - echo;
            cleanedOut[i] = error;
            
            // Update adaptive filter (NLMS)
            float power = 0;
            for (int j = 0; j < Math.Min(_filterLength, farReference.Length); j++)
            {
                if (i - j >= 0 && i - j < farReference.Length)
                {
                    power += farReference[i - j] * farReference[i - j];
                }
            }
            
            if (power > _regularization)
            {
                float updateFactor = _stepSize * error / (power + _regularization);
                for (int j = 0; j < Math.Min(_filterLength, farReference.Length); j++)
                {
                    if (i - j >= 0 && i - j < farReference.Length)
                    {
                        _adaptiveFilter[j] += updateFactor * farReference[i - j];
                    }
                }
            }
        }
        
        // Apply residual echo suppression
        if (_cfg.EchoSuppressionLevel == "VeryHigh")
        {
            ApplyResidualSuppressionInPlace(cleanedOut, echoEstimate);
        }
        
        // Apply noise suppression if enabled
        if (_cfg.NoiseSuppression)
        {
            ApplyNoiseSuppressionInPlace(cleanedOut);
        }
        
        // Apply high-pass filter if enabled
        if (_cfg.HighPass)
        {
            ApplyHighPassInPlace(cleanedOut);
        }
    }

    public void SetStreamDelayMs(int delayMs)
    {
        _streamDelayMs = Math.Max(0, Math.Min(200, delayMs));
    }

    private void ApplyResidualSuppressionInPlace(Span<float> signal, float[] echoEstimate)
    {
        // Simple spectral subtraction for residual echo
        for (int i = 0; i < signal.Length; i++)
        {
            float suppression = 1.0f - Math.Min(1.0f, Math.Abs(echoEstimate[i]) * 2);
            signal[i] *= suppression;
        }
    }

    private void ApplyNoiseSuppressionInPlace(Span<float> signal)
    {
        // Simple spectral gating
        float threshold = 0.01f; // -40 dBFS
        for (int i = 0; i < signal.Length; i++)
        {
            if (Math.Abs(signal[i]) < threshold)
            {
                signal[i] *= 0.1f; // Suppress by 20dB
            }
        }
    }

    private void ApplyHighPassInPlace(Span<float> signal)
    {
        // Simple first-order high-pass at 80Hz
        float alpha = 0.995f; // For 80Hz at 48kHz
        float prev = 0;
        
        for (int i = 0; i < signal.Length; i++)
        {
            float current = signal[i];
            signal[i] = alpha * (prev + current - (i > 0 ? signal[i - 1] : 0));
            prev = current;
        }
    }

    public void Dispose()
    {
        lock (_farBuffer)
        {
            _farBuffer.Clear();
        }
    }
}
