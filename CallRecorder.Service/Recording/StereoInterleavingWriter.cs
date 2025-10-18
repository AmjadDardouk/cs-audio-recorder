using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using CallRecorder.Core.Config;
using CallRecorder.Service.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;

namespace CallRecorder.Service.Recording;

/// <summary>
/// Contract used by AudioCaptureEngine to append mic/speaker bytes into a single stereo writer.
/// </summary>
public interface IStereoWriter : IDisposable
{
    // Append raw bytes for mic and speaker using their source formats.
    void AppendMic(ReadOnlySpan<byte> buffer, WaveFormat srcFormat);
    void AppendSpeaker(ReadOnlySpan<byte> buffer, WaveFormat srcFormat);

    // Optional: flush any remaining samples (pad with silence if needed)
    void FinalizeFlush();
}

/// <summary>
/// Creates a single stereo WAV:
/// - Left  = microphone (sender)
/// - Right = speakers (receiver)
/// Converts incoming PCM/IEEE float buffers to float32 mono, resamples if needed,
/// synchronizes in 10ms frames, runs AEC, then optionally applies gains/normalization,
/// and interleaves into a stereo stream.
/// Disk IO is offloaded to a dedicated writer thread for reliability under load,
/// with automatic recovery on IO errors.
/// </summary>
internal sealed class StereoInterleavingWriter : IStereoWriter
{
    private readonly ILogger<StereoInterleavingWriter> _logger;
    private WaveFileWriter _writer;
    private readonly int _outSampleRate;
    private readonly bool _isPcm16;

    // Source formats observed (for resampling decisions)
    private readonly WaveFormat? _micObservedFormat;
    private readonly WaveFormat? _spkObservedFormat;

    // Frame-synchronized AEC accumulation
    private readonly object _aecLock = new();
    private readonly List<float> _leftAcc = new();
    private readonly List<float> _rightAcc = new();
    private int _frameSamples;

    // External AEC/voice processing (WebRTC or managed fallback)
    private readonly IAecProcessor? _aec;

    // Reverse (far-end) ring buffer target (~200ms at 10ms frames)
    private readonly int _reverseTargetFrames;
    private readonly int _reverseMaxFrames;

    // Diagnostics dumps
    private readonly bool _diagEnable;
    private readonly bool _testToneCheck;
    private WaveFileWriter? _dumpNearRaw;
    private WaveFileWriter? _dumpNearProcessed;
    private WaveFileWriter? _dumpFar;

    // Diagnostics accumulators
    private double _sumNearRaw2;
    private double _sumNearProcessed2;
    private double _sumFar2;
    private double _sumFarNearProd;
    private double _sumFarNearRawProd;

    // Reverse stream health counters
    private int _reverseDrops;
    private int _reverseUnderruns;

    // Writer thread + queue to decouple disk IO from capture callbacks
    private readonly BlockingCollection<float[]> _writeQueue = new(new ConcurrentQueue<float[]>());
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;
    private readonly object _writerInitLock = new();
    private int _recoveryIndex = 0;
    private int _framesSinceFlush = 0;

    // Small lock to guard WaveFileWriter flush/replace
    private readonly object _writeLock = new();
    private readonly List<string> _segments = new();

    public string OutputPath { get; private set; }
    public IReadOnlyList<string> SegmentPaths => _segments.AsReadOnly();

    // DSP config (gains/normalization)
    private readonly bool _normEnabled;
    private readonly float _nearStaticGain;
    private readonly float _farStaticGain;
    private readonly float _targetRmsDbfs;
    private readonly float _maxGainDb;
    private readonly float _attackCoeff;  // 0..1 per frame
    private readonly float _releaseCoeff; // 0..1 per frame
    private float _nearDynGainDb = 0f;
    private float _farDynGainDb = 0f;

    // Low-pass filter and limiter config
    private readonly bool _lowPassEnabled;
    private readonly int _lowPassHz;
    private readonly float _limiterCeilLin;

    // LPF biquad coefficients (shared)
    private float _lpfA0, _lpfA1, _lpfA2, _lpfB1, _lpfB2;
    // LPF state for near channel
    private float _lpfNPrevIn1, _lpfNPrevIn2, _lpfNPrevOut1, _lpfNPrevOut2;
    // LPF state for far channel
    private float _lpfFPrevIn1, _lpfFPrevIn2, _lpfFPrevOut1, _lpfFPrevOut2;

    // Lookahead limiters (per-channel)
    private readonly CallRecorder.Service.Audio.LookaheadLimiter? _nearLimiter;
    private readonly CallRecorder.Service.Audio.LookaheadLimiter? _farLimiter;

    // Dithering for 16-bit quantization
    private readonly bool _enableDither;
    private readonly string _ditherType = "TriangularPdf";
    private readonly float _ditherAmp; // linear full-scale amplitude
    private readonly Random _ditherRnd = new();

    // Clipping diagnostics
    private long _clipHitsNear;
    private long _clipHitsFar;

    // Telemetry
    private long _framesProcessed;

    // Startup sidetone/monitoring detector (block recording if render contains mic)
    private bool _startupCheckActive = true;
    private readonly int _startupCheckSeconds = 4; // 3–5 s window per spec
    private int _startupFramesNeeded;
    private int _startupFramesEvaluated;
    private int _startupFarActiveFrames;
    private int _startupNearSilentFrames;
    private double _startupCorrAccum;
    private bool _recordingBlocked; // refuse to write frames due to leakage/monitoring

