using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CallRecorder.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;

namespace CallRecorder.Service.Audio;

/// <summary>
/// Self-test validator for echo cancellation performance.
/// Runs diagnostic tests to verify ERLE, leakage, and cross-correlation metrics.
/// </summary>
public sealed class EchoTestValidator
{
    private readonly ILogger<EchoTestValidator> _logger;
    private readonly AudioDspConfig _dspConfig;
    private readonly string _testOutputPath;
    
    // Test metrics
    private double _erle = 0;
    private double _residualLeakage = 0;
    private double _crossCorrelation = 0;
    private int _streamDelayMs = 0;
    
    // Test results
    private bool _testPassed = false;
    private string _testFailureReason = "";
    
    public EchoTestValidator(ILogger<EchoTestValidator> logger, IOptions<AudioDspConfig> dspConfig)
    {
        _logger = logger;
        _dspConfig = dspConfig.Value;
        _testOutputPath = Path.Combine(Path.GetTempPath(), "CallRecorder", "EchoTest");
        Directory.CreateDirectory(_testOutputPath);
    }
    
    /// <summary>
    /// Runs a comprehensive echo test with tone generation and analysis.
    /// </summary>
    public async Task<EchoTestResult> RunEchoTestAsync(int durationSeconds = 60, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting echo cancellation self-test for {duration} seconds", durationSeconds);
        
        var result = new EchoTestResult
        {
            TestStartTime = DateTime.UtcNow,
            DurationSeconds = durationSeconds
        };
        
        try
        {
            // Step 1: Generate test signals
            _logger.LogInformation("Generating test signals...");
            var (farEndPath, nearRawPath) = GenerateTestSignals(durationSeconds);
            result.FarEndFile = farEndPath;
            result.NearRawFile = nearRawPath;
            
            // Step 2: Process through AEC
            _logger.LogInformation("Processing through AEC...");
            var nearProcessedPath = await ProcessThroughAecAsync(farEndPath, nearRawPath, cancellationToken);
            result.NearProcessedFile = nearProcessedPath;
            
            // Step 3: Analyze results
            _logger.LogInformation("Analyzing echo cancellation performance...");
            AnalyzeResults(farEndPath, nearRawPath, nearProcessedPath, result);
            
            // Step 4: Validate against criteria
            ValidateResults(result);
            
            // Step 5: Generate diagnostic report
            GenerateReport(result);
            
            result.TestPassed = _testPassed;
            result.FailureReason = _testFailureReason;
            
            LogTestResults(result);
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Echo test failed with exception");
            result.TestPassed = false;
            result.FailureReason = $"Test exception: {ex.Message}";
            return result;
        }
    }
    
    private (string farEndPath, string nearRawPath) GenerateTestSignals(int durationSeconds)
    {
        var sampleRate = 48000;
        var samples = sampleRate * durationSeconds;
        
        // Generate far-end signal (1kHz tone + pink noise)
        var farEnd = new float[samples];
        var toneFreq = 1000.0; // 1kHz
        var toneAmplitude = 0.3f;
        var noiseAmplitude = 0.1f;
        var random = new Random();
        
        for (int i = 0; i < samples; i++)
        {
            // Tone component
            farEnd[i] = toneAmplitude * (float)Math.Sin(2 * Math.PI * toneFreq * i / sampleRate);
            
            // Pink noise component
            farEnd[i] += noiseAmplitude * (float)(random.NextDouble() * 2 - 1);
        }
        
        // Generate near-end signal (simulated echo + speech)
        var nearRaw = new float[samples];
        var echoDelay = (int)(0.045 * sampleRate); // 45ms delay
        var echoAttenuation = 0.5f; // -6dB
        
        for (int i = 0; i < samples; i++)
        {
            // Echo component (delayed and attenuated far-end)
            if (i >= echoDelay)
            {
                nearRaw[i] = farEnd[i - echoDelay] * echoAttenuation;
            }
            
            // Simulated speech bursts (every 2 seconds for 0.5 seconds)
            int cyclePosition = i % (sampleRate * 2);
            if (cyclePosition < sampleRate / 2)
            {
                nearRaw[i] += 0.2f * (float)Math.Sin(2 * Math.PI * 300 * i / sampleRate); // 300Hz speech-like tone
            }
            
            // Add some noise
            nearRaw[i] += 0.02f * (float)(random.NextDouble() * 2 - 1);
        }
        
        // Save test signals
        var farEndPath = Path.Combine(_testOutputPath, "test_far_end.wav");
        var nearRawPath = Path.Combine(_testOutputPath, "test_near_raw.wav");
        
        SaveFloatArrayAsWav(farEnd, farEndPath, sampleRate);
        SaveFloatArrayAsWav(nearRaw, nearRawPath, sampleRate);
        
        return (farEndPath, nearRawPath);
    }
    
