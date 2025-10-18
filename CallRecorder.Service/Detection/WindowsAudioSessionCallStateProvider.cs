using System;
using System.Collections;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CallRecorder.Core.Config;
using CallRecorder.Core.Models;
using CallRecorder.Service.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;

namespace CallRecorder.Service.Detection;

/// <summary>
/// Default call-state provider for Windows:
/// - Prefers Windows audio session heuristics (IAudioSessionManager2) to detect communications activity
/// - Falls back to robust VAD-based heuristics using IAudioCaptureEngine activity timestamps
/// Produces coarse phases: Ringing, Connected, Ended.
/// </summary>
public sealed class WindowsAudioSessionCallStateProvider : ICallStateProvider, IDisposable
{
    private readonly ILogger<WindowsAudioSessionCallStateProvider> _logger;
    private readonly IAudioCaptureEngine _audio;
    private readonly CallStateConfig _cfg;

    private readonly MMDeviceEnumerator _enum = new();
    private MMDevice? _renderEndpoint;
    private object? _sessionMgr; // use reflection to avoid hard dependency on specific NAudio types
    private volatile bool _running;

    private DateTime _lastAnySignalUtc = DateTime.MinValue;
    private DateTime _lastBiDirectionalVoiceUtc = DateTime.MinValue;
    private DateTime _phaseChangedUtc = DateTime.MinValue;

    public CallPhase CurrentPhase { get; private set; } = CallPhase.Idle;
    public string? SourceProcess { get; private set; }

    public WindowsAudioSessionCallStateProvider(
        ILogger<WindowsAudioSessionCallStateProvider> logger,
        IAudioCaptureEngine audio,
        IOptions<CallStateConfig> cfg)
    {
        _logger = logger;
        _audio = audio;
        _cfg = cfg.Value;
    }

