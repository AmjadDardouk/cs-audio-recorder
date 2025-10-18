using System.Collections.Concurrent;
using System.Linq;
using CallRecorder.Core.Config;
using CallRecorder.Service.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CallRecorder.Service.Audio;

// MVP: capture mic (mono) and speakers (mono loopback) continuously.
// Maintain small rolling pre-buffers for both channels (size based on PreBufferSeconds).
// Expose simple activity metrics and allow RecordingManager to consume live + buffered data.
public interface IAudioCaptureEngine : IDisposable
{
    Task StartAsync(CancellationToken token);
    void Stop();

    // Activity metrics for detection (updated on each DataAvailable)
    DateTime LastMicActivityUtc { get; }
    DateTime LastSpeakerActivityUtc { get; }
    long MicBytesSinceStart { get; }
    long SpeakerBytesSinceStart { get; }
    
    // Voice activity detection
    DateTime LastMicVoiceActivityUtc { get; }
    DateTime LastSpeakerVoiceActivityUtc { get; }

    // Device formats (as negotiated with the devices)
    WaveFormat MicFormat { get; }
    WaveFormat SpeakerFormat { get; }

    // Attach/detach active writers during a recording session (legacy per-channel)
    void AttachWriters(WaveFileWriter micWriter, WaveFileWriter speakerWriter);
    void DetachWriters();

    // New: Attach/detach single stereo writer (L=mic, R=speakers)
    void AttachStereoWriter(IStereoWriter writer);
    void DetachStereoWriter();

    // Flush buffered audio into provided writers at recording start
    (long micBytes, long speakerBytes) FlushPrebufferTo(WaveFileWriter micWriter, WaveFileWriter speakerWriter);

    // New: Flush buffered audio into stereo writer at recording start
    void FlushPrebufferToStereo(IStereoWriter writer);
}

internal class AudioCaptureEngine : IAudioCaptureEngine
{
    private readonly ILogger<AudioCaptureEngine> _logger;
    private readonly RecordingConfig _cfg;
    private readonly AudioDeviceConfig _devCfg;

    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _speakerCapture;
    private WaveFormat? _micFormat;
    private WaveFormat? _speakerFormat;

    private readonly object _stateLock = new();
    private bool _running;

    // Rolling pre-buffers per channel (store raw PCM frames as byte[] chunks)
    private readonly ConcurrentQueue<byte[]> _micBuffer = new();
    private readonly ConcurrentQueue<byte[]> _speakerBuffer = new();
    private long _micBufferedBytes;
    private long _speakerBufferedBytes;

    // Max bytes permitted in prebuffer per channel, set after device formats known
    private long _micMaxPrebufferBytes = 0;
    private long _speakerMaxPrebufferBytes = 0;

    // Activity metrics
    public DateTime LastMicActivityUtc { get; private set; } = DateTime.MinValue;
    public DateTime LastSpeakerActivityUtc { get; private set; } = DateTime.MinValue;
    public long MicBytesSinceStart { get; private set; }
    public long SpeakerBytesSinceStart { get; private set; }
    
    // Voice activity detection
    public DateTime LastMicVoiceActivityUtc { get; private set; } = DateTime.MinValue;
    public DateTime LastSpeakerVoiceActivityUtc { get; private set; } = DateTime.MinValue;
    
    private VoiceActivityDetector _micVad = new(48000);
    private VoiceActivityDetector _speakerVad = new(48000);

    // Expose negotiated device formats for creating WAV writers
    public WaveFormat MicFormat => _micFormat ?? _micCapture?.WaveFormat ?? throw new InvalidOperationException("Mic not initialized");
    public WaveFormat SpeakerFormat => _speakerFormat ?? _speakerCapture?.WaveFormat ?? throw new InvalidOperationException("Speaker not initialized");