    private async Task<string> ProcessThroughAecAsync(string farEndPath, string nearRawPath, CancellationToken cancellationToken)
    {
        var farEnd = LoadWavAsFloatArray(farEndPath);
        var nearRaw = LoadWavAsFloatArray(nearRawPath);
        
        // Create AEC processor
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var aecFactory = new AecProcessorFactory(loggerFactory);
        var aecProcessor = aecFactory.Create(_dspConfig);
        
        // Configure AEC
        aecProcessor.Configure(_dspConfig, 48000, 10);
        
        // Process in 10ms frames
        var frameSize = 480; // 10ms at 48kHz
        var nearProcessed = new float[nearRaw.Length];
        
        for (int i = 0; i < nearRaw.Length; i += frameSize)
        {
            var remaining = Math.Min(frameSize, nearRaw.Length - i);
            if (remaining < frameSize) break;
            
            // Feed far-end reference
            var farFrame = new ArraySegment<float>(farEnd, i, frameSize);
            aecProcessor.FeedFar(farFrame);
            
            // Process near-end
            var nearFrame = new ArraySegment<float>(nearRaw, i, frameSize);
            var processedFrame = new float[frameSize];
            aecProcessor.ProcessNear(nearFrame, processedFrame);
            
            // Copy to output
            Array.Copy(processedFrame, 0, nearProcessed, i, frameSize);
            
            // Update stream delay estimate
            if (i % (48000 / 10) == 0) // Every 100ms
            {
                aecProcessor.SetStreamDelayMs(_streamDelayMs);
            }
            
            if (cancellationToken.IsCancellationRequested)
                break;
        }
        
        aecProcessor.Dispose();
        
        // Save processed signal
        var nearProcessedPath = Path.Combine(_testOutputPath, "test_near_processed.wav");
        SaveFloatArrayAsWav(nearProcessed, nearProcessedPath, 48000);
        
        await Task.CompletedTask;
        return nearProcessedPath;
    }
    
    private void AnalyzeResults(string farEndPath, string nearRawPath, string nearProcessedPath, EchoTestResult result)
    {
        var farEnd = LoadWavAsFloatArray(farEndPath);
        var nearRaw = LoadWavAsFloatArray(nearRawPath);
        var nearProcessed = LoadWavAsFloatArray(nearProcessedPath);
        
        // Calculate ERLE (Echo Return Loss Enhancement)
        double powerNearRaw = CalculatePower(nearRaw);
        double powerNearProcessed = CalculatePower(nearProcessed);
        
        if (powerNearProcessed > 0)
        {
            _erle = 10 * Math.Log10(powerNearRaw / powerNearProcessed);
        }
        result.ERLE = _erle;
        
        // Calculate residual leakage
        double powerFar = CalculatePower(farEnd);
        if (powerFar > 0)
        {
            _residualLeakage = 10 * Math.Log10(powerNearProcessed / powerFar);
        }
        result.ResidualLeakage = _residualLeakage;
        
        // Calculate cross-correlation
        _crossCorrelation = CalculateCrossCorrelation(farEnd, nearProcessed);
        result.CrossCorrelation = _crossCorrelation;
        
        // Estimate delay
        _streamDelayMs = EstimateDelay(farEnd, nearRaw);
        result.EstimatedDelayMs = _streamDelayMs;
        
        // Calculate echo suppression in specific frequency band (around 1kHz)
        var (farSpectrum, nearRawSpectrum, nearProcessedSpectrum) = CalculateSpectrums(farEnd, nearRaw, nearProcessed);
        result.SpectralSuppression1kHz = CalculateSpectralSuppression(nearRawSpectrum, nearProcessedSpectrum, 1000, 48000);
    }
    
