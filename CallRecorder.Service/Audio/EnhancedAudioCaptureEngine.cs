using System.Collections.Concurrent;
using System.Linq;
using CallRecorder.Core.Config;
using CallRecorder.Service.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CallRecorder.Service.Audio;

/// <summary>
/// Enhanced audio capture engine with real-time echo cancellation and proper channel separation
/// Eliminates acoustic echo and provides clean separated audio channels
/// </summary>
public class EnhancedAudioCaptureEngine : IAudioCaptureEngine
{
    private readonly ILogger<EnhancedAudioCaptureEngine> _logger;
    private readonly RecordingConfig _cfg;
    private readonly AudioDeviceConfig _devCfg;
    private readonly AudioDspConfig _dspCfg;

    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _speakerCapture;
    private WaveFormat? _micFormat;
    private WaveFormat? _speakerFormat;

    private readonly object _stateLock = new();
    private bool _running;

    // Enhanced buffering with timestamp tracking for synchronization
    private readonly ConcurrentQueue<TimestampedAudioChunk> _micBuffer = new();
    private readonly ConcurrentQueue<TimestampedAudioChunk> _speakerBuffer = new();
    private long _micBufferedBytes;
    private long _speakerBufferedBytes;
    private long _micMaxPrebufferBytes = 0;
    private long _speakerMaxPrebufferBytes = 0;

    // Real-time audio processing
    private AdvancedAudioProcessor? _micProcessor;
    private AdvancedAudioProcessor? _speakerProcessor;
    private IAecProcessor? _aecProcessor;
    private readonly IAecProcessorFactory _aecFactory;

    // Activity metrics with enhanced voice detection
    public DateTime LastMicActivityUtc { get; private set; } = DateTime.MinValue;
    public DateTime LastSpeakerActivityUtc { get; private set; } = DateTime.MinValue;
    public long MicBytesSinceStart { get; private set; }
    public long SpeakerBytesSinceStart { get; private set; }
    public DateTime LastMicVoiceActivityUtc { get; private set; } = DateTime.MinValue;
    public DateTime LastSpeakerVoiceActivityUtc { get; private set; } = DateTime.MinValue;
    
    private VoiceActivityDetector _micVad = new(48000);
    private VoiceActivityDetector _speakerVad = new(48000);

    // Synchronization and timing
    private DateTime _captureStartTime = DateTime.MinValue;
    private readonly object _syncLock = new();

    // Audio quality monitoring
    private float _micSignalLevel = 0f;
    private float _speakerSignalLevel = 0f;
    private float _echoSuppressionLevel = 0f;

    public WaveFormat MicFormat => _micFormat ?? throw new InvalidOperationException("Mic not initialized");
    public WaveFormat SpeakerFormat => _speakerFormat ?? throw new InvalidOperationException("Speaker not initialized");

    // Recording writers
    private WaveFileWriter? _activeMicWriter;
    private WaveFileWriter? _activeSpeakerWriter;
    private IStereoWriter? _stereoWriter;

    public EnhancedAudioCaptureEngine(
        ILogger<EnhancedAudioCaptureEngine> logger, 
        IOptions<RecordingConfig> cfg, 
        IOptions<AudioDeviceConfig> devCfg,
        IOptions<AudioDspConfig> dspCfg,
        IAecProcessorFactory aecFactory)
    {
        _logger = logger;
        _cfg = cfg.Value;
        _devCfg = devCfg.Value;
        _dspCfg = dspCfg.Value;
        _aecFactory = aecFactory;
    }

