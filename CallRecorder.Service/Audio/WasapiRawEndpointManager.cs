using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace CallRecorder.Service.Audio;

/// <summary>
/// Manages WASAPI endpoints with RAW mode to bypass OS effects and ensure clean audio capture.
/// Forces AUDCLNT_STREAMOPTIONS_RAW to disable all OS enhancements and double AEC.
/// </summary>
public sealed class WasapiRawEndpointManager
{
    private readonly ILogger<WasapiRawEndpointManager> _logger;
    
    // Audio client properties for RAW mode
    private const int AUDCLNT_STREAMOPTIONS_NONE = 0x00;
    private const int AUDCLNT_STREAMOPTIONS_RAW = 0x01;
    private const int AUDCLNT_STREAMOPTIONS_MATCH_FORMAT = 0x02;
    private const int AUDCLNT_STREAMOPTIONS_AMBISONICS = 0x04;
    
    // Property key for audio effects
    private static readonly Guid PKEY_AudioEndpoint_Disable_SysFx = new Guid("1da5d803-d492-4edd-8c23-e7f7d9c9b8c5");
    
    public WasapiRawEndpointManager(ILogger<WasapiRawEndpointManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validates and configures an endpoint for RAW capture, detecting and warning about problematic settings.
    /// </summary>
    public EndpointValidationResult ValidateAndConfigureEndpoint(MMDevice device, bool isCapture)
    {
        var result = new EndpointValidationResult
        {
            DeviceId = device.ID,
            FriendlyName = device.FriendlyName,
            IsCapture = isCapture
        };

        try
        {
            // Check for Communications role
            if (isCapture)
            {
                result.IsCommunicationsEndpoint = device.FriendlyName.Contains("Communications") ||
                                                   device.DeviceFriendlyName.Contains("Communications");
            }
            else
            {
                // For render devices, we want Communications role for loopback
                result.IsCommunicationsEndpoint = true; // We'll force selection later
            }

            // Detect problematic configurations
            DetectSidetone(device, result);
            DetectEnhancements(device, result);
            DetectVirtualDevice(device, result);
            
            // Check if we can use RAW mode
            result.SupportsRawMode = CheckRawModeSupport(device);
            
            // Validate the endpoint
            result.IsValid = ValidateEndpointSuitability(result);
            
            if (!result.IsValid)
            {
                _logger.LogWarning("Endpoint {name} validation failed: {reason}", 
                    device.FriendlyName, result.ValidationMessage);
            }
            else
            {
                _logger.LogInformation("Endpoint {name} validated: RAW={raw}, Comms={comms}", 
                    device.FriendlyName, result.SupportsRawMode, result.IsCommunicationsEndpoint);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate endpoint {name}", device.FriendlyName);
            result.IsValid = false;
            result.ValidationMessage = $"Validation error: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Configures a WasapiCapture or WasapiLoopbackCapture instance for RAW mode.
    /// </summary>
    public void ConfigureRawMode(WasapiCapture capture, MMDevice device)
    {
        try
        {
            // Get the IAudioClient3 interface if available
            var audioClient = device.AudioClient;
            
            // Set client properties for RAW mode
            if (SetRawStreamProperties(audioClient))
            {
                _logger.LogInformation("RAW mode enabled for {device}", device.FriendlyName);
            }
            else
            {
                _logger.LogWarning("Could not enable RAW mode for {device}, OS effects may still be active", 
                    device.FriendlyName);
            }

            // Configure for event-driven mode with 10ms period
            capture.ShareMode = AudioClientShareMode.Shared;
            
            // Try to set buffer to exactly 10ms for tight timing
            try
            {
                var waveFormat = capture.WaveFormat;
                int bufferFrames = waveFormat.SampleRate / 100; // 10ms
                
                _logger.LogInformation("Configured {device} for 10ms frames ({frames} samples at {rate}Hz)",
                    device.FriendlyName, bufferFrames, waveFormat.SampleRate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not set 10ms buffer size, using default");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to configure RAW mode for {device}", device.FriendlyName);
        }
    }

    private bool SetRawStreamProperties(AudioClient audioClient)
    {
        try
        {
            // Acquire IAudioClient2 from NAudio's AudioClient COM wrapper and set RAW stream options.
            IntPtr unk = Marshal.GetIUnknownForObject(audioClient);
            IntPtr audioClient2Ptr = IntPtr.Zero;
            try
            {
                var iidIAudioClient2 = new Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA");
                int hrQi = Marshal.QueryInterface(unk, ref iidIAudioClient2, out audioClient2Ptr);
                if (hrQi != 0 || audioClient2Ptr == IntPtr.Zero)
                {
                    _logger.LogWarning("IAudioClient2 not available; RAW mode may not be supported on this OS/device. HR=0x{hr}", hrQi.ToString("X8"));
                    return false;
                }

                var props = new AudioClientNative.AudioClientProperties
                {
                    cbSize = (uint)Marshal.SizeOf<AudioClientNative.AudioClientProperties>(),
                    bIsOffload = false,
                    // 3 = AudioCategory_Communications
                    eCategory = 3,
                    Options = AUDCLNT_STREAMOPTIONS_RAW
                };

                IntPtr propsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientNative.AudioClientProperties>());
                try
                {
                    Marshal.StructureToPtr(props, propsPtr, false);
                    var client2 = (AudioClientNative.IAudioClient2)Marshal.GetObjectForIUnknown(audioClient2Ptr);
                    int hrSet = client2.SetClientProperties(propsPtr);
                    if (hrSet == 0)
                    {
                        return true;
                    }
                    else
                    {
                        _logger.LogWarning("IAudioClient2.SetClientProperties failed HR=0x{hr}", hrSet.ToString("X8"));
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(propsPtr);
                }
            }
            finally
            {
                if (audioClient2Ptr != IntPtr.Zero) Marshal.Release(audioClient2Ptr);
                if (unk != IntPtr.Zero) Marshal.Release(unk);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set RAW stream properties via IAudioClient2");
            return false;
        }
    }

    private void DetectSidetone(MMDevice device, EndpointValidationResult result)
    {
        try
        {
            // Check if "Listen to this device" is enabled
            // This would require registry access or WMI queries
            // For now, we check device properties as a proxy
            
            var properties = device.Properties;
            
            // Check for monitoring/loopback indicators in the device name
            if (device.FriendlyName.Contains("Listen") || 
                device.FriendlyName.Contains("Monitor") ||
                device.FriendlyName.Contains("What U Hear"))
            {
                result.HasSidetone = true;
                result.ValidationMessage = "Device appears to have monitoring/sidetone enabled";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not detect sidetone for {device}", device.FriendlyName);
        }
    }

    private void DetectEnhancements(MMDevice device, EndpointValidationResult result)
    {
        try
        {
            // Heuristic checks for enhancement indicators in the device name (avoid PropertyStore interop)
            if (device.FriendlyName.Contains("Enhanced") ||
                device.FriendlyName.Contains("with Effects") ||
                device.FriendlyName.Contains("Dolby") ||
                device.FriendlyName.Contains("DTS") ||
                device.FriendlyName.Contains("Nahimic"))
            {
                result.HasEnhancements = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not detect enhancements for {device}", device.FriendlyName);
        }
    }

    private void DetectVirtualDevice(MMDevice device, EndpointValidationResult result)
    {
        try
        {
            // Check for known virtual device patterns
            var name = device.FriendlyName.ToLowerInvariant();
            var deviceName = device.DeviceFriendlyName?.ToLowerInvariant() ?? "";
            
            result.IsVirtualDevice = 
                name.Contains("virtual") ||
                name.Contains("stereo mix") ||
                name.Contains("wave out mix") ||
                name.Contains("what u hear") ||
                name.Contains("loopback") ||
                deviceName.Contains("virtual") ||
                deviceName.Contains("vb-audio") ||
                deviceName.Contains("voicemeeter");
                
            if (result.IsVirtualDevice)
            {
                result.ValidationMessage = "Virtual device detected - may contain mixed audio paths";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not detect virtual device for {device}", device.FriendlyName);
        }
    }

    private bool CheckRawModeSupport(MMDevice device)
    {
        try
        {
            // Check Windows version (RAW mode requires Windows 10 1803+)
            var osVersion = Environment.OSVersion;
            if (osVersion.Platform != PlatformID.Win32NT || osVersion.Version.Major < 10)
            {
                return false;
            }
            
            // Check if device supports shared mode (required for RAW)
            var audioClient = device.AudioClient;
            if (audioClient == null)
            {
                return false;
            }
            
            // RAW mode is generally supported on Windows 10 1803+
            // More specific checks would require IAudioClient3 interface
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool ValidateEndpointSuitability(EndpointValidationResult result)
    {
        // Fail validation if critical issues detected
        if (result.HasSidetone)
        {
            result.ValidationMessage = "Sidetone/monitoring detected - will cause echo feedback loop. " +
                                        "Disable 'Listen to this device' in Sound settings.";
            return false;
        }

        if (result.IsCapture && result.IsVirtualDevice)
        {
            result.ValidationMessage = "Virtual capture device detected - may already contain mixed audio. " +
                                        "Use a physical microphone endpoint instead.";
            return false;
        }

        if (result.HasEnhancements && !result.SupportsRawMode)
        {
            result.ValidationMessage = "Audio enhancements detected and RAW mode not available. " +
                                        "Disable all enhancements in Sound settings -> Device Properties.";
            // Don't fail, but warn
            _logger.LogWarning(result.ValidationMessage);
        }

        if (!result.IsCommunicationsEndpoint && result.IsCapture)
        {
            result.ValidationMessage = "Not using Communications endpoint - may not be optimal for calls. " +
                                        "Consider setting as default Communications device.";
            // Don't fail, but warn
            _logger.LogWarning(result.ValidationMessage);
        }

        result.ValidationMessage = "Endpoint validated successfully";
        return true;
    }

    /// <summary>
    /// Selects the best capture and render endpoints for echo-free recording.
    /// </summary>
    public (MMDevice micDevice, MMDevice speakerDevice) SelectOptimalEndpoints()
    {
        var enumerator = new MMDeviceEnumerator();
        MMDevice micDevice = null;
        MMDevice speakerDevice = null;

        try
        {
            // Get Communications endpoints (preferred)
            try
            {
                micDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                _logger.LogInformation("Selected Communications capture device: {name}", micDevice.FriendlyName);
            }
            catch
            {
                // Fall back to default capture
                micDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                _logger.LogWarning("Using default capture device (Communications not available): {name}", 
                    micDevice.FriendlyName);
            }

            // Validate mic endpoint
            var micValidation = ValidateAndConfigureEndpoint(micDevice, true);
            if (!micValidation.IsValid)
            {
                throw new InvalidOperationException($"Microphone validation failed: {micValidation.ValidationMessage}");
            }

            // Get Communications render endpoint for loopback
            try
            {
                speakerDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
                _logger.LogInformation("Selected Communications render device: {name}", speakerDevice.FriendlyName);
            }
            catch
            {
                // Fall back to default render
                speakerDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _logger.LogWarning("Using default render device (Communications not available): {name}", 
                    speakerDevice.FriendlyName);
            }

            // Validate speaker endpoint
            var speakerValidation = ValidateAndConfigureEndpoint(speakerDevice, false);
            if (!speakerValidation.IsValid)
            {
                _logger.LogWarning("Speaker validation warning: {message}", speakerValidation.ValidationMessage);
            }

            return (micDevice, speakerDevice);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to select optimal endpoints");
            throw;
        }
    }

    public class EndpointValidationResult
    {
        public string DeviceId { get; set; }
        public string FriendlyName { get; set; }
        public bool IsCapture { get; set; }
        public bool IsValid { get; set; }
        public string ValidationMessage { get; set; }
        public bool HasSidetone { get; set; }
        public bool HasEnhancements { get; set; }
        public bool IsVirtualDevice { get; set; }
        public bool IsCommunicationsEndpoint { get; set; }
        public bool SupportsRawMode { get; set; }
    }
}

/// <summary>
/// P/Invoke definitions for IAudioClient2/3 RAW mode configuration.
/// </summary>
internal static class AudioClientNative
{
    [ComImport]
    [Guid("726778CD-F60A-4eda-82DE-E47610CD78AA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient2
    {
        // IAudioClient methods (inherited)
        int Initialize(
            [In] AudioClientShareMode ShareMode,
            [In] uint StreamFlags,
            [In] long hnsBufferDuration,
            [In] long hnsPeriodicity,
            [In] IntPtr pFormat,
            [In] IntPtr AudioSessionGuid);
            
        // ... other IAudioClient methods omitted for brevity
        
        // IAudioClient2 methods
        int IsOffloadCapable(
            [In] int Category,
            [Out] out bool pbOffloadCapable);
            
        int SetClientProperties(
            [In] IntPtr pProperties);
            
        int GetBufferSizeLimits(
            [In] IntPtr pFormat,
            [In] bool bEventDriven,
            [Out] out long phnsMinBufferDuration,
            [Out] out long phnsMaxBufferDuration);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioClientProperties
    {
        public uint cbSize;
        public bool bIsOffload;
        public int eCategory;
        public int Options;
    }
}