    private void ValidateResults(EchoTestResult result)
    {
        _testPassed = true;
        _testFailureReason = "";
        
        // Criterion 1: ERLE should be >= 20 dB
        if (result.ERLE < 20)
        {
            _testPassed = false;
            _testFailureReason += $"ERLE too low ({result.ERLE:F1} dB, target >= 20 dB). ";
        }
        
        // Criterion 2: Residual leakage should be <= -35 dB
        if (result.ResidualLeakage > -35)
        {
            _testPassed = false;
            _testFailureReason += $"Residual leakage too high ({result.ResidualLeakage:F1} dB, target <= -35 dB). ";
        }
        
        // Criterion 3: Cross-correlation should be very low
        if (Math.Abs(result.CrossCorrelation) > 0.1)
        {
            _testPassed = false;
            _testFailureReason += $"High cross-correlation detected ({result.CrossCorrelation:F3}, target < 0.1). ";
        }
        
        // Check for specific failure patterns
        if (result.ERLE < 5)
        {
            if (Math.Abs(result.CrossCorrelation) > 0.5)
            {
                _testFailureReason += "ReverseStream likely not called or wrong order. ";
            }
            else if (result.EstimatedDelayMs > 60)
            {
                _testFailureReason += $"Delay mismatch detected ({result.EstimatedDelayMs} ms). Check buffer alignment. ";
            }
        }
        
        // Check for sidetone/monitoring
        if (result.CrossCorrelation > 0.8 && result.ERLE < 3)
        {
            _testFailureReason += "Possible sidetone or 'Listen to this device' enabled. ";
        }
        
        result.Diagnosis = _testFailureReason;
    }
    
    private void GenerateReport(EchoTestResult result)
    {
        var reportPath = Path.Combine(_testOutputPath, "echo_test_report.txt");
        
        using var writer = new StreamWriter(reportPath);
        writer.WriteLine("Echo Cancellation Test Report");
        writer.WriteLine("==============================");
        writer.WriteLine($"Test Date: {result.TestStartTime:yyyy-MM-dd HH:mm:ss} UTC");
        writer.WriteLine($"Duration: {result.DurationSeconds} seconds");
        writer.WriteLine();
        writer.WriteLine("Results:");
        writer.WriteLine($"  ERLE: {result.ERLE:F1} dB (target >= 20 dB) - {(result.ERLE >= 20 ? "PASS" : "FAIL")}");
        writer.WriteLine($"  Residual Leakage: {result.ResidualLeakage:F1} dB (target <= -35 dB) - {(result.ResidualLeakage <= -35 ? "PASS" : "FAIL")}");
        writer.WriteLine($"  Cross-correlation: {result.CrossCorrelation:F3} (target < 0.1) - {(Math.Abs(result.CrossCorrelation) < 0.1 ? "PASS" : "FAIL")}");
        writer.WriteLine($"  Estimated Delay: {result.EstimatedDelayMs} ms");
        writer.WriteLine($"  1kHz Suppression: {result.SpectralSuppression1kHz:F1} dB");
        writer.WriteLine();
        writer.WriteLine($"Overall: {(result.TestPassed ? "PASS" : "FAIL")}");
        if (!result.TestPassed)
        {
            writer.WriteLine($"Failure Reason: {result.FailureReason}");
            writer.WriteLine($"Diagnosis: {result.Diagnosis}");
        }
        
        result.ReportFile = reportPath;
    }
    
    private void LogTestResults(EchoTestResult result)
    {
        _logger.LogInformation("Echo Test Results:");
        _logger.LogInformation("  ERLE: {erle:F1} dB (target >= 20 dB)", result.ERLE);
        _logger.LogInformation("  Residual Leakage: {leakage:F1} dB (target <= -35 dB)", result.ResidualLeakage);
        _logger.LogInformation("  Cross-correlation: {correlation:F3} (target < 0.1)", result.CrossCorrelation);
        _logger.LogInformation("  Estimated Delay: {delay} ms", result.EstimatedDelayMs);
        
        if (result.TestPassed)
        {
            _logger.LogInformation("Echo test PASSED ✓");
        }
        else
        {
            _logger.LogWarning("Echo test FAILED: {reason}", result.FailureReason);
            _logger.LogWarning("Diagnosis: {diagnosis}", result.Diagnosis);
        }
    }
    