    public async Task StartAsync(CancellationToken token)
    {
        lock (_stateLock)
        {
            if (_running) return;
            _running = true;
        }

        try
        {
            // Create RAW endpoint manager for clean audio capture
            var rawManagerLogger = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<WasapiRawEndpointManager>();
            var rawManager = new WasapiRawEndpointManager(rawManagerLogger);
            
            // Select optimal endpoints with validation
            var (micDevice, speakerDevice) = rawManager.SelectOptimalEndpoints();
            
            _logger.LogInformation("Selected mic: {name} (Communications endpoint)", micDevice.FriendlyName);
            _logger.LogInformation("Selected speaker: {name} (Communications loopback)", speakerDevice.FriendlyName);
            _logger.LogInformation("Mic endpoint ID: {id}", micDevice.ID);
            _logger.LogInformation("Speaker endpoint ID: {id}", speakerDevice.ID);
            
            // Initialize capture devices with RAW mode
            _micCapture = new WasapiCapture(micDevice);
            _speakerCapture = new WasapiLoopbackCapture(speakerDevice);
            
            // Configure RAW mode to bypass OS effects
            rawManager.ConfigureRawMode(_micCapture, micDevice);
            rawManager.ConfigureRawMode(_speakerCapture, speakerDevice);
            
            // Force shared mode with event-driven for 10ms timing
            _micCapture.ShareMode = AudioClientShareMode.Shared;
            _speakerCapture.ShareMode = AudioClientShareMode.Shared;

            // Cache formats and validate
            _micFormat = _micCapture.WaveFormat;
            _speakerFormat = _speakerCapture.WaveFormat;

            ValidateAudioFormats();

            // Setup enhanced buffering
            _micMaxPrebufferBytes = (long)(_micFormat.AverageBytesPerSecond * _cfg.PreBufferSeconds);
            _speakerMaxPrebufferBytes = (long)(_speakerFormat.AverageBytesPerSecond * _cfg.PreBufferSeconds);

            // Initialize real-time audio processors
            InitializeAudioProcessors();

            // Setup event handlers
            _micCapture.DataAvailable += OnMicDataAvailable;
            _speakerCapture.DataAvailable += OnSpeakerDataAvailable;
            _micCapture.RecordingStopped += (s, e) => HandleCaptureError("Microphone", e.Exception);
            _speakerCapture.RecordingStopped += (s, e) => HandleCaptureError("Speaker", e.Exception);

            _logger.LogInformation("Starting enhanced audio capture with echo cancellation");
            _logger.LogInformation("Mic format: {micRate}Hz {micBits}-bit {micCh}ch", 
                _micFormat.SampleRate, _micFormat.BitsPerSample, _micFormat.Channels);
            _logger.LogInformation("Speaker format: {spkRate}Hz {spkBits}-bit {spkCh}ch", 
                _speakerFormat.SampleRate, _speakerFormat.BitsPerSample, _speakerFormat.Channels);

            // Start capture synchronously
            _captureStartTime = DateTime.UtcNow;
            _micCapture.StartRecording();
            _speakerCapture.StartRecording();

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start enhanced audio capture");
            Stop();
            throw;
        }
    }

    private void ValidateAudioFormats()
    {
        // Validate mic format
        if (_micFormat!.SampleRate < _devCfg.MinSampleRateHz)
        {
            _logger.LogWarning("Microphone sample rate {rate}Hz is below minimum {min}Hz", 
                _micFormat.SampleRate, _devCfg.MinSampleRateHz);
        }

        if (_micFormat.BitsPerSample < _devCfg.MinBitsPerSample)
        {
            _logger.LogWarning("Microphone bit depth {bits} is below minimum {min}", 
                _micFormat.BitsPerSample, _devCfg.MinBitsPerSample);
        }

        // Check for optimal formats
        if (_micFormat.SampleRate != 48000)
        {
            _logger.LogInformation("Note: Microphone using {rate}Hz instead of professional 48kHz", 
                _micFormat.SampleRate);
        }
    }

    private void InitializeAudioProcessors()
    {
        // Create specific loggers for audio processors
        var processorLoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole());
        var micProcessorLogger = processorLoggerFactory.CreateLogger<AdvancedAudioProcessor>();
        var speakerProcessorLogger = processorLoggerFactory.CreateLogger<AdvancedAudioProcessor>();

        // Initialize mic processor with noise suppression and AGC
        _micProcessor = new AdvancedAudioProcessor(
            micProcessorLogger, _dspCfg, _micFormat!.SampleRate, _micFormat.Channels);

        // Initialize speaker processor with normalization and limiting
        _speakerProcessor = new AdvancedAudioProcessor(
            speakerProcessorLogger, _dspCfg, _speakerFormat!.SampleRate, _speakerFormat.Channels);

        // Initialize AEC processor for echo cancellation
        _aecProcessor = _aecFactory.Create(_dspCfg);
        _aecProcessor.Configure(_dspCfg, Math.Max(_micFormat.SampleRate, _speakerFormat.SampleRate), _dspCfg.FrameMs);

        // Initialize VADs with actual sample rates
        _micVad = new VoiceActivityDetector(_micFormat.SampleRate);
        _speakerVad = new VoiceActivityDetector(_speakerFormat.SampleRate);

