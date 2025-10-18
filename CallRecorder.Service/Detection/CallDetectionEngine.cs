using CallRecorder.Core.Config;
using CallRecorder.Core.Models;
using CallRecorder.Service.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CallRecorder.Service.Detection;

// Enhanced call detection using Voice Activity Detection (VAD):
// - Detects actual voice/conversation, not just any audio
// - Requires both mic and speaker voice activity for high confidence
// - Uses longer timeout for voice activity to handle pauses in conversation
public class CallDetectionEngine
{
    private readonly ILogger<CallDetectionEngine> _logger;
    private readonly IAudioCaptureEngine _audio;
    private readonly CallDetectionConfig _cfg;
    
    // Track conversation patterns
    private DateTime _conversationStartTime = DateTime.MinValue;
    private int _consecutiveActiveDetections = 0;

    public CallDetectionEngine(
        ILogger<CallDetectionEngine> logger,
        IAudioCaptureEngine audio,
        IOptions<CallDetectionConfig> cfg)
    {
        _logger = logger;
        _audio = audio;
        _cfg = cfg.Value;
    }

    public CallState Detect()
    {
        var now = DateTime.UtcNow;
        
        // Check for VOICE activity (not just any audio)
        // Use longer timeout for voice (3 seconds) to handle natural pauses in conversation
        bool micVoiceActive = (now - _audio.LastMicVoiceActivityUtc) <= TimeSpan.FromMilliseconds(3000);
        bool speakerVoiceActive = (now - _audio.LastSpeakerVoiceActivityUtc) <= TimeSpan.FromMilliseconds(3000);
        
        // Also check for recent activity to ensure audio is flowing
        bool micHasSignal = (now - _audio.LastMicActivityUtc) <= TimeSpan.FromMilliseconds(5000);
        bool speakerHasSignal = (now - _audio.LastSpeakerActivityUtc) <= TimeSpan.FromMilliseconds(5000);
        
        // Calculate confidence based on voice activity patterns
        int confidence = 0;

        if (micVoiceActive && speakerVoiceActive)
        {
            confidence = 85;
            _consecutiveActiveDetections++;
        }
        else if (micVoiceActive || speakerVoiceActive)
        {
            // Start when either party is speaking (service debounce prevents flapping)
            confidence = 60;
            // Don't increment consecutive count for partial activity
        }
        else
        {
            confidence = 0;
            _consecutiveActiveDetections = 0;
        }

        // Debug log to observe detection inputs
        _logger.LogDebug("VAD micVoice={micV} spkVoice={spkV} micSig={micSig} spkSig={spkSig} conf={conf}",
            micVoiceActive, speakerVoiceActive, micHasSignal, speakerHasSignal, confidence);

        // Early-start on any recent signal if enabled to minimize delay
        bool startOnSignal = _cfg.StartOnAnySignal && (micHasSignal || speakerHasSignal);
        if (startOnSignal && confidence < _cfg.ConfidenceThreshold)
        {
            // Boost to threshold for consistency in logs/UI when starting on signal
            confidence = _cfg.ConfidenceThreshold;
        }

        var isActive = startOnSignal || confidence >= _cfg.ConfidenceThreshold;
        
        // Track conversation start time for debugging
        if (isActive && _conversationStartTime == DateTime.MinValue)
        {
            _conversationStartTime = now;
            _logger.LogDebug("Conversation detected - Voice on mic: {mic}, Voice on speaker: {spk}", 
                micVoiceActive, speakerVoiceActive);
        }
        else if (!isActive && _conversationStartTime != DateTime.MinValue)
        {
            var duration = now - _conversationStartTime;
            _logger.LogDebug("Conversation ended - Duration: {duration}s", duration.TotalSeconds);
            _conversationStartTime = DateTime.MinValue;
        }

        return new CallState
        {
            IsActive = isActive,
            Confidence = confidence,
            ProcessName = null,
            ProcessId = null,
            DetectedAt = now
        };
    }
}
