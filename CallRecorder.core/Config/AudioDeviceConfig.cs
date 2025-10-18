namespace CallRecorder.Core.Config;

public class AudioDeviceConfig
{
    // Prefer Windows "Communications" role devices when selecting defaults (set false to avoid comms mics by default)
    public bool PreferCommunicationsEndpoints { get; set; } = false;

    // Optional exact device IDs (from MMDevice.ID). If set, they take precedence.
    public string? MicDeviceId { get; set; }
    public string? SpeakerDeviceId { get; set; }

    // Enhanced device filtering for high-quality audio capture
    public string[] MicInclude { get; set; } = new[] { "Microphone", "Headset", "Mic", "Studio", "Condenser", "Dynamic", "USB" };
    public string[] MicExclude { get; set; } = new[] { 
        "Line In", "Stereo Mix", "What U Hear", "Loopback", "Hands-Free", 
        "Array", "Virtual", "Communications", "Webcam", "Built-in", "Internal",
        "Realtek", "HD Audio", "Generic", "Bluetooth", "A2DP" 
    };

    public string[] SpeakerInclude { get; set; } = new[] { "Speakers", "Headset", "Headphones", "Studio", "Monitor", "USB" };
    public string[] SpeakerExclude { get; set; } = new[] { 
        "Communications", "Bluetooth", "A2DP", "Virtual", "Generic" 
    };

    // Device quality assessment
    public bool EnableDeviceQualityScoring { get; set; } = true;
    public int MinSampleRateHz { get; set; } = 44100; // Minimum 44.1kHz; prefer 48k via PreferHighSampleRates
    public int MinBitsPerSample { get; set; } = 16; // Minimum bit depth
    public bool RequireNativeFormat { get; set; } = false; // Prefer devices with native format support
    
    // Device capability preferences
    public bool PreferHighSampleRates { get; set; } = true; // Prefer 48kHz+ devices
    public bool PreferLowLatencyDevices { get; set; } = true;
    public bool AvoidSharedModeDevices { get; set; } = false; // Prefer exclusive mode capable devices
    
    // Device warm-up and testing
    public bool TestDevicesBeforeSelection { get; set; } = true;
    public int TestDurationMs { get; set; } = 100; // Quick test recording duration
    public float MinSignalToNoiseRatio { get; set; } = 20.0f; // Minimum SNR per tests; recommend higher in production
    
    // Advanced device detection
    public bool DetectAudioInterfaces { get; set; } = true; // Prefer professional audio interfaces
    public string[] PreferredManufacturers { get; set; } = new[] { 
        "Focusrite", "PreSonus", "Steinberg", "RME", "MOTU", "Universal Audio",
        "Zoom", "Tascam", "Roland", "Yamaha", "Behringer", "Audio-Technica",
        "Shure", "Rode", "Blue", "Samson", "AKG", "Sennheiser"
    };
    
    // Fallback behavior
    public bool AllowFallbackToAnyDevice { get; set; } = true;
    public bool WarnOnLowQualityDevice { get; set; } = true;

    // Log all devices found to help diagnose selection issues
    public bool LogDeviceEnumeration { get; set; } = true;
    public bool LogDeviceCapabilities { get; set; } = true;
    public bool LogQualityScores { get; set; } = true;

    // RAW mode enforcement and sidetone rejection
    public bool ForceRawMode { get; set; } = true;
    public bool RejectSidetone { get; set; } = true;
}