        _logger.LogInformation("Initialized real-time audio processors with echo cancellation");
    }

    private void HandleCaptureError(string source, Exception? exception)
    {
        if (exception != null)
        {
            _logger.LogError(exception, "{source} capture stopped with error", source);
        }
        else
        {
            _logger.LogInformation("{source} capture stopped normally", source);
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (!_running) return;
            _running = false;
        }

        try
        {
            _micCapture?.StopRecording();
            _speakerCapture?.StopRecording();
            
            _micProcessor?.Dispose();
            _speakerProcessor?.Dispose();
            _aecProcessor?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during audio capture shutdown");
        }
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (e.BytesRecorded <= 0) return;

            var timestamp = DateTime.UtcNow;
            LastMicActivityUtc = timestamp;
            MicBytesSinceStart += e.BytesRecorded;

            // Use RAW mic for AEC input (no pre-processing here to avoid double-processing)
            var buf = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, buf, 0, e.BytesRecorded);

            // Voice activity detection on raw audio
            int bytesPerSample = _micFormat!.BitsPerSample / 8;
            if (_micVad.DetectVoice(buf, bytesPerSample))
            {
                LastMicVoiceActivityUtc = timestamp;
            }

            // Update signal level monitoring (compute from raw)
            var floatSamples = ConvertToFloat(buf, buf.Length, _micFormat);
            UpdateSignalLevel(floatSamples, ref _micSignalLevel);

            // Store in prebuffer
            var chunk = new TimestampedAudioChunk(buf, timestamp);
            _micBuffer.Enqueue(chunk);
            var newTotal = Interlocked.Add(ref _micBufferedBytes, buf.Length);

            // Manage buffer size
            while (newTotal > _micMaxPrebufferBytes && _micBuffer.TryDequeue(out var old))
            {
                newTotal = Interlocked.Add(ref _micBufferedBytes, -old.Data.Length);
            }

            // Pass to active writers
            PassToWriters(buf, _activeMicWriter, (writer, data) => writer.Write(data, 0, data.Length));

            // Pass to stereo writer (left channel = mic, RAW -> AEC -> post)
            _stereoWriter?.AppendMic(buf, _micFormat);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing microphone data");
        }
    }

    private void OnSpeakerDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (e.BytesRecorded <= 0) return;

            var timestamp = DateTime.UtcNow;
            LastSpeakerActivityUtc = timestamp;
            SpeakerBytesSinceStart += e.BytesRecorded;

            // Use RAW far-end for AEC reverse stream (no pre-processing here)
            var buf = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, buf, 0, e.BytesRecorded);

            // Voice activity detection on raw audio
            int bytesPerSample = _speakerFormat!.BitsPerSample / 8;
            if (_speakerVad.DetectVoice(buf, bytesPerSample))
            {
                LastSpeakerVoiceActivityUtc = timestamp;
            }

            // Update signal level monitoring (compute from raw)
            var floatSamples = ConvertToFloat(buf, buf.Length, _speakerFormat);
            UpdateSignalLevel(floatSamples, ref _speakerSignalLevel);

            // Store in prebuffer
            var chunk = new TimestampedAudioChunk(buf, timestamp);
            _speakerBuffer.Enqueue(chunk);
            var newTotal = Interlocked.Add(ref _speakerBufferedBytes, buf.Length);

            // Manage buffer size
            while (newTotal > _speakerMaxPrebufferBytes && _speakerBuffer.TryDequeue(out var old))
            {
                newTotal = Interlocked.Add(ref _speakerBufferedBytes, -old.Data.Length);
            }

            // Pass to active writers
            PassToWriters(buf, _activeSpeakerWriter, (writer, data) => writer.Write(data, 0, data.Length));

            // Pass to stereo writer (right channel = far-end loopback, RAW -> post)
            _stereoWriter?.AppendSpeaker(buf, _speakerFormat);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing speaker data");
        }
    }

    private void UpdateSignalLevel(ReadOnlySpan<float> samples, ref float currentLevel)
    {
        float rms = 0f;
        foreach (var sample in samples)
        {
            rms += sample * sample;
        }
        rms = MathF.Sqrt(rms / samples.Length);
        currentLevel = currentLevel * 0.9f + rms * 0.1f; // Smooth update
    }

    private void PassToWriters<T>(T data, WaveFileWriter? writer, Action<WaveFileWriter, T> writeAction)
    {
        if (writer != null)
        {
            lock (writer)
            {
                try
                {
                    writeAction(writer, data);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error writing to audio file");
                }
            }
        }
    }

    private float[] ConvertToFloat(byte[] bytes, int length, WaveFormat format)
    {
        if (format.BitsPerSample == 16)
        {
            var samples = new float[length / 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short sample = BitConverter.ToInt16(bytes, i * 2);
                samples[i] = sample / 32768f;
            }
            return samples;
        }
        else if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            var samples = new float[length / 4];
            Buffer.BlockCopy(bytes, 0, samples, 0, length);
            return samples;
        }
        else
        {
            throw new NotSupportedException($"Unsupported audio format: {format.BitsPerSample}-bit {format.Encoding}");
        }
    }

    private byte[] ConvertToBytes(ReadOnlySpan<float> samples, WaveFormat format)
    {
        if (format.BitsPerSample == 16)
        {
            var bytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                var clamped = Math.Max(-1f, Math.Min(1f, samples[i]));
                short sample = (short)(clamped * 32767f);
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), sample);
            }
            return bytes;
        }
        else if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            var bytes = new byte[samples.Length * 4];
            Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
            return bytes;
        }
        else
        {
            throw new NotSupportedException($"Unsupported audio format: {format.BitsPerSample}-bit {format.Encoding}");
        }
    }

    // Implement existing interface methods...
    public void AttachWriters(WaveFileWriter micWriter, WaveFileWriter speakerWriter)
    {
        _activeMicWriter = micWriter;
        _activeSpeakerWriter = speakerWriter;
    }

    public void DetachWriters()
    {
        _activeMicWriter = null;
        _activeSpeakerWriter = null;
    }

    public void AttachStereoWriter(IStereoWriter writer)
    {
        _stereoWriter = writer;
    }

    public void DetachStereoWriter()
    {
        _stereoWriter = null;
    }

    public (long micBytes, long speakerBytes) FlushPrebufferTo(WaveFileWriter micWriter, WaveFileWriter speakerWriter)
    {
        long micFlushed = 0;
        long spkFlushed = 0;

        while (_micBuffer.TryDequeue(out var micChunk))
        {
            micWriter.Write(micChunk.Data, 0, micChunk.Data.Length);
            micFlushed += micChunk.Data.Length;
            Interlocked.Add(ref _micBufferedBytes, -micChunk.Data.Length);
        }

        while (_speakerBuffer.TryDequeue(out var spkChunk))
        {
            speakerWriter.Write(spkChunk.Data, 0, spkChunk.Data.Length);
            spkFlushed += spkChunk.Data.Length;
            Interlocked.Add(ref _speakerBufferedBytes, -spkChunk.Data.Length);
        }

        _logger.LogInformation("Flushed enhanced prebuffer: mic={micBytes} bytes, speaker={spkBytes} bytes", 
            micFlushed, spkFlushed);
        return (micFlushed, spkFlushed);
    }

    public void FlushPrebufferToStereo(IStereoWriter writer)
    {
        // Enhanced synchronization during flush
        var micChunks = new List<TimestampedAudioChunk>();
        var speakerChunks = new List<TimestampedAudioChunk>();

        // Collect all chunks with timestamps
        while (_micBuffer.TryDequeue(out var micChunk))
        {
            micChunks.Add(micChunk);
            Interlocked.Add(ref _micBufferedBytes, -micChunk.Data.Length);
        }

        while (_speakerBuffer.TryDequeue(out var spkChunk))
        {
            speakerChunks.Add(spkChunk);
            Interlocked.Add(ref _speakerBufferedBytes, -spkChunk.Data.Length);
        }

        // Sort by timestamp for proper synchronization
        micChunks.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        speakerChunks.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        // Apply discard if configured
        ApplyInitialDiscard(micChunks, speakerChunks);

        // Write in synchronized order
        WriteSynchronizedAudio(writer, micChunks, speakerChunks);

        _logger.LogInformation("Flushed synchronized prebuffer: {micCount} mic chunks, {spkCount} speaker chunks", 
            micChunks.Count, speakerChunks.Count);
    }

    private void ApplyInitialDiscard(List<TimestampedAudioChunk> micChunks, List<TimestampedAudioChunk> speakerChunks)
    {
        if (_cfg.DiscardInitialMs <= 0) return;

        var discardDuration = TimeSpan.FromMilliseconds(_cfg.DiscardInitialMs);
        var cutoffTime = _captureStartTime.Add(discardDuration);

        micChunks.RemoveAll(chunk => chunk.Timestamp < cutoffTime);
        speakerChunks.RemoveAll(chunk => chunk.Timestamp < cutoffTime);
    }

    private void WriteSynchronizedAudio(IStereoWriter writer, List<TimestampedAudioChunk> micChunks, List<TimestampedAudioChunk> speakerChunks)
    {
        int micIndex = 0, speakerIndex = 0;

        // Interleave chunks based on timestamps for proper synchronization
        while (micIndex < micChunks.Count || speakerIndex < speakerChunks.Count)
        {
            bool writeMic = false;

            if (micIndex >= micChunks.Count)
            {
                writeMic = false; // Only speaker chunks left
            }
            else if (speakerIndex >= speakerChunks.Count)
            {
                writeMic = true; // Only mic chunks left
            }
            else
            {
                // Compare timestamps
                writeMic = micChunks[micIndex].Timestamp <= speakerChunks[speakerIndex].Timestamp;
            }

            if (writeMic)
            {
                writer.AppendMic(micChunks[micIndex].Data, MicFormat);
                micIndex++;
            }
            else
            {
                writer.AppendSpeaker(speakerChunks[speakerIndex].Data, SpeakerFormat);
                speakerIndex++;
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _micCapture?.Dispose();
        _speakerCapture?.Dispose();
        _micProcessor?.Dispose();
        _speakerProcessor?.Dispose();
        _aecProcessor?.Dispose();
    }
}

/// <summary>
/// Audio chunk with timestamp for synchronization
/// </summary>
public record TimestampedAudioChunk(byte[] Data, DateTime Timestamp);
