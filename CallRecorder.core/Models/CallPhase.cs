namespace CallRecorder.Core.Models;

/// <summary>
/// High-level call phases used to control start/stop and pre/post-roll behavior.
/// </summary>
public enum CallPhase
{
    Idle = 0,
    Ringing = 1,
    Connected = 2,
    Ended = 3
}
