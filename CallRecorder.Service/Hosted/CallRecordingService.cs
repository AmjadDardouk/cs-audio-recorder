using CallRecorder.Core.Config;
using CallRecorder.Core.Models;
using CallRecorder.Service.Audio;
using CallRecorder.Service.Detection;
using CallRecorder.Service.Recording;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CallRecorder.Service.Hosted;

/// <summary>
/// Orchestrates audio engine lifetime, polling-based call detection,
/// and recording start/stop transitions.
/// </summary>
internal class CallRecordingService : BackgroundService
{
    private readonly ILogger<CallRecordingService> _logger;
    private readonly IAudioCaptureEngine _audio;
    private readonly CallDetectionEngine _detection;
    private readonly IRecordingManager _recManager;
    private readonly CallDetectionConfig _detectCfg;
    private readonly ICallStateProvider _callState;
    private readonly RecordingConfig _recCfg;
    private CallPhase _lastPhase = CallPhase.Idle;

    // Enhanced debounce to avoid flapping
    private bool _lastActive = false;
    private DateTime _lastStateChangeUtc = DateTime.MinValue;
    private DateTime _callStartUtc = DateTime.MinValue;
    private int _consecutiveInactiveDetections = 0;

    public CallRecordingService(
        ILogger<CallRecordingService> logger,
        IAudioCaptureEngine audio,
        CallDetectionEngine detection,
        IRecordingManager recManager,
        IOptions<CallDetectionConfig> detectCfg,
        ICallStateProvider callState,
        IOptions<RecordingConfig> recCfg)
    {
        _logger = logger;
        _audio = audio;
        _detection = detection;
        _recManager = recManager;
        _detectCfg = detectCfg.Value;
        _callState = callState;
        _recCfg = recCfg.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CallRecordingService starting");

        // Start continuous audio capture
        await _audio.StartAsync(stoppingToken);

        // Start call-state provider (Windows audio-session + VAD heuristics)
        await _callState.StartAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Evaluate call state (Ringing -> Connected -> Ended)
                var phase = _callState.Evaluate();
                var now = DateTime.UtcNow;
                var prevPhase = _lastPhase;

                // Track phase changes
                if (phase != _lastPhase)
                {
                    _logger.LogInformation("Call phase: {prev} -> {next}", prevPhase, phase);
                    _lastPhase = phase;
                    _lastStateChangeUtc = now;
                }

                // Derive voice activity to guard stop conditions (hangover)
                bool micVoiceActive = (now - _audio.LastMicVoiceActivityUtc) <= TimeSpan.FromMilliseconds(Math.Max(500, Math.Min(_detectCfg.StopHangoverMs, 2000)));
                bool speakerVoiceActive = (now - _audio.LastSpeakerVoiceActivityUtc) <= TimeSpan.FromMilliseconds(Math.Max(500, Math.Min(_detectCfg.StopHangoverMs, 2000)));
                bool voiceActive = micVoiceActive || speakerVoiceActive;

                bool shouldStartRecording = false;
                bool shouldStopRecording = false;

                // Start when transitioning into Connected (edge-triggered)
                if (phase == CallPhase.Connected && prevPhase != CallPhase.Connected && !_recManager.IsRecording)
                {
                    shouldStartRecording = true;
                    _callStartUtc = now;
                }

                // Stop after Ended + post-roll and sustained inactivity
                if (phase == CallPhase.Ended && _recManager.IsRecording)
                {
                    var postRollElapsed = (now - _lastStateChangeUtc) >= TimeSpan.FromSeconds(Math.Max(0, _recCfg.PostRollSeconds));
                    var inactivity = !voiceActive &&
                                     (now - _audio.LastMicActivityUtc) >= TimeSpan.FromMilliseconds(_detectCfg.StopHangoverMs) &&
                                     (now - _audio.LastSpeakerActivityUtc) >= TimeSpan.FromMilliseconds(_detectCfg.StopHangoverMs);

                    if (postRollElapsed && inactivity)
                    {
                        shouldStopRecording = true;
                    }
                }

                if (shouldStartRecording)
                {
                    _logger.LogInformation("📞 Phase=Connected (edge). Starting recording... source={src}", _callState.SourceProcess ?? "unknown");
                    await _recManager.StartAsync(_callState.SourceProcess);
                }

                if (shouldStopRecording)
                {
                    var callDuration = (DateTime.UtcNow - _callStartUtc).TotalSeconds;

                    if (callDuration >= _detectCfg.MinimumCallDurationSeconds)
                    {
                        _logger.LogInformation("📞 Phase=Ended. Stopping recording...");
                        var meta = await _recManager.StopAsync();
                        if (meta != null)
                        {
                            _logger.LogInformation("✅ Saved recording: {file}, duration: {sec}s",
                                meta.FilePath, meta.Duration.TotalSeconds.ToString("0.0"));
                        }
                    }
                    else
                    {
                        _logger.LogInformation("⏱️ Call too short ({sec}s < {min}s). Discarding recording.",
                            callDuration.ToString("0.0"), _detectCfg.MinimumCallDurationSeconds);
                        await _recManager.StopAsync();
                    }

                    _callStartUtc = DateTime.MinValue;
                }

                await Task.Delay(_detectCfg.PollIntervalMs, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in CallRecordingService loop");
        }
        finally
        {
            try
            {
                if (_recManager.IsRecording)
                {
                    await _recManager.StopAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping active recording during shutdown");
            }

            _audio.Stop();
            _logger.LogInformation("CallRecordingService stopped");
        }
    }
}