    public async Task StartAsync(CancellationToken token)
    {
        if (_running) return;
        _running = true;

        try
        {
            // Prefer communications render endpoint, allow fallback if configured
            _renderEndpoint = TryGetRenderEndpoint(eCommunications: _cfg.CommunicationsOnly, allowFallback: _cfg.AllowGeneralRenderFallback);
            if (_renderEndpoint != null)
            {
                try
                {
                    // Use reflection to access AudioSessionManager2 to support broader NAudio versions
                    try
                    {
                        _sessionMgr = _renderEndpoint?.GetType()
                            .GetProperty("AudioSessionManager2")
                            ?.GetValue(_renderEndpoint);
                    }
                    catch
                    {
                        _sessionMgr = null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to acquire AudioSessionManager2 from render endpoint.");
                    _sessionMgr = null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CallStateProvider initialization failed; will operate VAD-only.");
        }

        SourceProcess = GetLikelyCommunicationsProcessName();
        _logger.LogInformation("Call state provider started. initialPhase={phase}, sourceProcess={proc}", CurrentPhase, SourceProcess);
        await Task.CompletedTask;
    }

    public void Stop()
    {
        _running = false;
    }

    public CallPhase Evaluate()
    {
        var now = DateTime.UtcNow;

        // Track recent signals
        bool micRecent = (now - _audio.LastMicActivityUtc) <= TimeSpan.FromMilliseconds(Math.Max(_cfg.RingHoldMs, 500));
        bool spkRecent = (now - _audio.LastSpeakerActivityUtc) <= TimeSpan.FromMilliseconds(Math.Max(_cfg.RingHoldMs, 500));
        bool anySignal = micRecent || spkRecent;
        if (anySignal) _lastAnySignalUtc = now;

        // Track recent bi-directional voice (Connected indicator)
        bool micVoiceRecent = (now - _audio.LastMicVoiceActivityUtc) <= TimeSpan.FromMilliseconds(Math.Max(_cfg.ConnectBidirectionalWindowMs, 500));
        bool spkVoiceRecent = (now - _audio.LastSpeakerVoiceActivityUtc) <= TimeSpan.FromMilliseconds(Math.Max(_cfg.ConnectBidirectionalWindowMs, 500));
        bool biDirectional = (micVoiceRecent && spkVoiceRecent) || (micVoiceRecent && spkRecent) || (spkVoiceRecent && micRecent);
        if (biDirectional) _lastBiDirectionalVoiceUtc = now;

        // Periodically refresh likely source process
        if ((SourceProcess == null || SourceProcess == "unknown") || (now - _phaseChangedUtc) > TimeSpan.FromSeconds(5))
        {
            SourceProcess = GetLikelyCommunicationsProcessName();
        }

        // Determine phase transitions
        CallPhase next = CurrentPhase;

        switch (CurrentPhase)
        {
            case CallPhase.Idle:
            case CallPhase.Ended:
                if (anySignal)
                {
                    // Speaker render goes active (or mic activity) -> Ringing
                    next = CallPhase.Ringing;
                }
                break;

            case CallPhase.Ringing:
                if (biDirectional)
                {
                    next = CallPhase.Connected;
                }
                else if (!anySignal && (now - _lastAnySignalUtc) > TimeSpan.FromMilliseconds(_cfg.EndSilenceMs))
                {
                    // Ring ended without connection
                    next = CallPhase.Ended;
                }
                break;

            case CallPhase.Connected:
                // End when prolonged silence/inactivity
                if (!anySignal && (now - _lastAnySignalUtc) > TimeSpan.FromMilliseconds(_cfg.EndSilenceMs))
                {
                    next = CallPhase.Ended;
                }
                break;
        }

        if (next != CurrentPhase)
        {
            var prev = CurrentPhase;
            CurrentPhase = next;
            _phaseChangedUtc = now;
            _logger.LogInformation("Call phase change: {prev} -> {next} (source={proc})", prev, next, SourceProcess ?? "unknown");
        }

        return CurrentPhase;
    }

    private MMDevice? TryGetRenderEndpoint(bool eCommunications, bool allowFallback)
    {
        try
        {
            if (eCommunications)
            {
                var comms = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
                if (comms != null) return comms;
                if (!allowFallback) return null;
            }
            return _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get default render endpoint");
            return null;
        }
    }

    private string? GetLikelyCommunicationsProcessName()
    {
        try
        {
            if (_sessionMgr == null) return "unknown";

            // Reflectively call GetSessionEnumerator and inspect sessions to extract ProcessName when available.
            var mgrType = _sessionMgr.GetType();
            var getEnum = mgrType.GetMethod("GetSessionEnumerator");
            var enumObj = getEnum?.Invoke(_sessionMgr, null);
            if (enumObj == null) return "unknown";

            var list = new System.Collections.Generic.List<(string name, int state)>();

            foreach (var s in (IEnumerable)enumObj)
            {
                try
                {
                    // Try to get AudioSessionControl2 via generic QueryInterface<AudioSessionControl2>()
                    var sType = s.GetType();
                    var qmi = sType.GetMethod("QueryInterface");
                    object? s2 = null;

                    var asc2Type = Type.GetType("NAudio.CoreAudioApi.AudioSessionControl2, NAudio");
                    if (qmi != null && asc2Type != null && qmi.IsGenericMethodDefinition)
                    {
                        var qmiGen = qmi.MakeGenericMethod(asc2Type);
                        s2 = qmiGen.Invoke(s, null);
                    }

                    var ctrl = s2 ?? s; // fallback to original if QueryInterface not available

                    // Extract ProcessName if available
                    var procObj = ctrl?.GetType().GetProperty("Process")?.GetValue(ctrl);
                    var name = (procObj?.GetType().GetProperty("ProcessName")?.GetValue(procObj) as string)?.ToLowerInvariant();

                    // Extract State if available (active sessions preferred)
                    int state = 0;
                    var stateObj = ctrl?.GetType().GetProperty("State")?.GetValue(ctrl);
                    if (stateObj is int si) state = si;

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        list.Add((name!, state));
                    }

                    // Dispose s2 if it implements IDisposable
                    if (s2 is IDisposable disp) disp.Dispose();
                }
                catch
                {
                    // ignore bad session
                }
            }

            if (list.Count == 0) return "unknown";

            var whitelist = (_cfg.ProcessWhitelist ?? Array.Empty<string>()).Select(p => p.ToLowerInvariant()).ToHashSet();
            var blacklist = (_cfg.ProcessBlacklist ?? Array.Empty<string>()).Select(p => p.ToLowerInvariant()).ToHashSet();

            var filtered = list
                .Where(t => !_cfg.PreferWhitelistedProcesses || whitelist.Contains(t.name))
                .Where(t => !blacklist.Contains(t.name))
                .OrderByDescending(t => t.state == 1 ? 1 : 0) // 1 ~ Active
                .ThenBy(t => t.name)
                .ToList();

            return filtered.FirstOrDefault().name ?? list.First().name ?? "unknown";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enumerate audio sessions for process name");
            return "unknown";
        }
    }

    public void Dispose()
    {
        try { (_sessionMgr as IDisposable)?.Dispose(); } catch { }
        try { _renderEndpoint?.Dispose(); } catch { }
        try { _enum?.Dispose(); } catch { }
    }
}
