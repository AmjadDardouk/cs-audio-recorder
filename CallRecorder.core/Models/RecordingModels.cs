namespace CallRecorder.Core.Models;

public enum AudioChannel
{
    Left,   // Agent microphone
    Right   // Customer speakers
}

public class ProcessInfo
{
    public int Id { get; set; }
    public string? ProcessName { get; set; }
    public string? WindowTitle { get; set; }
}

public class RecordingMetadata
{
    public string RecordingId { get; set; } = string.Empty;
    public string? AgentId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;

    // Single stereo output (L=mic, R=speakers)
    public string? FilePath { get; set; }

    // Legacy fields (no longer used once stereo is enabled)
    public string? MicrophoneFilePath { get; set; }
    public string? SpeakerFilePath { get; set; }
}
