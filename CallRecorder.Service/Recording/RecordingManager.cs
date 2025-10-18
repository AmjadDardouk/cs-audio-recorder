using CallRecorder.Core.Config;
using CallRecorder.Core.Models;
using CallRecorder.Service.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CallRecorder.Service.Recording;

public interface IRecordingManager
{
    bool IsRecording { get; }
    DateTime? StartTimeUtc { get; }
    Task StartAsync(string? sourceLabel = null);
    Task<RecordingMetadata?> StopAsync();
}

internal class RecordingManager : IRecordingManager, IDisposable
{
    private readonly ILogger<RecordingManager> _logger;
    private readonly IAudioCaptureEngine _audio;
    private readonly RecordingConfig _recCfg;

    private IStereoWriter? _stereoWriter;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<RecordingConfig> _recCfgOptions;
    private readonly IAecProcessorFactory _aecFactory;
    private readonly IOptions<AudioDspConfig> _dspOptions;
    private int _startStopGate = 0;

    public bool IsRecording { get; private set; }
    public DateTime? StartTimeUtc { get; private set; }

    public RecordingManager(
        ILogger<RecordingManager> logger,
        ILoggerFactory loggerFactory,
        IAudioCaptureEngine audio,
        IOptions<RecordingConfig> recCfg,
        IAecProcessorFactory aecFactory,
        IOptions<AudioDspConfig> dspCfg)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _audio = audio;
        _recCfgOptions = recCfg;
        _recCfg = recCfg.Value;
        _aecFactory = aecFactory;
        _dspOptions = dspCfg;
    }

    public async Task StartAsync(string? sourceLabel = null)
    {
        // Serialize start/stop transitions and make start idempotent under race
        if (Interlocked.CompareExchange(ref _startStopGate, 1, 0) != 0)
        {
            _logger.LogWarning("Start ignored: start/stop already in progress");
            return;
        }
        try
        {
            if (IsRecording)
            {
                _logger.LogWarning("Recording already active");
                return;
            }

            var id = Guid.NewGuid().ToString("N");

            // Create single stereo writer (L=mic, R=speakers)
            var aec = _aecFactory.Create(_dspOptions.Value);
            _stereoWriter = new StereoInterleavingWriter(
                _loggerFactory.CreateLogger<StereoInterleavingWriter>(),
                _recCfgOptions,
                id,
                _audio.MicFormat,
                _audio.SpeakerFormat,
                aec,
                _dspOptions.Value,
                sourceLabel);

            // Flush prebuffer then enable live passthrough to stereo writer
            _audio.FlushPrebufferToStereo(_stereoWriter);
            _audio.AttachStereoWriter(_stereoWriter);

            StartTimeUtc = DateTime.UtcNow;
            IsRecording = true;

            _logger.LogInformation("Recording started (stereo). file={file}", (_stereoWriter as StereoInterleavingWriter)?.OutputPath);

            await Task.CompletedTask;
        }
        finally
        {
            Volatile.Write(ref _startStopGate, 0);
        }
    }

    public async Task<RecordingMetadata?> StopAsync()
    {
        // Serialize start/stop transitions and make stop idempotent under race
        if (Interlocked.CompareExchange(ref _startStopGate, 1, 0) != 0)
        {
            _logger.LogWarning("Stop ignored: start/stop already in progress");
            return null;
        }
        try
        {
            if (!IsRecording)
            {
                _logger.LogWarning("No active recording to stop");
                return null;
            }

            // Stop passthrough
            _audio.DetachStereoWriter();

            // Finalize and dispose stereo writer
            string? path = null;
            List<string>? segments = null;
            try
            {
                if (_stereoWriter is StereoInterleavingWriter siw)
                {
                    path = siw.OutputPath;
                    // capture all segment paths before disposing
                    segments = siw.SegmentPaths?.ToList();
                }
                _stereoWriter?.FinalizeFlush();
                _stereoWriter?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing stereo writer");
            }
            finally
            {
                _stereoWriter = null;
            }

            // If multiple segments were produced (e.g., writer recovery), merge into a single file
            try
            {
                if (segments != null && segments.Count > 1)
                {
                    // Use the first segment's path as the final output target
                    var finalPath = segments[0];
                    await WavPostProcessor.MergeSegmentsAsync(segments, finalPath, _logger);
                    path = finalPath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to merge segments for recording");
            }

            // Optional offline post-normalization for consistent loudness and peak safety
            try
            {
                if (_dspOptions.Value.PostNormalize && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    await WavPostProcessor.NormalizeAsync(path!, _dspOptions.Value, _logger);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-normalization failed for {file}", path);
            }

            var endUtc = DateTime.UtcNow;

            var meta = new RecordingMetadata
            {
                RecordingId = Path.GetFileNameWithoutExtension(path ?? string.Empty).Split('_').LastOrDefault() ?? Guid.NewGuid().ToString("N"),
                StartTime = StartTimeUtc ?? endUtc,
                EndTime = endUtc,
                FilePath = path
            };

            _logger.LogInformation("Recording stopped. Duration={sec}s, file={file}",
                meta.Duration.TotalSeconds.ToString("0.0"),
                meta.FilePath);

            // Reset state
            IsRecording = false;
            StartTimeUtc = null;

            return await Task.FromResult(meta);
        }
        finally
        {
            Volatile.Write(ref _startStopGate, 0);
        }
    }

    public void Dispose()
    {
        try
        {
            _stereoWriter?.Dispose();
        }
        catch { /* ignore */ }
    }
}