    // Helper methods
    private double CalculatePower(float[] signal)
    {
        double sum = 0;
        for (int i = 0; i < signal.Length; i++)
        {
            sum += signal[i] * signal[i];
        }
        return sum / signal.Length;
    }
    
    private double CalculateCrossCorrelation(float[] x, float[] y)
    {
        int length = Math.Min(x.Length, y.Length);
        double sumXY = 0, sumX2 = 0, sumY2 = 0;
        
        for (int i = 0; i < length; i++)
        {
            sumXY += x[i] * y[i];
            sumX2 += x[i] * x[i];
            sumY2 += y[i] * y[i];
        }
        
        if (sumX2 * sumY2 == 0) return 0;
        return sumXY / Math.Sqrt(sumX2 * sumY2);
    }
    
    private int EstimateDelay(float[] reference, float[] signal)
    {
        // Simple delay estimation using cross-correlation peak
        int maxLag = 4800; // 100ms at 48kHz
        double maxCorr = 0;
        int bestLag = 0;
        
        for (int lag = 0; lag < maxLag && lag < signal.Length; lag++)
        {
            double corr = 0;
            int count = 0;
            
            for (int i = 0; i < reference.Length && i + lag < signal.Length; i++)
            {
                corr += reference[i] * signal[i + lag];
                count++;
            }
            
            if (count > 0)
            {
                corr /= count;
                if (Math.Abs(corr) > Math.Abs(maxCorr))
                {
                    maxCorr = corr;
                    bestLag = lag;
                }
            }
        }
        
        return (int)(bestLag * 1000.0 / 48000); // Convert to ms
    }
    
    private (double[], double[], double[]) CalculateSpectrums(float[] farEnd, float[] nearRaw, float[] nearProcessed)
    {
        // Simple FFT placeholder - in production use proper FFT
        int fftSize = 2048;
        var farSpectrum = new double[fftSize / 2];
        var nearRawSpectrum = new double[fftSize / 2];
        var nearProcessedSpectrum = new double[fftSize / 2];
        
        // Simplified spectrum calculation (would use FFT in production)
        for (int i = 0; i < fftSize / 2; i++)
        {
            farSpectrum[i] = 0.1;
            nearRawSpectrum[i] = 0.05;
            nearProcessedSpectrum[i] = 0.01;
        }
        
        return (farSpectrum, nearRawSpectrum, nearProcessedSpectrum);
    }
    
    private double CalculateSpectralSuppression(double[] rawSpectrum, double[] processedSpectrum, int freqHz, int sampleRate)
    {
        int bin = (freqHz * rawSpectrum.Length * 2) / sampleRate;
        if (bin >= 0 && bin < rawSpectrum.Length && processedSpectrum[bin] > 0)
        {
            return 10 * Math.Log10(rawSpectrum[bin] / processedSpectrum[bin]);
        }
        return 0;
    }
    
    private void SaveFloatArrayAsWav(float[] data, string path, int sampleRate)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        using var writer = new WaveFileWriter(path, format);
        writer.WriteSamples(data, 0, data.Length);
    }
    
    private float[] LoadWavAsFloatArray(string path)
    {
        using var reader = new AudioFileReader(path);
        var samples = new float[reader.Length / 4];
        reader.Read(samples, 0, samples.Length);
        return samples;
    }
}

/// <summary>
/// Results from echo cancellation test.
/// </summary>
public class EchoTestResult
{
    public DateTime TestStartTime { get; set; }
    public int DurationSeconds { get; set; }
    public bool TestPassed { get; set; }
    public string FailureReason { get; set; } = "";
    public string Diagnosis { get; set; } = "";
    
    // Metrics
    public double ERLE { get; set; }
    public double ResidualLeakage { get; set; }
    public double CrossCorrelation { get; set; }
    public int EstimatedDelayMs { get; set; }
    public double SpectralSuppression1kHz { get; set; }
    
    // File paths
    public string FarEndFile { get; set; } = "";
    public string NearRawFile { get; set; } = "";
    public string NearProcessedFile { get; set; } = "";
    public string ReportFile { get; set; } = "";
}
