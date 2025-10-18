namespace CallRecorder.Core.Config;

/// <summary>
/// Configuration for call-state detection based on Windows audio sessions and heuristics.
/// </summary>
public class CallStateConfig
{
    // If true, only consider sessions on the Windows "Communications" role endpoints as call candidates
    public bool CommunicationsOnly { get; set; } = true;

    // Optional process whitelist/blacklist for identifying softphone apps
    public string[] ProcessWhitelist { get; set; } = new[] { "teams.exe", "zoom.exe", "skype.exe", "lync.exe", "webex.exe" };
    public string[] ProcessBlacklist { get; set; } = new string[0];

    // Time windows and thresholds
    public int RingHoldMs { get; set; } = 2000; // consider as "ringing" when comms render session goes active
    public int ConnectBidirectionalWindowMs { get; set; } = 1500; // require mic OR speaker VAD within this window to confirm connected
    public int EndSilenceMs { get; set; } = 3000; // sustained inactivity to consider call ended

    // Optional RMS gating to avoid non-call low-level noise (dBFS)
    public float MinAverageRmsDbfs { get; set; } = -45f;

    // Optional: prefer sessions whose process names match whitelist (if any provided)
    public bool PreferWhitelistedProcesses { get; set; } = true;

    // Optional: allow general render sessions (non-comms) if no comms endpoint is present
    public bool AllowGeneralRenderFallback { get; set; } = true;
}
