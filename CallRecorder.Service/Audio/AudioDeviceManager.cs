using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CallRecorder.Core.Config;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CallRecorder.Service.Audio;

/// <summary>
/// Advanced audio device manager with quality assessment, pre-warming, and optimal device selection
/// </summary>
public sealed class AudioDeviceManager : IDisposable
{
    private readonly ILogger<AudioDeviceManager> _logger;
    private readonly AudioDeviceConfig _config;
    private readonly RecordingConfig _recordingConfig;
    private readonly MMDeviceEnumerator _enumerator;
    
    // Pre-warmed devices cache
    private readonly Dictionary<string, DeviceInfo> _deviceCache = new();
    private readonly object _cacheLock = new();
    
    public AudioDeviceManager(ILogger<AudioDeviceManager> logger, AudioDeviceConfig config, RecordingConfig recordingConfig)
    {
        _logger = logger;
        _config = config;
        _recordingConfig = recordingConfig;
        _enumerator = new MMDeviceEnumerator();
    }
    
    /// <summary>
    /// Pre-warm and assess all available audio devices
    /// </summary>
    public async Task PreWarmDevicesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting device pre-warming and assessment...");
        
        var captureDevices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        var renderDevices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        
        var allDevices = captureDevices.Concat(renderDevices).ToList();
        var tasks = new List<Task>();
        
