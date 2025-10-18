namespace CallRecorder.Core.Models;

public class CallState
{
    public bool IsActive { get; set; }
    public int Confidence { get; set; }
    public string? ProcessName { get; set; }
    public int? ProcessId { get; set; }
    public DateTime DetectedAt { get; set; }

    public static CallState NoCall => new CallState
    {
        IsActive = false,
        Confidence = 0,
        DetectedAt = DateTime.UtcNow
    };
}