    // Runtime LeakageGuard
    private const double LeakageThresholdDb = -25.0; // threshold for persistent leakage
    private int _leakageHighFrames;
    private int _leakageDelayBumpMs; // +15ms bump if persistent leakage detected

    public StereoInterleavingWriter(
        ILogger<StereoInterleavingWriter> logger,
        IOptions<RecordingConfig> cfg,
        string recordingId,
        WaveFormat micFormat,
        WaveFormat spkFormat,
        IAecProcessor? aecProcessor,
        AudioDspConfig? dspConfig = null,
        string? sourceLabel = null)
    {
        _logger = logger;

        // Choose output sample rate: honor configured SampleRate if set, else prefer speaker rate, fall back to mic
        _outSampleRate = cfg.Value.SampleRate > 0 ? cfg.Value.SampleRate : spkFormat.SampleRate;
        if (_outSampleRate <= 0) _outSampleRate = micFormat.SampleRate > 0 ? micFormat.SampleRate : 48000;
        // Coerce to supported high-quality rates (44.1k or 48k), prefer 48k
        if (_outSampleRate != 48000 && _outSampleRate != 44100)
        {
            _logger.LogInformation("Coercing output sample rate {rate}Hz to 48000Hz", _outSampleRate);
            _outSampleRate = 48000;
        }

        var outputDir = cfg.Value.OutputDirectory;

        // Build Calls/YYYY/MM/DD directory structure
        var now = DateTime.UtcNow;
        var callsDir = Path.Combine(outputDir, "Calls", now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));
        Directory.CreateDirectory(callsDir);

        // File name: {timestamp}_{app-or-peer}.wav
        var ts = now.ToString("yyyyMMdd_HHmmss");
        var labelPart = SanitizeFilePart(string.IsNullOrWhiteSpace(sourceLabel) ? "unknown" : sourceLabel);
        OutputPath = Path.Combine(callsDir, $"{ts}_{labelPart}.wav");

        // Honor configured bit depth where possible: 16-bit PCM or 32-bit float
        _isPcm16 = (cfg.Value.BitsPerSample == 16);
        var outFormat = _isPcm16
            ? new WaveFormat(_outSampleRate, 16, 2) // stereo PCM 16-bit
            : WaveFormat.CreateIeeeFloatWaveFormat(_outSampleRate, 2); // stereo float32

        _writer = new WaveFileWriter(OutputPath, outFormat);
        _segments.Add(OutputPath);

        // Diagnostics
        _diagEnable = dspConfig?.DiagnosticsEnableMonoDumps ?? false;
        _testToneCheck = dspConfig?.DiagnosticsTestToneCheck ?? false;

        if (_diagEnable)
        {
            var baseName = Path.GetFileNameWithoutExtension(OutputPath);
            var dir = Path.GetDirectoryName(OutputPath) ?? ".";
            var monoFmt = new WaveFormat(_outSampleRate, 16, 1);
            _dumpNearRaw = new WaveFileWriter(Path.Combine(dir, $"{baseName}_near_raw.wav"), monoFmt);
            _dumpNearProcessed = new WaveFileWriter(Path.Combine(dir, $"{baseName}_near_processed.wav"), monoFmt);
            _dumpFar = new WaveFileWriter(Path.Combine(dir, $"{baseName}_far_end.wav"), monoFmt);
        }

        _micObservedFormat = micFormat;
        _spkObservedFormat = spkFormat;

        _aec = aecProcessor;
        if (_aec != null && dspConfig != null)
        {
            _aec.Configure(dspConfig, _outSampleRate, dspConfig.FrameMs);
            // Provide initial delay estimate to AEC if supported (helps convergence)
            try { _aec.SetStreamDelayMs(dspConfig.InitialDelayMs); } catch { /* ignore */ }
        }

        // Frame size in samples for synchronized processing (default 10ms)
        int frameMs = Math.Max(1, dspConfig?.FrameMs ?? 10);
        _frameSamples = Math.Max(1, (_outSampleRate * frameMs) / 1000);
        // Reverse buffer target (~200ms), max double the target to absorb jitter
        _reverseTargetFrames = Math.Max(1, (200 + frameMs - 1) / frameMs);
        _reverseMaxFrames = _reverseTargetFrames * 2;

        // Configure startup guard frame budget
        _startupFramesNeeded = Math.Max(1, (_startupCheckSeconds * 1000) / frameMs);
        _logger.LogInformation("Echo safeguards: startup leak check for ~{sec}s (~{frames} frames), threshold |r| > 0.2 at ±10ms.",
            _startupCheckSeconds, _startupFramesNeeded);

        // Gains / normalization config
        _normEnabled = dspConfig?.Normalize ?? true;
        _nearStaticGain = DbToLin(dspConfig?.NearGainDb ?? 0f);
        _farStaticGain = DbToLin(dspConfig?.FarGainDb ?? 0f);
        _targetRmsDbfs = dspConfig?.TargetRmsDbfs ?? -20f;
        _maxGainDb = dspConfig?.MaxGainDb ?? 6f;
        // per-frame smoothing coefficients (closer to 0 = faster, closer to 1 = slower)
        var attackMs = Math.Max(1, dspConfig?.AttackMs ?? 30);
        var releaseMs = Math.Max(1, dspConfig?.ReleaseMs ?? 500);
        _attackCoeff = TimeConstToCoeff(attackMs, frameMs);
        _releaseCoeff = TimeConstToCoeff(releaseMs, frameMs);

