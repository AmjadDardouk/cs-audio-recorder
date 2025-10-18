namespace CallRecorder.Core.Config;

public class RecordingConfig
{
    // Where to store local recordings (WAV for MVP)
    public string OutputDirectory { get; set; } = @"C:\ProgramData\CallRecorder\Temp";

    // Length of rolling pre-buffer in seconds to ensure zero-loss start
    public int PreBufferSeconds { get; set; } = 10;
    
    // Post-roll seconds to continue capturing after call end
    public int PostRollSeconds { get; set; } = 3;

    // Optionally discard the first N milliseconds from the rolling prebuffer to avoid init thumps
    public int DiscardInitialMs { get; set; } = 150;

    // Audio format - Enhanced for professional quality
    public int SampleRate { get; set; } = 48000; // Professional broadcast standard
    public int BitsPerSample { get; set; } = 16; // CD quality, optimal for voice
    public int Channels { get; set; } = 2; // Stereo (L=mic, R=speakers)
    
    // Enhanced quality settings
    public bool UseHighQualityResampling { get; set; } = true;
    public int BufferSize { get; set; } = 1024; // Optimal buffer size for low latency
    public bool EnableLookaheadLimiting { get; set; } = true;
    public int LookaheadMs { get; set; } = 5; // Lookahead time for limiting
    
    // Device warm-up and initialization
    public bool PreWarmDevices { get; set; } = true;
    public int PreWarmDurationMs { get; set; } = 500; // Time to warm up devices before recording
    public bool UseExclusiveMode { get; set; } = false; // WASAPI exclusive mode for minimal latency
    
    // Memory management for continuous recording
    public bool UseMemoryPools { get; set; } = true;
    public int MaxMemoryPoolSizeMB { get; set; } = 50; // Maximum memory pool size
    public bool EnableGCOptimizations { get; set; } = true;
    
    // Advanced quality features
    public bool EnableDcRemoval { get; set; } = true; // Remove DC offset
    public bool EnableDithering { get; set; } = true; // Add dithering for better quantization
    public float DitherAmountDb { get; set; } = -96f; // Dither amplitude
}