    // Recording passthrough delegates (assigned by RecordingManager when active)
    private WaveFileWriter? _activeMicWriter;
    private WaveFileWriter? _activeSpeakerWriter;
    private readonly object _micWriterLock = new();
    private readonly object _speakerWriterLock = new();

    // Single stereo writer (L=mic, R=speakers)
    private IStereoWriter? _stereoWriter;

    public AudioCaptureEngine(ILogger<AudioCaptureEngine> logger, IOptions<RecordingConfig> cfg, IOptions<AudioDeviceConfig> devCfg)
    {
        _logger = logger;
        _cfg = cfg.Value;
        _devCfg = devCfg.Value;
    }

    private static bool IsIncluded(string name, string[]? include, string[]? exclude)
    {
        var n = name ?? string.Empty;
        if (exclude != null && exclude.Any(ex => n.IndexOf(ex, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
        if (include == null || include.Length == 0) return true;
        return include.Any(inc => n.IndexOf(inc, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private MMDevice SelectMic(MMDeviceEnumerator enumerator)
    {
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        if (_devCfg.LogDeviceEnumeration)
        {
            foreach (var d in devices)
                _logger.LogInformation("Capture device: {name} [{id}] state={state}", d.FriendlyName, d.ID, d.State);
        }

        if (!string.IsNullOrWhiteSpace(_devCfg.MicDeviceId))
        {
            try { return enumerator.GetDevice(_devCfg.MicDeviceId); } catch { /* ignore */ }
        }

        // Prefer Multimedia endpoint by default (avoid communications endpoints unless requested)
        Role first = _devCfg.PreferCommunicationsEndpoints ? Role.Communications : Role.Multimedia;
        Role second = _devCfg.PreferCommunicationsEndpoints ? Role.Multimedia : Role.Communications;

        // Try preferred default
        try
        {
            var ep = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, first);
            if (IsIncluded(ep.FriendlyName, _devCfg.MicInclude, _devCfg.MicExclude))
                return ep;
        }
        catch { /* ignore */ }

        // Try secondary default
        try
        {
            var ep = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, second);
            if (IsIncluded(ep.FriendlyName, _devCfg.MicInclude, _devCfg.MicExclude))
                return ep;
        }
        catch { /* ignore */ }

        // Try any included device
        var sel = devices.FirstOrDefault(d => IsIncluded(d.FriendlyName, _devCfg.MicInclude, _devCfg.MicExclude));
        if (sel != null) return sel;

        // Fall back to preferred default even if excluded, then first device
        try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, first); } catch { /* ignore */ }
        return devices.First();
    }

    private MMDevice SelectSpeaker(MMDeviceEnumerator enumerator)
    {
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        if (_devCfg.LogDeviceEnumeration)
        {
            foreach (var d in devices)
                _logger.LogInformation("Render device: {name} [{id}] state={state}", d.FriendlyName, d.ID, d.State);
        }

        if (!string.IsNullOrWhiteSpace(_devCfg.SpeakerDeviceId))
        {
            try { return enumerator.GetDevice(_devCfg.SpeakerDeviceId); } catch { /* ignore */ }
        }

        // Prefer Multimedia endpoint by default (avoid communications endpoints unless requested)
        Role first = _devCfg.PreferCommunicationsEndpoints ? Role.Communications : Role.Multimedia;
        Role second = _devCfg.PreferCommunicationsEndpoints ? Role.Multimedia : Role.Communications;

        // Try preferred default
        try
        {
            var ep = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, first);
            if (IsIncluded(ep.FriendlyName, _devCfg.SpeakerInclude, _devCfg.SpeakerExclude))
                return ep;
        }
        catch { /* ignore */ }

        // Try secondary default
        try
        {
            var ep = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, second);
            if (IsIncluded(ep.FriendlyName, _devCfg.SpeakerInclude, _devCfg.SpeakerExclude))
                return ep;
        }
        catch { /* ignore */ }

        // Try any included device
        var sel = devices.FirstOrDefault(d => IsIncluded(d.FriendlyName, _devCfg.SpeakerInclude, _devCfg.SpeakerExclude));
        if (sel != null) return sel;

        // Fall back to preferred default even if excluded, then first device
        try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, first); } catch { /* ignore */ }
        return devices.First();
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
            // Select devices using config + heuristics
            var enumerator = new MMDeviceEnumerator();
            var mic = SelectMic(enumerator);
            var spk = SelectSpeaker(enumerator);

            _micCapture = new WasapiCapture(mic); // capture from input device
            _speakerCapture = new WasapiLoopbackCapture(spk); // loopback from output device

            _micCapture.ShareMode = AudioClientShareMode.Shared;
            _speakerCapture.ShareMode = AudioClientShareMode.Shared;

            // Cache formats
            _micFormat = _micCapture.WaveFormat;
            _speakerFormat = _speakerCapture.WaveFormat;

            // Setup prebuffer sizes based on device formats and PreBufferSeconds
            _micMaxPrebufferBytes = (long)(_micCapture.WaveFormat.AverageBytesPerSecond * _cfg.PreBufferSeconds);
            _speakerMaxPrebufferBytes = (long)(_speakerCapture.WaveFormat.AverageBytesPerSecond * _cfg.PreBufferSeconds);

            // Reinitialize VADs with actual device sample rates
            _micVad = new VoiceActivityDetector(_micCapture.WaveFormat.SampleRate);
            _speakerVad = new VoiceActivityDetector(_speakerCapture.WaveFormat.SampleRate);

            _micCapture.DataAvailable += OnMicDataAvailable;
            _speakerCapture.DataAvailable += OnSpeakerDataAvailable;
            _micCapture.RecordingStopped += (s, e) =>
            {
                if (e.Exception != null) _logger.LogError(e.Exception, "Mic capture stopped with error");
            };
            _speakerCapture.RecordingStopped += (s, e) =>
            {
                if (e.Exception != null) _logger.LogError(e.Exception, "Speaker capture stopped with error");
            };

            _logger.LogInformation("Starting audio capture. Mic: {mic}, Speaker: {spk}", mic.FriendlyName, spk.FriendlyName);

            _micCapture.StartRecording();
            _speakerCapture.StartRecording();

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start audio capture");
            Stop();
            throw;
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
        }
        catch { /* ignore */ }
        try
        {
            _speakerCapture?.StopRecording();
        }
        catch { /* ignore */ }
    }

    public (long micBytes, long speakerBytes) FlushPrebufferTo(WaveFileWriter micWriter, WaveFileWriter speakerWriter)
    {
        long micFlushed = 0;
        long spkFlushed = 0;

        // Drain current prebuffers in FIFO order
        while (_micBuffer.TryDequeue(out var micChunk))
        {
            micWriter.Write(micChunk, 0, micChunk.Length);
            micFlushed += micChunk.Length;
            Interlocked.Add(ref _micBufferedBytes, -micChunk.Length);
        }
        while (_speakerBuffer.TryDequeue(out var spkChunk))
        {
            speakerWriter.Write(spkChunk, 0, spkChunk.Length);
            spkFlushed += spkChunk.Length;
            Interlocked.Add(ref _speakerBufferedBytes, -spkChunk.Length);
        }

        _logger.LogInformation("Flushed prebuffer (per-channel): mic={micBytes} bytes, speaker={spkBytes} bytes", micFlushed, spkFlushed);
        return (micFlushed, spkFlushed);
    }

    public void FlushPrebufferToStereo(IStereoWriter writer)
    {
        long micFlushed = 0;
        long spkFlushed = 0;

        // Compute optional discard from start of prebuffer to avoid init thumps
        int micDiscardRemaining = 0;
        int spkDiscardRemaining = 0;
        try
        {
            if (_cfg.DiscardInitialMs > 0)
            {
                micDiscardRemaining = (int)Math.Ceiling(MicFormat.AverageBytesPerSecond * (_cfg.DiscardInitialMs / 1000.0));
                spkDiscardRemaining = (int)Math.Ceiling(SpeakerFormat.AverageBytesPerSecond * (_cfg.DiscardInitialMs / 1000.0));
            }
        }
        catch { /* ignore */ }

        // Drain both prebuffers in approximate time order by alternating between channels.
        bool takeMicNext = true;
        while (!_micBuffer.IsEmpty || !_speakerBuffer.IsEmpty)
        {
            bool didWork = false;

            if (takeMicNext && _micBuffer.TryDequeue(out var micChunk))
            {
                // Apply discard if needed
                if (micDiscardRemaining >= micChunk.Length)
                {
                    micDiscardRemaining -= micChunk.Length;
                }
                else
                {
                    int offset = Math.Max(0, micDiscardRemaining);
                    micDiscardRemaining = 0;
                    var span = new ReadOnlySpan<byte>(micChunk, offset, micChunk.Length - offset);
                    writer.AppendMic(span, MicFormat);
                    micFlushed += span.Length;
                }
                Interlocked.Add(ref _micBufferedBytes, -micChunk.Length);
                didWork = true;
            }

            if (!takeMicNext && _speakerBuffer.TryDequeue(out var spkChunk))
            {
                if (spkDiscardRemaining >= spkChunk.Length)
                {
                    spkDiscardRemaining -= spkChunk.Length;
                }
                else
                {
                    int offset = Math.Max(0, spkDiscardRemaining);
                    spkDiscardRemaining = 0;
                    var span = new ReadOnlySpan<byte>(spkChunk, offset, spkChunk.Length - offset);
                    writer.AppendSpeaker(span, SpeakerFormat);
                    spkFlushed += span.Length;
                }
                Interlocked.Add(ref _speakerBufferedBytes, -spkChunk.Length);
                didWork = true;
            }

            // If the preferred side had no data, try the other side
            if (!didWork)
            {
                if (_micBuffer.TryDequeue(out var micChunk2))
                {
                    if (micDiscardRemaining >= micChunk2.Length)
                    {
                        micDiscardRemaining -= micChunk2.Length;
                    }
                    else
                    {
                        int offset = Math.Max(0, micDiscardRemaining);
                        micDiscardRemaining = 0;
                        var span = new ReadOnlySpan<byte>(micChunk2, offset, micChunk2.Length - offset);
                        writer.AppendMic(span, MicFormat);
                        micFlushed += span.Length;
                    }
                    Interlocked.Add(ref _micBufferedBytes, -micChunk2.Length);
                    didWork = true;
                }
                else if (_speakerBuffer.TryDequeue(out var spkChunk2))
                {
                    if (spkDiscardRemaining >= spkChunk2.Length)
                    {
                        spkDiscardRemaining -= spkChunk2.Length;
                    }
                    else
                    {
                        int offset = Math.Max(0, spkDiscardRemaining);
                        spkDiscardRemaining = 0;
                        var span = new ReadOnlySpan<byte>(spkChunk2, offset, spkChunk2.Length - offset);
                        writer.AppendSpeaker(span, SpeakerFormat);
                        spkFlushed += span.Length;
                    }
                    Interlocked.Add(ref _speakerBufferedBytes, -spkChunk2.Length);
                    didWork = true;
                }
            }

            // Alternate preference next iteration
            takeMicNext = !takeMicNext;

            // If neither had data (race), avoid a tight loop
            if (!didWork)
                break;
        }

        _logger.LogInformation("Flushed prebuffer (stereo): mic={micBytes} bytes, spk={spkBytes} bytes", micFlushed, spkFlushed);
    }

    // RecordingManager calls to connect writers for live passthrough
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

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (e.BytesRecorded > 0)
            {
                LastMicActivityUtc = DateTime.UtcNow;
                MicBytesSinceStart += e.BytesRecorded;
                
                // Check for voice activity (use actual device format bytes per sample)
                int micBytesPerSample = Math.Max(2, MicFormat.BitsPerSample / 8);
                if (_micVad.DetectVoice(e.Buffer.AsSpan(0, e.BytesRecorded), micBytesPerSample))
                {
                    LastMicVoiceActivityUtc = DateTime.UtcNow;
                }

                // Copy chunk and enqueue into prebuffer
                var chunk = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);

                _micBuffer.Enqueue(chunk);
                var newTotal = Interlocked.Add(ref _micBufferedBytes, chunk.Length);

                // Evict oldest if over capacity
                while (newTotal > _micMaxPrebufferBytes && _micBuffer.TryDequeue(out var old))
                {
                    newTotal = Interlocked.Add(ref _micBufferedBytes, -old.Length);
                }

                // If recording active, write-through (per-channel)
                var writer = _activeMicWriter;
                if (writer != null)
                {
                    lock (_micWriterLock)
                    {
                        writer.Write(chunk, 0, chunk.Length);
                    }
                }

                // If stereo writer attached, append to left channel
                var stereo = _stereoWriter;
                if (stereo != null)
                {
                    stereo.AppendMic(chunk, MicFormat);
                }
            }
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
            if (e.BytesRecorded > 0)
            {
                LastSpeakerActivityUtc = DateTime.UtcNow;
                SpeakerBytesSinceStart += e.BytesRecorded;
                
                // Check for voice activity (use actual device format bytes per sample)
                int spkBytesPerSample = Math.Max(2, SpeakerFormat.BitsPerSample / 8);
                if (_speakerVad.DetectVoice(e.Buffer.AsSpan(0, e.BytesRecorded), spkBytesPerSample))
                {
                    LastSpeakerVoiceActivityUtc = DateTime.UtcNow;
                }

                var chunk = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);

                _speakerBuffer.Enqueue(chunk);
                var newTotal = Interlocked.Add(ref _speakerBufferedBytes, chunk.Length);

                while (newTotal > _speakerMaxPrebufferBytes && _speakerBuffer.TryDequeue(out var old))
                {
                    newTotal = Interlocked.Add(ref _speakerBufferedBytes, -old.Length);
                }

                var writer = _activeSpeakerWriter;
                if (writer != null)
                {
                    lock (_speakerWriterLock)
                    {
                        writer.Write(chunk, 0, chunk.Length);
                    }
                }

                // If stereo writer attached, append to right channel
                var stereo = _stereoWriter;
                if (stereo != null)
                {
                    stereo.AppendSpeaker(chunk, SpeakerFormat);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing speaker data");
        }
    }

    public QualityMetricsResult GetQualityMetrics()
    {
        // Basic quality metrics for the simple implementation
        return new QualityMetricsResult
        {
            RmsLevelDb = -20f, // Placeholder
            PeakLevelDb = -6f, // Placeholder
            DynamicRange = 14f, // Placeholder
            SampleCount = MicBytesSinceStart + SpeakerBytesSinceStart
        };
    }

    public MemoryUsageStats GetMemoryStats()
    {
        // Basic memory stats for the simple implementation
        return new MemoryUsageStats
        {
            TotalAllocatedBytes = _micBufferedBytes + _speakerBufferedBytes,
            PoolAllocatedBytes = 0,
            PeakMemoryUsage = _micBufferedBytes + _speakerBufferedBytes,
            AudioBuffersPooled = _micBuffer.Count + _speakerBuffer.Count,
            ProcessingBuffersPooled = 0,
            GcTotalMemory = GC.GetTotalMemory(false),
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2)
        };
    }

    public void Dispose()
    {
        try
        {
            Stop();
            _micCapture?.Dispose();
            _speakerCapture?.Dispose();
        }
        catch { /* ignore */ }
    }
}