        // Low-pass filter and limiter configuration
        _lowPassEnabled = dspConfig?.LowPass ?? true;
        _lowPassHz = Math.Max(2000, Math.Min((_outSampleRate / 2) - 100, dspConfig?.LowPassHz ?? 9000));
        _limiterCeilLin = DbToLin(dspConfig?.LimiterCeilingDbfs ?? -1.0f);

        if (_lowPassEnabled)
        {
            SetLowPass(_lowPassHz, _outSampleRate);
        }

        // Initialize lookahead limiters for both channels (post-processing stage)
        if (dspConfig != null && dspConfig.EnableLimiter)
        {
            _nearLimiter = new CallRecorder.Service.Audio.LookaheadLimiter(dspConfig, _outSampleRate, 1); // mono
            _farLimiter  = new CallRecorder.Service.Audio.LookaheadLimiter(dspConfig, _outSampleRate, 1); // mono
        }

        // Initialize dithering parameters for 16-bit PCM writes
        _enableDither = dspConfig?.EnableDithering ?? true;
        _ditherType   = dspConfig?.DitherType ?? "TriangularPdf";
        // Convert configured dBFS amount to linear full-scale amplitude
        _ditherAmp    = (float)Math.Pow(10.0, ((dspConfig?.DitherAmountDb ?? -96f) / 20.0));

        _logger.LogInformation("Stereo writer created: {path} (rate={rate}Hz, pcm16={pcm16}, aec={aec}, norm={norm})",
            OutputPath, _outSampleRate, _isPcm16, _aec != null, _normEnabled);
        _logger.LogInformation("Channel mapping: Left=mic(processed), Right=system; source={src}", labelPart);

