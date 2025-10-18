namespace CallRecorder.Core.Config;

public class AudioDspConfig
{
    // Echo Cancellation
    public bool EchoCancellation { get; set; } = true;
    public string EchoSuppressionLevel { get; set; } = "High"; // Low, Moderate, High, VeryHigh
    public int EchoFilterLengthMs { get; set; } = 45; // Enhanced filter length for better echo coverage
    
    // Noise Suppression - Enhanced
    public bool NoiseSuppression { get; set; } = true;
    public string SuppressionLevel { get; set; } = "High"; // Low, Moderate, High, VeryHigh
    public bool SpectralSubtraction { get; set; } = true; // Advanced spectral noise reduction
    public float NoiseFloorDb { get; set; } = -60f; // Noise floor estimation
    public bool AdaptiveNoiseReduction { get; set; } = true; // Adaptive noise reduction
    
    // High-pass filter - Enhanced for better low-frequency noise removal
    public bool HighPass { get; set; } = true;
    public int HighPassHz { get; set; } = 80; // Cutoff frequency
    public int HighPassOrder { get; set; } = 2; // Filter order (1=6dB/oct, 2=12dB/oct, etc.)
    public string HighPassType { get; set; } = "Butterworth"; // Butterworth, Chebyshev, Elliptic
    
    // Low-pass filter - Enhanced for hiss removal
    public bool LowPass { get; set; } = true;
    public int LowPassHz { get; set; } = 9000; // Cutoff frequency
    public int LowPassOrder { get; set; } = 2; // Filter order
    public string LowPassType { get; set; } = "Butterworth"; // Filter type
    
    // Automatic Gain Control - Enhanced
    public bool Agc { get; set; } = true; // Enable AGC for better level consistency
    public float AgcTargetDb { get; set; } = -23f; // Target level (EBU R128 standard)
    public float AgcMaxGainDb { get; set; } = 12f; // Maximum gain
    public int AgcAttackMs { get; set; } = 10; // Fast attack for transients
    public int AgcReleaseMs { get; set; } = 100; // Medium release
    public bool AgcLimiter { get; set; } = true; // Built-in limiter

    // Frame length in milliseconds for processing
    public int FrameMs { get; set; } = 10; // 10ms frames for real-time processing

    // Target sample rate for processing
    public int SampleRate { get; set; } = 48000;

    // Per-channel gain adjustments (dB)
    public float NearGainDb { get; set; } = -3f;
    public float FarGainDb { get; set; } = -6f;

    // Real-time Volume Normalization - Enhanced
    public bool Normalize { get; set; } = true;
    public float TargetRmsDbfs { get; set; } = -20f; // Target RMS level
    public float TargetLufsDb { get; set; } = -23f; // LUFS target (broadcasting standard)
    public float MaxGainDb { get; set; } = 6f; // Maximum normalization gain
    public int AttackMs { get; set; } = 30; // Attack time
    public int ReleaseMs { get; set; } = 500; // Release time
    public bool UseGatedLoudness { get; set; } = true; // Use gated loudness measurement
    
    // Advanced Limiting and Clipping Protection
    public bool EnableLimiter { get; set; } = true;
    public float LimiterCeilingDbfs { get; set; } = -1.0f; // Sample-peak ceiling per spec (≤ -1.0 dBFS)
    public float LimiterThresholdDbfs { get; set; } = -1.0f; // Threshold at ceiling to prevent overs
    public int LimiterLookaheadMs { get; set; } = 5; // 3–5 ms lookahead per spec
    public int LimiterReleaseMs { get; set; } = 50; // Release time
    public bool SoftKneeLimiter { get; set; } = true; // Soft-knee compression
    
    // DC Removal and Offset Correction
    public bool DcRemoval { get; set; } = true;
    public float DcFilterCutoffHz { get; set; } = 5f; // High-pass for DC removal
    
    // Dithering for better quantization
    public bool EnableDithering { get; set; } = true;
    public string DitherType { get; set; } = "TriangularPdf"; // TriangularPdf, RectangularPdf, Noise
    public float DitherAmountDb { get; set; } = -96f;
    
    // Multiband Processing
    public bool MultibandProcessing { get; set; } = false; // Advanced multiband processing
    public float[] CrossoverFrequencies { get; set; } = new[] { 200f, 2000f, 8000f }; // 4-band crossover
    public float[] BandGainsDb { get; set; } = new[] { 0f, 0f, 0f, 0f }; // Per-band gains
    
    // Voice Enhancement
    public bool VoiceEnhancement { get; set; } = true;
    public bool DeEsser { get; set; } = true; // Reduce sibilance
    public float DeEsserFrequencyHz { get; set; } = 6000f; // De-esser frequency
    public float DeEsserThresholdDb { get; set; } = -20f; // De-esser threshold
    public bool VoiceClarity { get; set; } = true; // Enhance voice clarity
    
    // Real-time Monitoring and Quality Metrics
    public bool EnableQualityMetrics { get; set; } = true;
    public bool MonitorThd { get; set; } = true; // Total Harmonic Distortion monitoring
    public bool MonitorSnr { get; set; } = true; // Signal-to-Noise Ratio monitoring
    public bool MonitorLoudness { get; set; } = true; // Loudness monitoring
    
    // Post-processing
    public bool PostNormalize { get; set; } = true; // Post-recording normalization
    public bool PostNoiseReduction { get; set; } = true; // Advanced post-processing noise reduction
    public bool PostEqualizer { get; set; } = false; // Post-processing EQ

    // AEC3 advanced options (WebRTC APM or compatible)
    public bool DelayAgnostic { get; set; } = true;
    public bool ExtendedFilter { get; set; } = true;
    public bool RefinedAdaptiveFilter { get; set; } = true;
    public int InitialDelayMs { get; set; } = 45;

    // Diagnostics and validation
    public bool DiagnosticsEnableMonoDumps { get; set; } = false; // Write near_raw, near_processed, far_end WAVs
    public bool DiagnosticsTestToneCheck { get; set; } = false;   // Run test tone mapping check (1 kHz on far-end)
}