        foreach (var device in allDevices)
        {
            if (cancellationToken.IsCancellationRequested) break;
            
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var deviceInfo = await AssessDeviceQualityAsync(device, cancellationToken);
                    lock (_cacheLock)
                    {
                        _deviceCache[device.ID] = deviceInfo;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to assess device: {deviceName} [{deviceId}]", device.FriendlyName, device.ID);
                }
            }, cancellationToken));
        }
        
        await Task.WhenAll(tasks);
        _logger.LogInformation("Device pre-warming completed. {count} devices assessed.", _deviceCache.Count);
    }
    
    /// <summary>
    /// Select the best microphone device based on quality assessment
    /// </summary>
    public DeviceSelectionResult SelectBestMicrophone()
    {
        var captureDevices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        
        if (_config.LogDeviceEnumeration)
        {
            _logger.LogInformation("Available capture devices:");
            foreach (var device in captureDevices)
            {
                _logger.LogInformation("  {name} [{id}] - State: {state}", device.FriendlyName, device.ID, device.State);
            }
        }
        
        // Check for specific device ID first
        if (!string.IsNullOrWhiteSpace(_config.MicDeviceId))
        {
            try
            {
                var specificDevice = _enumerator.GetDevice(_config.MicDeviceId);
                var info = GetOrAssessDevice(specificDevice);
                return new DeviceSelectionResult(specificDevice, info, "Specific device ID");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get specific microphone device: {deviceId}", _config.MicDeviceId);
            }
        }
        

        // Score and rank devices
        var scoredDevices = new List<(MMDevice Device, DeviceInfo Info, float Score, string Reason)>();
        
        foreach (var device in captureDevices)
        {
            try
            {
                var info = GetOrAssessDevice(device);
                var score = CalculateDeviceScore(device, info, true);
                var reason = GetScoreReason(device, info, true);
                scoredDevices.Add((device, info, score, reason));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to score device: {deviceName}", device.FriendlyName);
            }
        }
        
        // Sort by score (highest first)
        scoredDevices.Sort((a, b) => b.Score.CompareTo(a.Score));
        
        if (_config.LogQualityScores)
        {
            _logger.LogInformation("Microphone device scores:");
            foreach (var (device, info, score, reason) in scoredDevices.Take(5))
            {
                _logger.LogInformation("  {score:F2} - {name} - {reason}", score, device.FriendlyName, reason);
            }
        }
        
        // Prefer devices that pass inclusion rules
        var eligible = scoredDevices.Where(sd => sd.Info.PassesInclusionRules).ToList();

        // If configured, prefer the default Communications capture endpoint explicitly
        if (_config.PreferCommunicationsEndpoints)
        {
            try
            {
                var commsMic = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                if (commsMic != null)
                {
                    var commsInfo = GetOrAssessDevice(commsMic);
                    if (commsInfo.PassesInclusionRules)
                    {
                        _logger.LogInformation("Selecting default Communications capture endpoint: {name}", commsMic.FriendlyName);
                        return new DeviceSelectionResult(commsMic, commsInfo, "Default communications endpoint");
                    }
                    else
                    {
                        _logger.LogWarning("Default Communications capture endpoint fails inclusion rules: {name}", commsMic.FriendlyName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get default Communications capture endpoint");
            }
        }

        // Select best eligible device by score
        var best = eligible.FirstOrDefault();
        if (best.Device != null)
        {
            if (_config.WarnOnLowQualityDevice && best.Score < 50)
            {
                _logger.LogWarning("Selected microphone has low quality score: {score:F2} - {name}", best.Score, best.Device.FriendlyName);
            }

            return new DeviceSelectionResult(best.Device, best.Info, best.Reason);
        }

        // Fallback: communications endpoint (even if it didn't pass include) to avoid "Stereo Mix"/Line In
        if (_config.AllowFallbackToAnyDevice)
        {
            try
            {
                var commsMic = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                if (commsMic != null)
                {
                    var commsInfo = GetOrAssessDevice(commsMic);
                    _logger.LogWarning("Falling back to default Communications capture endpoint: {name}", commsMic.FriendlyName);
                    return new DeviceSelectionResult(commsMic, commsInfo, "Fallback: default communications endpoint");
                }
            }
            catch { /* ignore */ }

            if (captureDevices.Any())
            {
                var fallback = captureDevices.First();
                var info = GetOrAssessDevice(fallback);
                _logger.LogWarning("Using fallback microphone device: {name}", fallback.FriendlyName);
                return new DeviceSelectionResult(fallback, info, "Fallback device");
            }
        }
        
        throw new InvalidOperationException("No suitable microphone device found");
    }
    
    /// <summary>
    /// Select the best speaker/output device based on quality assessment
    /// </summary>
    public DeviceSelectionResult SelectBestSpeaker()
    {
        var renderDevices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        
        if (_config.LogDeviceEnumeration)
        {
            _logger.LogInformation("Available render devices:");
            foreach (var device in renderDevices)
            {
                _logger.LogInformation("  {name} [{id}] - State: {state}", device.FriendlyName, device.ID, device.State);
            }
        }
        
        // Check for specific device ID first
        if (!string.IsNullOrWhiteSpace(_config.SpeakerDeviceId))
        {
            try
            {
                var specificDevice = _enumerator.GetDevice(_config.SpeakerDeviceId);
                var info = GetOrAssessDevice(specificDevice);
                return new DeviceSelectionResult(specificDevice, info, "Specific device ID");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get specific speaker device: {deviceId}", _config.SpeakerDeviceId);
            }
        }
        
        // Score and rank devices
        var scoredDevices = new List<(MMDevice Device, DeviceInfo Info, float Score, string Reason)>();
        
        foreach (var device in renderDevices)
        {
            try
            {
                var info = GetOrAssessDevice(device);
                var score = CalculateDeviceScore(device, info, false);
                var reason = GetScoreReason(device, info, false);
                scoredDevices.Add((device, info, score, reason));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to score device: {deviceName}", device.FriendlyName);
            }
        }
        
        // Sort by score (highest first)
        scoredDevices.Sort((a, b) => b.Score.CompareTo(a.Score));
        
        if (_config.LogQualityScores)
        {
            _logger.LogInformation("Speaker device scores:");
            foreach (var (device, info, score, reason) in scoredDevices.Take(5))
            {
                _logger.LogInformation("  {score:F2} - {name} - {reason}", score, device.FriendlyName, reason);
            }
        }
        
        // Select best device
        var best = scoredDevices.FirstOrDefault();
        if (best.Device != null)
        {
            if (_config.WarnOnLowQualityDevice && best.Score < 50)
            {
                _logger.LogWarning("Selected speaker has low quality score: {score:F2} - {name}", best.Score, best.Device.FriendlyName);
            }
            
            return new DeviceSelectionResult(best.Device, best.Info, best.Reason);
        }
        
        // Fallback
        if (_config.AllowFallbackToAnyDevice && renderDevices.Any())
        {
            var fallback = renderDevices.First();
            var info = GetOrAssessDevice(fallback);
            _logger.LogWarning("Using fallback speaker device: {name}", fallback.FriendlyName);
            return new DeviceSelectionResult(fallback, info, "Fallback device");
        }
        
        throw new InvalidOperationException("No suitable speaker device found");
    }
    
    private DeviceInfo GetOrAssessDevice(MMDevice device)
    {
        lock (_cacheLock)
        {
            if (_deviceCache.TryGetValue(device.ID, out var cached))
                return cached;
        }
        
        // Assess device synchronously (should be fast if pre-warming was done)
        var info = AssessDeviceQualityAsync(device, CancellationToken.None).Result;
        
        lock (_cacheLock)
        {
            _deviceCache[device.ID] = info;
        }
        
        return info;
    }
    
    private async Task<DeviceInfo> AssessDeviceQualityAsync(MMDevice device, CancellationToken cancellationToken)
    {
        var info = new DeviceInfo
        {
            DeviceId = device.ID,
            FriendlyName = device.FriendlyName,
            IsActive = device.State == DeviceState.Active,
            DataFlow = device.DataFlow
        };
        
        try
        {
            // Get device properties (simplified for compatibility)
            info.Description = device.FriendlyName;
            info.Manufacturer = "Unknown"; // Simplified for now
            
            // Check if it's a preferred manufacturer
            info.IsPreferredManufacturer = _config.PreferredManufacturers.Any(pm => 
                info.Manufacturer?.Contains(pm, StringComparison.OrdinalIgnoreCase) == true ||
                info.FriendlyName?.Contains(pm, StringComparison.OrdinalIgnoreCase) == true);
            
            // Get audio format information
            if (device.DataFlow == DataFlow.Capture)
            {
                using var capture = new WasapiCapture(device);
                info.NativeFormat = capture.WaveFormat;
                info.SupportsExclusiveMode = await TestExclusiveModeAsync(device, cancellationToken);
            }
            else
            {
                using var loopback = new WasapiLoopbackCapture(device);
                info.NativeFormat = loopback.WaveFormat;
                info.SupportsExclusiveMode = await TestExclusiveModeAsync(device, cancellationToken);
            }
            
            // Test device if enabled
            if (_config.TestDevicesBeforeSelection && !cancellationToken.IsCancellationRequested)
            {
                info.TestResults = await PerformDeviceTestAsync(device, cancellationToken);
            }
            
            // Check inclusion/exclusion rules
            info.PassesInclusionRules = device.DataFlow == DataFlow.Capture 
                ? IsIncluded(info.FriendlyName, _config.MicInclude, _config.MicExclude)
                : IsIncluded(info.FriendlyName, _config.SpeakerInclude, _config.SpeakerExclude);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error assessing device: {deviceName}", device.FriendlyName);
            info.HasError = true;
            info.ErrorMessage = ex.Message;
        }
        
        return info;
    }
    
    private async Task<bool> TestExclusiveModeAsync(MMDevice device, CancellationToken cancellationToken)
    {
        try
        {
            // Quick test for exclusive mode support
            if (device.DataFlow == DataFlow.Capture)
            {
                using var capture = new WasapiCapture(device);
                capture.ShareMode = AudioClientShareMode.Exclusive;
                // Just initialize, don't actually start recording
                return true;
            }
            else
            {
                // For render devices, we can't easily test exclusive mode with loopback
                return false;
            }
        }
        catch
        {
            return false;
        }
    }
    
    private async Task<DeviceTestResults> PerformDeviceTestAsync(MMDevice device, CancellationToken cancellationToken)
    {
        var results = new DeviceTestResults();
        
        try
        {
            if (device.DataFlow == DataFlow.Capture)
            {
                await TestCaptureDeviceAsync(device, results, cancellationToken);
            }
            else
            {
                await TestRenderDeviceAsync(device, results, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            results.HasError = true;
            results.ErrorMessage = ex.Message;
            _logger.LogDebug(ex, "Device test failed: {deviceName}", device.FriendlyName);
        }
        
        return results;
    }
    
    private async Task TestCaptureDeviceAsync(MMDevice device, DeviceTestResults results, CancellationToken cancellationToken)
    {
        using var capture = new WasapiCapture(device);
        capture.ShareMode = AudioClientShareMode.Shared;
        
        var samples = new List<float>();
        var startTime = DateTime.UtcNow;
        
        capture.DataAvailable += (s, e) =>
        {
            if (e.BytesRecorded > 0 && capture.WaveFormat.BitsPerSample == 16)
            {
                for (int i = 0; i < e.BytesRecorded; i += 2)
                {
                    short sample = BitConverter.ToInt16(e.Buffer, i);
                    samples.Add(sample / 32768f);
                }
            }
        };
        
        capture.StartRecording();
        await Task.Delay(_config.TestDurationMs, cancellationToken);
        capture.StopRecording();
        
        results.TestDurationMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
        results.SamplesRecorded = samples.Count;
        
        if (samples.Count > 0)
        {
            // Calculate basic audio metrics
            float sum = 0, sumSquared = 0, peak = 0;
            foreach (var sample in samples)
            {
                var abs = Math.Abs(sample);
                sum += abs;
                sumSquared += sample * sample;
                peak = Math.Max(peak, abs);
            }
            
            results.AverageLevel = sum / samples.Count;
            results.RmsLevel = (float)Math.Sqrt(sumSquared / samples.Count);
            results.PeakLevel = peak;
            
            // Estimate SNR (very basic)
            if (results.RmsLevel > 0)
            {
                var quietSamples = samples.Where(s => Math.Abs(s) < results.RmsLevel * 0.1f).ToList();
                if (quietSamples.Count > 0)
                {
                    var noiseRms = (float)Math.Sqrt(quietSamples.Sum(s => s * s) / quietSamples.Count);
                    results.EstimatedSnrDb = 20f * (float)Math.Log10(Math.Max(results.RmsLevel / Math.Max(noiseRms, 1e-6f), 1e-6f));
                }
            }
        }
        
        results.IsSuccess = samples.Count > 0;
    }
    
    private async Task TestRenderDeviceAsync(MMDevice device, DeviceTestResults results, CancellationToken cancellationToken)
    {
        // For render devices, we test via loopback capture
        using var loopback = new WasapiLoopbackCapture(device);
        
        var samples = new List<float>();
        var startTime = DateTime.UtcNow;
        
        loopback.DataAvailable += (s, e) =>
        {
            if (e.BytesRecorded > 0)
            {
                // Convert to float samples based on format
                var format = loopback.WaveFormat;
                if (format.BitsPerSample == 16)
                {
                    for (int i = 0; i < e.BytesRecorded; i += 2)
                    {
                        short sample = BitConverter.ToInt16(e.Buffer, i);
                        samples.Add(sample / 32768f);
                    }
                }
                else if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    for (int i = 0; i < e.BytesRecorded; i += 4)
                    {
                        float sample = BitConverter.ToSingle(e.Buffer, i);
                        samples.Add(sample);
                    }
                }
            }
        };
        
        loopback.StartRecording();
        await Task.Delay(_config.TestDurationMs, cancellationToken);
        loopback.StopRecording();
        
        results.TestDurationMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
        results.SamplesRecorded = samples.Count;
        results.IsSuccess = true; // Render devices are generally considered successful if they don't throw
    }
    
    private float CalculateDeviceScore(MMDevice device, DeviceInfo info, bool isCapture)
    {
        float score = 0;
        
        // Base score for working device
        if (info.IsActive && !info.HasError)
            score += 20;
        
        // Inclusion/exclusion rules
        if (!info.PassesInclusionRules)
            return 0; // Immediate disqualification
        
        // Preferred manufacturer bonus
        if (info.IsPreferredManufacturer)
            score += 25;
        
        // Audio format quality
        if (info.NativeFormat != null)
        {
            // Sample rate scoring
            if (info.NativeFormat.SampleRate >= 48000)
                score += 15;
            else if (info.NativeFormat.SampleRate >= 44100)
                score += 10;
            else if (info.NativeFormat.SampleRate >= _config.MinSampleRateHz)
                score += 5;
            
            // Bit depth scoring
            if (info.NativeFormat.BitsPerSample >= 24)
                score += 10;
            else if (info.NativeFormat.BitsPerSample >= _config.MinBitsPerSample)
                score += 5;
        }
        
        // Exclusive mode support
        if (info.SupportsExclusiveMode && _config.PreferLowLatencyDevices)
            score += 10;
        
        // Test results
        if (info.TestResults != null && info.TestResults.IsSuccess)
        {
            score += 10;
            
            // SNR bonus for capture devices
            if (isCapture && info.TestResults.EstimatedSnrDb >= _config.MinSignalToNoiseRatio)
                score += 15;
        }
        
        // Role preference (avoid communications devices unless preferred)
        if (_config.PreferCommunicationsEndpoints)
        {
            // Prefer communications devices
            if (info.FriendlyName?.Contains("Communication", StringComparison.OrdinalIgnoreCase) == true)
                score += 5;
        }
        else
        {
            // Avoid communications devices
            if (info.FriendlyName?.Contains("Communication", StringComparison.OrdinalIgnoreCase) == true)
                score -= 10;
        }
        
        return Math.Max(0, score);
    }
    
    private string GetScoreReason(MMDevice device, DeviceInfo info, bool isCapture)
    {
        var reasons = new List<string>();
        
        if (info.IsPreferredManufacturer)
            reasons.Add("Preferred manufacturer");
        
        if (info.NativeFormat?.SampleRate >= 48000)
            reasons.Add("High sample rate");
        
        if (info.SupportsExclusiveMode)
            reasons.Add("Exclusive mode");
        
        if (info.TestResults?.IsSuccess == true && isCapture && info.TestResults.EstimatedSnrDb >= _config.MinSignalToNoiseRatio)
            reasons.Add($"Good SNR ({info.TestResults.EstimatedSnrDb:F1}dB)");
        
        if (!info.PassesInclusionRules)
            reasons.Add("Fails inclusion rules");
        
        return reasons.Any() ? string.Join(", ", reasons) : "Default scoring";
    }
    
    private static bool IsIncluded(string? name, string[]? include, string[]? exclude)
    {
        var n = name ?? string.Empty;
        if (exclude != null && exclude.Any(ex => n.Contains(ex, StringComparison.OrdinalIgnoreCase))) 
            return false;
        if (include == null || include.Length == 0) 
            return true;
        return include.Any(inc => n.Contains(inc, StringComparison.OrdinalIgnoreCase));
    }
    
    public void Dispose()
    {
        _enumerator?.Dispose();
    }
}

/// <summary>
/// Device information and assessment results
/// </summary>
public class DeviceInfo
{
    public string DeviceId { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public bool IsActive { get; set; }
    public bool HasError { get; set; }
    public string ErrorMessage { get; set; } = "";
    public DataFlow DataFlow { get; set; }
    public WaveFormat? NativeFormat { get; set; }
    public bool SupportsExclusiveMode { get; set; }
    public bool IsPreferredManufacturer { get; set; }
    public bool PassesInclusionRules { get; set; }
    public DeviceTestResults? TestResults { get; set; }
}

/// <summary>
/// Device test results
/// </summary>
public class DeviceTestResults
{
    public bool IsSuccess { get; set; }
    public bool HasError { get; set; }
    public string ErrorMessage { get; set; } = "";
    public int TestDurationMs { get; set; }
    public int SamplesRecorded { get; set; }
    public float AverageLevel { get; set; }
    public float RmsLevel { get; set; }
    public float PeakLevel { get; set; }
    public float EstimatedSnrDb { get; set; }
}

/// <summary>
/// Device selection result
/// </summary>
public class DeviceSelectionResult
{
    public MMDevice Device { get; }
    public DeviceInfo Info { get; }
    public string SelectionReason { get; }
    
    public DeviceSelectionResult(MMDevice device, DeviceInfo info, string reason)
    {
        Device = device;
        Info = info;
        SelectionReason = reason;
    }
}