        // Start background writer
        _writerTask = Task.Run(() => WriterLoop(_cts.Token));
    }

    public void AppendMic(ReadOnlySpan<byte> buffer, WaveFormat srcFormat)
    {
        var mono = ToFloatMono(buffer, srcFormat);
        if (srcFormat.SampleRate != _outSampleRate && mono.Length > 0)
            mono = ResampleLinear(mono, srcFormat.SampleRate, _outSampleRate);

        // Defer AEC to synchronized frame processing
        lock (_aecLock)
        {
            for (int i = 0; i < mono.Length; i++)
                _leftAcc.Add(mono[i]);

            TryProcessFramesLocked();
        }
    }

    public void AppendSpeaker(ReadOnlySpan<byte> buffer, WaveFormat srcFormat)
    {
        var mono = ToFloatMono(buffer, srcFormat);
        if (srcFormat.SampleRate != _outSampleRate && mono.Length > 0)
            mono = ResampleLinear(mono, srcFormat.SampleRate, _outSampleRate);

        // Defer far-end feeding to synchronized frame processing
        lock (_aecLock)
        {
            for (int i = 0; i < mono.Length; i++)
                _rightAcc.Add(mono[i]);

            TryProcessFramesLocked();
        }
    }

    private void TryProcessFramesLocked()
    {
        // Process frames even if only one side has data; pad the other with zeros.
        // This prevents delaying near-end writes when far-end is temporarily silent.
        while (true)
        {
            bool haveL = _leftAcc.Count >= _frameSamples;
            bool haveR = _rightAcc.Count >= _frameSamples;
            if (!haveL && !haveR) break;

            // Pace output: avoid unbounded near-only frames when far is missing
            int leftFramesBuffered = _leftAcc.Count / _frameSamples;
            int rightFramesBuffered = _rightAcc.Count / _frameSamples;
            int leadFrames = leftFramesBuffered - rightFramesBuffered;
            const int maxLeadFrames = 2; // allow up to ~20-30ms lead before waiting for far
            if (haveL && !haveR && leadFrames > maxLeadFrames)
            {
                // Wait for far-end to catch up instead of padding indefinitely,
                // which would inflate total duration and create "slow" playback perception.
                break;
            }

            // Manage reverse buffer occupancy (drop oldest if over target to keep alignment tight)
            int reverseFramesBuffered = _rightAcc.Count / _frameSamples;
            while (reverseFramesBuffered > _reverseMaxFrames)
            {
                _rightAcc.RemoveRange(0, _frameSamples);
                reverseFramesBuffered--;
                _reverseDrops++;
            }

            // Estimate stream delay from reverse buffer fill and pass to AEC (if supported)
            int frameMsEstimate = Math.Max(1, (int)Math.Round(1000.0 * _frameSamples / _outSampleRate));
            int delayMs = (reverseFramesBuffered - _reverseTargetFrames) * frameMsEstimate + _leakageDelayBumpMs;
            try { _aec?.SetStreamDelayMs(delayMs); } catch { /* ignore */ }

            // Periodic telemetry for reverse alignment
            if ((_framesProcessed % 100) == 0)
            {
                _logger.LogDebug("Reverse buffer fill={fill} frames (~{ms} ms), est streamDelay={delay} ms",
                    reverseFramesBuffered, reverseFramesBuffered * frameMsEstimate, delayMs);

                // Periodic diagnostics (approx 1s): ERLE, leakage proxy, cross-corr near 0 lag
                if (_diagEnable)
                {
                    const double eps = 1e-9;
                    double erle = (_sumNearProcessed2 > 0.0)
                        ? 10.0 * Math.Log10((_sumNearRaw2 + eps) / (_sumNearProcessed2 + eps))
                        : 0.0;

                    double leakCorr = 0.0;
                    if (_sumFar2 > 0.0 && _sumNearProcessed2 > 0.0)
                    {
                        leakCorr = _sumFarNearProd / Math.Sqrt(_sumFar2 * _sumNearProcessed2);
                        if (leakCorr > 1.0) leakCorr = 1.0;
                        if (leakCorr < -1.0) leakCorr = -1.0;
                    }

                    _logger.LogInformation("AEC metrics: ERLE={erle:F1} dB, corr(far,nearProc)={corr:F3}, delay={delay} ms, reverseFill={fill}",
                        erle, leakCorr, delayMs, reverseFramesBuffered);
                }
            }

            float[] nearFrame;
            if (haveL)
            {
                nearFrame = _leftAcc.GetRange(0, _frameSamples).ToArray();
                _leftAcc.RemoveRange(0, _frameSamples);
            }
            else
            {
                nearFrame = new float[_frameSamples]; // zeros
            }

            float[] farFrame;
            if (haveR)
            {
                farFrame = _rightAcc.GetRange(0, _frameSamples).ToArray();
                _rightAcc.RemoveRange(0, _frameSamples);
            }
            else
            {
                farFrame = new float[_frameSamples]; // zeros
                _reverseUnderruns++;
            }

            // Startup sidetone/monitoring detector and runtime leakage guard
            EvaluateStartupLeakage(nearFrame, farFrame, frameMsEstimate);
            if (_recordingBlocked)
            {
                if ((_framesProcessed % 50) == 0)
                {
                    _logger.LogError("Mic monitoring/sidetone detected on render path. Disable 'Listen to this device' or use headphones. Recording is blocked.");
                }
                continue; // refuse to write while blocked
            }

            // AEC: feed far, then process near for the same frame window
            float[] processedNear = nearFrame;
            if (_aec != null)
            {
                _aec.FeedFar(farFrame);
                var cleaned = new float[nearFrame.Length];
                _aec.ProcessNear(nearFrame, cleaned);
                processedNear = cleaned;
            }

            // Apply per-channel static gain + normalization (post-processing)
            ApplyPostProcessing(processedNear, farFrame);

            // Diagnostics: dumps and metrics
            if (_diagEnable)
            {
                if (_dumpNearRaw != null)
                    WriteMono16(_dumpNearRaw, nearFrame);
                if (_dumpNearProcessed != null)
                    WriteMono16(_dumpNearProcessed, processedNear);
                if (_dumpFar != null)
                    WriteMono16(_dumpFar, farFrame);

                for (int i = 0; i < nearFrame.Length; i++)
                {
                    double nr = nearFrame[i];
                    double np = processedNear[i];
                    double fr = farFrame[i];
                    _sumNearRaw2 += nr * nr;
                    _sumNearProcessed2 += np * np;
                    _sumFar2 += fr * fr;
                    _sumFarNearProd += fr * np;
                    _sumFarNearRawProd += fr * nr;
                }
            }

            // Interleave this synchronized frame and enqueue for background write
            var interleaved = new float[_frameSamples * 2];
            for (int i = 0; i < _frameSamples; i++)
            {
                int idx = i * 2;
                interleaved[idx]     = processedNear[i]; // left: near after AEC + gains/norm
                interleaved[idx + 1] = farFrame[i];      // right: far + gains/norm
            }

            // Enqueue frame for async disk write
            _writeQueue.Add(interleaved);
            _framesProcessed++;
        }
    }

    private void ApplyPostProcessing(float[] nearFrame, float[] farFrame)
    {
        // Apply static gain first
        if (Math.Abs(_nearStaticGain - 1f) > 1e-6)
        {
            for (int i = 0; i < nearFrame.Length; i++) nearFrame[i] *= _nearStaticGain;
        }
        if (Math.Abs(_farStaticGain - 1f) > 1e-6)
        {
            for (int i = 0; i < farFrame.Length; i++) farFrame[i] *= _farStaticGain;
        }

        if (!_normEnabled) return;

        // Compute RMS per channel
        float nearRms = Rms(nearFrame);
        float farRms  = Rms(farFrame);

        float nearDb = LinToDb(nearRms);
        float farDb  = LinToDb(farRms);

        // Determine desired additional gain to hit target RMS (limit to MaxGainDb)
        float nearNeeded = Clamp(_targetRmsDbfs - nearDb, 0f, _maxGainDb);
        float farNeeded  = Clamp(_targetRmsDbfs - farDb,  0f, _maxGainDb);

        // Smooth towards desired gain with attack (increase fast) and release (decrease slow)
        _nearDynGainDb = SmoothDb(_nearDynGainDb, nearNeeded);
        _farDynGainDb  = SmoothDb(_farDynGainDb,  farNeeded);

        float nearDynLin = DbToLin(_nearDynGainDb);
        float farDynLin  = DbToLin(_farDynGainDb);

        for (int i = 0; i < nearFrame.Length; i++) nearFrame[i] *= nearDynLin;
        for (int i = 0; i < farFrame.Length; i++) farFrame[i] *= farDynLin;

        // Optional low-pass filtering to tame hiss on both channels
        if (_lowPassEnabled)
        {
            ApplyBiquadInPlace(nearFrame, ref _lpfNPrevIn1, ref _lpfNPrevIn2, ref _lpfNPrevOut1, ref _lpfNPrevOut2);
            ApplyBiquadInPlace(farFrame,  ref _lpfFPrevIn1, ref _lpfFPrevIn2, ref _lpfFPrevOut1, ref _lpfFPrevOut2);
        }

        // Lookahead limiter per-channel to avoid clipping with headroom (ceiling configured)
        _nearLimiter?.Process(nearFrame.AsSpan());
        _farLimiter?.Process(farFrame.AsSpan());

        // Safety hard ceiling (should rarely engage if limiter is effective)
        LimitInPlace(nearFrame, _limiterCeilLin);
        LimitInPlace(farFrame,  _limiterCeilLin);

        // Count near-ceiling hits for diagnostics (should be zero)
        float hitThresh = _limiterCeilLin * 0.999f;
        for (int i = 0; i < nearFrame.Length; i++)
        {
            if (MathF.Abs(nearFrame[i]) >= hitThresh) _clipHitsNear++;
        }
        for (int i = 0; i < farFrame.Length; i++)
        {
            if (MathF.Abs(farFrame[i]) >= hitThresh) _clipHitsFar++;
        }
    }

    public void FinalizeFlush()
    {
        // Pad tail to a full frame for both sides, then process remaining
        lock (_aecLock)
        {
            if (_frameSamples > 0)
            {
                int remL = _leftAcc.Count % _frameSamples;
                int remR = _rightAcc.Count % _frameSamples;
                int padL = remL == 0 ? 0 : _frameSamples - remL;
                int padR = remR == 0 ? 0 : _frameSamples - remR;

                for (int i = 0; i < padL; i++) _leftAcc.Add(0f);
                for (int i = 0; i < padR; i++) _rightAcc.Add(0f);

                TryProcessFramesLocked();
            }
        }

        // Wait briefly for queue to drain and flush writer
        WaitForQueueDrainAndFlush();

        // Diagnostics: log metrics and close dumps
        LogDiagnostics();
        try { _dumpNearRaw?.Dispose(); } catch { /* ignore */ }
        try { _dumpNearProcessed?.Dispose(); } catch { /* ignore */ }
        try { _dumpFar?.Dispose(); } catch { /* ignore */ }
    }

    private void WaitForQueueDrainAndFlush()
    {
        // Allow a brief window to drain queued frames
        var sw = Stopwatch.StartNew();
        while (_writeQueue.Count > 0 && sw.ElapsedMilliseconds < 5000)
        {
            Thread.Sleep(5);
        }
        lock (_writeLock)
        {
            try { _writer.Flush(); } catch { /* ignore */ }
        }
    }

    private void WriterLoop(CancellationToken token)
    {
        try
        {
            foreach (var frame in _writeQueue.GetConsumingEnumerable(token))
            {
                try
                {
                    WriteFrame(frame);
                    _framesSinceFlush++;

                    // Periodic flush to reduce loss window (about 10 frames ~100ms at 10ms frames)
                    if (_framesSinceFlush >= 10)
                    {
                        lock (_writeLock)
                        {
                            _writer.Flush();
                        }
                        _framesSinceFlush = 0;
                    }
                }
                catch (IOException ioex)
                {
                    _logger.LogWarning(ioex, "IO error writing frame. Attempting recovery...");
                    RecoverWriter();
                }
                catch (UnauthorizedAccessException uaex)
                {
                    _logger.LogWarning(uaex, "Access error writing frame. Attempting recovery...");
                    RecoverWriter();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unexpected error writing frame, attempting recovery...");
                    RecoverWriter();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Writer loop exited with unexpected exception");
        }
    }

    private void RecoverWriter()
    {
        lock (_writerInitLock)
        {
            try
            {
                lock (_writeLock)
                {
                    try { _writer.Flush(); } catch { /* ignore */ }
                    try { _writer.Dispose(); } catch { /* ignore */ }
                }

                _recoveryIndex++;
                var dir = Path.GetDirectoryName(OutputPath) ?? ".";
                var name = Path.GetFileNameWithoutExtension(OutputPath) ?? "recording";
                var ext = Path.GetExtension(OutputPath);
                var recoveryPath = Path.Combine(dir, $"{name}-recovery{_recoveryIndex}{ext}");

                // Recreate writer with same format
                WaveFormat fmt = _isPcm16
                    ? new WaveFormat(_outSampleRate, 16, 2)
                    : WaveFormat.CreateIeeeFloatWaveFormat(_outSampleRate, 2);

                lock (_writeLock)
                {
                    _writer = new WaveFileWriter(recoveryPath, fmt);
                    OutputPath = recoveryPath;
                    _segments.Add(recoveryPath);
                }

                _logger.LogInformation("Recovered writer to new file: {path}", recoveryPath);
            }
            catch (Exception rex)
            {
                _logger.LogError(rex, "Failed to recover writer; frames may be dropped until next recovery");
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteFrame(float[] interleaved)
    {
        lock (_writeLock)
        {
            if (_isPcm16)
            {
                // Convert float [-1,1] to PCM 16-bit and write
                var bytes = new byte[interleaved.Length * 2];
                int bi = 0;
                for (int i = 0; i < interleaved.Length; i++)
                {
                    float f = interleaved[i];

                    // Optional TPDF/rectangular dithering before quantization to 16-bit
                    if (_enableDither)
                    {
                        float d = 0f;
                        if (_ditherType == "TriangularPdf")
                        {
                            // TPDF: difference of two uniform random variables in [0,1)
                            d = ((float)_ditherRnd.NextDouble() - (float)_ditherRnd.NextDouble()) * _ditherAmp;
                        }
                        else
                        {
                            // RectangularPdf or fallback: single uniform in [-0.5,0.5] scaled
                            d = ((float)_ditherRnd.NextDouble() - 0.5f) * 2f * _ditherAmp;
                        }
                        f += d;
                    }

                    // Clip to valid range after dither
                    if (f > 1f) f = 1f;
                    else if (f < -1f) f = -1f;

                    short s = (short)Math.Round(f * short.MaxValue);
                    bytes[bi++] = (byte)(s & 0xFF);
                    bytes[bi++] = (byte)((s >> 8) & 0xFF);
                }
                _writer.Write(bytes, 0, bytes.Length);
            }
            else
            {
                // 32-bit float direct
                _writer.WriteSamples(interleaved, 0, interleaved.Length);
            }
        }
    }

    private static float[] ToFloatMono(ReadOnlySpan<byte> buffer, WaveFormat srcFormat)
    {
        // Handle common encodings: IEEE float 32-bit or PCM 16-bit
        var enc = srcFormat.Encoding;

        if (enc == WaveFormatEncoding.IeeeFloat && srcFormat.BitsPerSample == 32)
        {
            // Convert channels to mono (downmix if needed)
            int sampleCount = buffer.Length / 4; // 4 bytes per float
            var floatBuffer = MemoryMarshal.Cast<byte, float>(buffer).ToArray();

            if (srcFormat.Channels == 1)
                return floatBuffer;

            // Downmix stereo/multi-channel to mono by averaging channels
            int frames = sampleCount / srcFormat.Channels;
            var mono = new float[frames];
            int idx = 0;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                for (int c = 0; c < srcFormat.Channels; c++)
                {
                    sum += floatBuffer[idx++];
                }
                mono[f] = sum / srcFormat.Channels;
            }
            return mono;
        }
        else if (enc == WaveFormatEncoding.Pcm && srcFormat.BitsPerSample == 16)
        {
            int bytesPerSample = 2;
            int sampleCount = buffer.Length / bytesPerSample;
            var mono = new float[sampleCount / srcFormat.Channels];

            int idx = 0;
            for (int i = 0; i < mono.Length; i++)
            {
                float sum = 0f;
                for (int c = 0; c < srcFormat.Channels; c++)
                {
                    short s = BitConverter.ToInt16(buffer.Slice(idx, 2));
                    idx += 2;
                    sum += s / 32768f;
                }
                mono[i] = sum / srcFormat.Channels;
            }
            return mono;
        }
        else
        {
            // Fallback: try to interpret as 32-bit float per sample
            int sampleCount = buffer.Length / 4;
            if (sampleCount <= 0) return Array.Empty<float>();
            var asFloat = new float[sampleCount];
            Buffer.BlockCopy(buffer.ToArray(), 0, asFloat, 0, buffer.Length);

            if (srcFormat.Channels <= 1) return asFloat;

            int frames = sampleCount / srcFormat.Channels;
            var mono = new float[frames];
            int idx = 0;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                for (int c = 0; c < srcFormat.Channels; c++)
                    sum += asFloat[idx++];
                mono[f] = sum / srcFormat.Channels;
            }
            return mono;
        }
    }

    private static float[] ResampleLinear(float[] src, int srcRate, int dstRate)
    {
        if (srcRate <= 0 || dstRate <= 0 || src.Length == 0 || srcRate == dstRate) return src;

        double ratio = (double)dstRate / srcRate;
        int dstLen = (int)Math.Round(src.Length * ratio);
        if (dstLen <= 1) return src;

        var dst = new float[dstLen];

        for (int i = 0; i < dstLen; i++)
        {
            double srcIdx = i / ratio;
            int i0 = (int)Math.Floor(srcIdx);
            int i1 = Math.Min(i0 + 1, src.Length - 1);
            double frac = srcIdx - i0;

            float s0 = src[i0];
            float s1 = src[i1];
            dst[i] = (float)(s0 + (s1 - s0) * frac);
        }
        return dst;
    }

    public void Dispose()
    {
        try
        {
            FinalizeFlush();
        }
        catch { /* ignore */ }

        try
        {
            // Stop writer thread
            _cts.Cancel();
            try { _writerTask.Wait(1000); } catch { /* ignore */ }
        }
        catch { /* ignore */ }

        try
        {
            lock (_writeLock)
            {
                _writer?.Dispose();
            }
            _logger.LogInformation("Stereo writer disposed: {path}", OutputPath);
        }
        catch { /* ignore */ }

        try
        {
            _writeQueue.Dispose();
        }
        catch { /* ignore */ }

        try
        {
            _cts.Dispose();
        }
        catch { /* ignore */ }
    }

    // Helpers for gains/normalization
    private static float Rms(ReadOnlySpan<float> x)
    {
        double sum = 0.0;
        for (int i = 0; i < x.Length; i++) sum += x[i] * x[i];
        return (float)Math.Sqrt(sum / Math.Max(1, x.Length));
    }

    private static float DbToLin(float db) => (float)Math.Pow(10.0, db / 20.0);
    private static float LinToDb(float lin)
    {
        const float eps = 1e-6f;
        return 20f * (float)Math.Log10(Math.Max(eps, lin));
    }
    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    private float SmoothDb(float currentDb, float targetDb)
    {
        // If we need more gain (increase), use attack (faster). If less, use release (slower).
        bool increasing = targetDb > currentDb;
        float coeff = increasing ? _attackCoeff : _releaseCoeff;
        // One-pole smoothing: y[n] = coeff*y[n-1] + (1-coeff)*x[n]
        return coeff * currentDb + (1f - coeff) * targetDb;
    }
    private static float TimeConstToCoeff(int timeConstMs, int frameMs)
    {
        // Approximate smoothing coefficient per frame
        // e^(-frame/timeConst) bounds in (0..1)
        return (float)Math.Exp(-(double)frameMs / Math.Max(1.0, timeConstMs));
    }
    private static void SoftClipInPlace(Span<float> x)
    {
        // Simple soft clipper: y = tanh(k*x)/tanh(k) with k ~ 1.5
        const float k = 1.5f;
        const float norm = 1f / 0.9051482536f; // 1/tanh(1.5)
        for (int i = 0; i < x.Length; i++)
        {
            float y = (float)Math.Tanh(k * x[i]) * norm;
            x[i] = y;
        }
    }

    private void SetLowPass(int cutoffHz, int fs)
    {
        // 2nd-order Butterworth low-pass biquad
        float w = 2.0f * (float)Math.PI * cutoffHz / fs;
        float cosw = (float)Math.Cos(w);
        float sinw = (float)Math.Sin(w);
        float alpha = sinw / (float)Math.Sqrt(2.0);

        float b0 = (1f - cosw) * 0.5f;
        float b1 = (1f - cosw);
        float b2 = (1f - cosw) * 0.5f;
        float a0 = 1f + alpha;
        float a1 = -2f * cosw;
        float a2 = 1f - alpha;

        _lpfA0 = b0 / a0;
        _lpfA1 = b1 / a0;
        _lpfA2 = b2 / a0;
        _lpfB1 = a1 / a0;
        _lpfB2 = a2 / a0;

        // reset states
        _lpfNPrevIn1 = _lpfNPrevIn2 = _lpfNPrevOut1 = _lpfNPrevOut2 = 0f;
        _lpfFPrevIn1 = _lpfFPrevIn2 = _lpfFPrevOut1 = _lpfFPrevOut2 = 0f;
    }

    private void ApplyBiquadInPlace(Span<float> x, ref float prevIn1, ref float prevIn2, ref float prevOut1, ref float prevOut2)
    {
        for (int i = 0; i < x.Length; i++)
        {
            float in0 = x[i];
            float y = _lpfA0 * in0 + _lpfA1 * prevIn1 + _lpfA2 * prevIn2
                      - _lpfB1 * prevOut1 - _lpfB2 * prevOut2;
            prevIn2 = prevIn1;
            prevIn1 = in0;
            prevOut2 = prevOut1;
            prevOut1 = y;
            x[i] = y;
        }
    }

    private static void LimitInPlace(Span<float> x, float ceiling)
    {
        float c = MathF.Abs(ceiling);
        if (c <= 0f || c > 1.0f) c = 1.0f;
        for (int i = 0; i < x.Length; i++)
        {
            float a = x[i];
            float aa = MathF.Abs(a);
            if (aa > c)
            {
                x[i] = (a >= 0f ? c : -c);
            }
        }
    }

    private static void WriteMono16(WaveFileWriter writer, float[] mono)
    {
        var bytes = new byte[mono.Length * 2];
        int bi = 0;
        for (int i = 0; i < mono.Length; i++)
        {
            float f = mono[i];
            if (f > 1f) f = 1f;
            else if (f < -1f) f = -1f;
            short s = (short)Math.Round(f * short.MaxValue);
            bytes[bi++] = (byte)(s & 0xFF);
            bytes[bi++] = (byte)((s >> 8) & 0xFF);
        }
        writer.Write(bytes, 0, bytes.Length);
    }

    private void LogDiagnostics()
    {
        if (!_diagEnable) return;

        double erle = (_sumNearProcessed2 > 0.0)
            ? 10.0 * Math.Log10((_sumNearRaw2 + 1e-9) / (_sumNearProcessed2 + 1e-9))
            : 0.0;

        // Correlation-based leakage proxy (not perfect but indicative)
        double leakageDb = -999.0;
        if (_sumFar2 > 0.0 && _sumNearProcessed2 > 0.0)
        {
            double corr = _sumFarNearProd / Math.Sqrt(_sumFar2 * _sumNearProcessed2);
            corr = Math.Min(1.0, Math.Max(-1.0, corr));
            double leakLin = Math.Abs(corr);
            leakageDb = (leakLin <= 1e-6) ? -120.0 : 20.0 * Math.Log10(leakLin);
        }

        _logger.LogInformation("Diagnostics: ERLE={erle:F1} dB, leakage(corr-proxy)={leakageDb:F1} dB (target ≤ -35 dB)", erle, leakageDb);
        _logger.LogInformation("Reverse stream: drops={drops}, underruns={underruns}", _reverseDrops, _reverseUnderruns);

        // Pre-AEC leakage proxy: if high, far-end likely contains mic (sidetone) or device mix
        double leakageRawDb = -999.0;
        if (_sumFar2 > 0.0 && _sumNearRaw2 > 0.0)
        {
            double corrRaw = _sumFarNearRawProd / Math.Sqrt(_sumFar2 * _sumNearRaw2);
            corrRaw = Math.Min(1.0, Math.Max(-1.0, corrRaw));
            double leakRawLin = Math.Abs(corrRaw);
            leakageRawDb = (leakRawLin <= 1e-6) ? -120.0 : 20.0 * Math.Log10(leakRawLin);
        }
        _logger.LogInformation("Pre-AEC leakage(corr-proxy)={leakageRawDb:F1} dB (indicative of sidetone/mix)", leakageRawDb);

        if (leakageRawDb > -15.0) // strong correlation
        {
            _logger.LogWarning("Detected strong pre-AEC correlation between far-end and near-raw (sidetone/device mix likely). "
                + "Advise disabling 'Listen to this device' on the mic, prefer per-app session loopback, or use Communications render endpoint.");
        }

        // Clipping diagnostics summary (should be zero when limiter + headroom configured correctly)
        double ceilDb = 20.0 * Math.Log10(Math.Max(1e-9, _limiterCeilLin));
        _logger.LogInformation("Clipping check: near hits={near}, far hits={far} (target 0) at ceiling {ceilDb:F1} dBFS",
            _clipHitsNear, _clipHitsFar, ceilDb);
        if (_clipHitsNear > 0 || _clipHitsFar > 0)
        {
            _logger.LogWarning("Limiter saturation detected (near={near}, far={far}). Consider reducing recording gains by -3 dB steps or lowering limiter ceiling.",
                _clipHitsNear, _clipHitsFar);
        }

        if (_testToneCheck)
        {
            const double eps = 1e-9;
            double farVsNearDb = 10.0 * Math.Log10(((_sumFar2 + eps) / (_sumNearProcessed2 + eps)));
            _logger.LogInformation("ToneCheck proxy: Far-vs-Near level = {farVsNearDb:F1} dB (target ≥ 35 dB)", farVsNearDb);

            bool pass = (erle >= 20.0) && (leakageDb <= -35.0) && (farVsNearDb >= 35.0);
            if (pass)
            {
                _logger.LogInformation("TestToneCheck: PASS (ERLE≥20 dB, leakage≤-35 dB, Far/Near≥35 dB)");
            }
            else
            {
                _logger.LogWarning("TestToneCheck: FAIL (ERLE={erle:F1} dB, leakage={leakageDb:F1} dB, Far/Near={farVsNearDb:F1} dB). Check per-app session loopback, communications endpoint, sidetone/monitoring, and delay alignment.",
                    erle, leakageDb, farVsNearDb);
            }
        }
    }

    // Leakage detection helpers
    private void EvaluateStartupLeakage(float[] nearFrame, float[] farFrame, int frameMsEstimate)
    {
        // During startup, compute correlation when near is silent and far is active
        if (_startupCheckActive)
        {
            float nearRms = Rms(nearFrame);
            float farRms = Rms(farFrame);
            bool nearSilent = LinToDb(nearRms) < -45f; // VAD-silent proxy
            bool farActive = LinToDb(farRms) > -35f;

            if (nearSilent) _startupNearSilentFrames++;
            if (farActive) _startupFarActiveFrames++;

            if (nearSilent && farActive)
            {
                double corr0 = CorrelationAtZeroLag(farFrame, nearFrame);
                _startupCorrAccum += Math.Abs(corr0);
            }

            _startupFramesEvaluated++;
            if (_startupFramesEvaluated >= _startupFramesNeeded)
            {
                double avgCorr = (_startupFarActiveFrames > 0)
                    ? _startupCorrAccum / Math.Max(1, _startupFarActiveFrames)
                    : 0.0;

                if (avgCorr > 0.2)
                {
                    _recordingBlocked = true;
                    _logger.LogError("Mic monitoring/sidetone detected on render path (avg |r|={corr:F2} near 0 lag over {sec}s). Disable 'Listen to this device' in Recording device properties or switch to headphones.",
                        avgCorr, _startupCheckSeconds);
                }
                _startupCheckActive = false;
            }
        }

        // Runtime LeakageGuard: when near is silent and far is active, track leakage
        if (!_startupCheckActive)
        {
            float nearRms = Rms(nearFrame);
            float farRms = Rms(farFrame);
            bool nearSilent = LinToDb(nearRms) < -45f;
            bool farActive = LinToDb(farRms) > -35f;

            if (nearSilent && farActive)
            {
                double corr0 = CorrelationAtZeroLag(farFrame, nearFrame);
                double leakDb = 20.0 * Math.Log10(Math.Max(1e-9, Math.Abs(corr0)));
                if (leakDb > LeakageThresholdDb)
                {
                    _leakageHighFrames++;
                }
            }

            // Approximately every second (100 frames at 10 ms)
            if ((_framesProcessed % 100) == 0 && _framesProcessed > 0)
            {
                if (_leakageHighFrames > 70) // persistent high leakage
                {
                    _leakageDelayBumpMs += 15;
                    _logger.LogWarning("LeakageGuard: persistent leakage detected (> {thr} dB). Increasing AEC delay estimate by +15 ms to {bump} ms.",
                        LeakageThresholdDb, _leakageDelayBumpMs);

                    // If still failing after multiple bumps, stop recording
                    if (_leakageDelayBumpMs >= 45)
                    {
                        _logger.LogError("LeakageGuard: auto-stop due to persistent leakage. Use headphones or disable mic monitoring.");
                        _recordingBlocked = true;
                    }
                }
                _leakageHighFrames = 0;
            }
        }
    }

    private static double CorrelationAtZeroLag(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        int n = Math.Min(x.Length, y.Length);
        if (n <= 1) return 0.0;

        double sumx = 0, sumy = 0, sumxx = 0, sumyy = 0, sumxy = 0;
        for (int i = 0; i < n; i++)
        {
            double xi = x[i];
            double yi = y[i];
            sumx += xi;
            sumy += yi;
            sumxx += xi * xi;
            sumyy += yi * yi;
            sumxy += xi * yi;
        }
        double denom = Math.Sqrt((sumxx - (sumx * sumx) / n) * (sumyy - (sumy * sumy) / n)) + 1e-12;
        if (denom <= 1e-12) return 0.0;
        double r = (sumxy - (sumx * sumy) / n) / denom;
        if (r > 1.0) r = 1.0;
        if (r < -1.0) r = -1.0;
        return r;
    }

    private static string SanitizeFilePart(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "unknown";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Where(ch => !invalid.Contains(ch)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) return "unknown";
        // limit length
        if (cleaned.Length > 40) cleaned = cleaned.Substring(0, 40);
        return cleaned.ToLowerInvariant();
    }
}
