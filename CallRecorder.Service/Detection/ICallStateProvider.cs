using System.Threading;
using System.Threading.Tasks;
using CallRecorder.Core.Models;

namespace CallRecorder.Service.Detection;

/// <summary>
/// Pluggable provider for determining call state (Ringing, Connected, Ended)
/// using platform events (preferred) and VAD/audio heuristics as fallback.
/// </summary>
public interface ICallStateProvider
{
    /// <summary>
    /// Current coarse call phase.
    /// </summary>
    CallPhase CurrentPhase { get; }

    /// <summary>
    /// The most likely source process (e.g., teams.exe) associated with the call.
    /// </summary>
    string? SourceProcess { get; }

    /// <summary>
    /// Start any background monitoring required.
    /// </summary>
    Task StartAsync(CancellationToken token);

    /// <summary>
    /// Stop background monitoring.
    /// </summary>
    void Stop();

    /// <summary>
    /// Poll/evaluate state now, returning latest phase. Implementation should
    /// incorporate both platform session info and recent VAD activity.
    /// </summary>
    CallPhase Evaluate();
}
