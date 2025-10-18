namespace CallRecorder.Core.Config;

public class CallDetectionConfig
{
    // Poll interval in milliseconds for detecting call state
    public int PollIntervalMs { get; set; } = 500;

    // Confidence threshold (0-100) to consider a call active
    public int ConfidenceThreshold { get; set; } = 70;

    // Optional: minimum seconds to consider a call valid before stopping
    public int MinimumCallDurationSeconds { get; set; } = 5;

    // Inactivity hangover before stopping (ms)
    public int StopHangoverMs { get; set; } = 3000;

    // Start recording immediately on any recent audio signal (mic or speaker), not only on VAD voice
    public bool StartOnAnySignal { get; set; } = true;
}
