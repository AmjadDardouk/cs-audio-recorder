# Windows Call Recorder

A robust Windows service for automatic call recording from communication applications like Zoom, Teams, Slack, and others.

## Features

- **Multi-Signal Call Detection**: Uses process monitoring, network activity, and audio analysis
- **Automatic Device Selection**: Intelligently selects the best audio input/output devices
- **Pre-Buffering**: Captures audio before call detection for complete recordings
- **Opus Encoding**: High-quality, efficient audio compression
- **S3 Upload**: Automatic upload to AWS S3 for storage
- **Comprehensive Monitoring**: DataDog integration for metrics and monitoring
- **Windows Service**: Runs as a background service with automatic recovery

## Supported Applications

- Zoom
- Microsoft Teams
- Slack
- Discord
- WebEx
- GoToMeeting
- Skype
- WhatsApp

## Architecture

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│  Call Detection │ -> │ Audio Capture    │ -> │ Recording       │
│  Engine         │    │ Engine           │    │ Manager         │
└─────────────────┘    └──────────────────┘    └─────────────────┘
         │                       │                       │
         ▼                       ▼                       ▼
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│  Device Manager │    │  Upload Manager  │    │  Monitoring     │
│                 │    │                  │    │  Service        │
└─────────────────┘    └──────────────────┘    └─────────────────┘
```

## Prerequisites

- Windows 10/11
- .NET 8.0 Runtime
- Administrator privileges for service installation
- AWS credentials for S3 upload (optional)

## Quick Start

### 1. Build and Deploy

```powershell
# Run as Administrator
.\deploy.ps1 -InstallService
```

### 2. Configure

Edit `C:\CallRecorder\appsettings.json` with your settings:

```json
{
  "Upload": {
    "S3BucketName": "your-bucket-name",
    "AwsAccessKeyId": "your-access-key",
    "AwsSecretAccessKey": "your-secret-key"
  },
  "Monitoring": {
    "DataDogApiKey": "your-datadog-key",
    "SentryDsn": "your-sentry-dsn"
  }
}
```

### 3. Start Service

```powershell
Start-Service -Name "CallRecorderService"
```

## Configuration

### Audio Settings

```json
"Audio": {
  "SampleRate": 48000,
  "Channels": 2,
  "BufferSizeMs": 1000,
  "PreBufferSeconds": 5,
  "AutoDeviceSelection": true
}
```

### Call Detection

```json
"CallDetection": {
  "TargetProcessNames": ["zoom.exe", "teams.exe"],
  "ConfidenceThreshold": 70,
  "DetectionIntervalMs": 1000
}
```

### Recording Settings

```json
"Recording": {
  "OutputDirectory": "C:\\CallRecordings",
  "MaxRecordingDurationMinutes": 120,
  "AudioQuality": 8
}
```

## Monitoring

The service provides comprehensive monitoring through:

- **DataDog Metrics**: Call detection, recording status, upload metrics
- **File Logging**: Detailed logs in `C:\Logs\CallRecorder`
- **Health Checks**: Regular system health monitoring
- **Error Tracking**: Sentry integration for error reporting

### Key Metrics

- `call_detection.confidence` - Call detection confidence level
- `call.started` - Number of calls detected
- `recording.active_sessions` - Currently active recordings
- `upload.success` - Successful uploads
- `health.overall` - Overall system health

## Troubleshooting

### Common Issues

1. **Service won't start**
   - Check logs in `C:\Logs\CallRecorder`
   - Verify .NET 8.0 is installed
   - Ensure administrator privileges

2. **No recordings**
   - Check supported applications are running
   - Verify audio devices are available
   - Check call detection confidence threshold

3. **Upload failures**
   - Verify AWS credentials
   - Check network connectivity
   - Verify S3 bucket permissions

### Log Files

- Application logs: `C:\Logs\CallRecorder\callrecorder-{date}.log`
- Windows Event Log: `Application and Services Logs\CallRecorderService`

## Development

### Building from Source

```bash
dotnet build
```

### Running Tests

```bash
dotnet test
```

### Project Structure

```
src/
├── CallRecorder.Core/          # Core interfaces and models
├── CallRecorder.Service/       # Windows service implementation
tests/
└── CallRecorder.Tests/         # Unit and integration tests
```

## Security Considerations

- Audio recordings are stored locally before upload
- S3 uploads use private ACLs
- No sensitive data is logged
- Service runs with minimal required privileges

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Support

For issues and feature requests, please create an issue in the project repository.



I implemented a local-only MVP of the Windows call recorder per your request (no S3, just record locally) and wired it into your existing solution. You can run it via Visual Studio 2022 to validate call detection and recording with dual-channel capture (mic and speakers to separate WAV files).

What was added/changed

1) Packages and TFM (Service project)
- CallRecorder.Service now targets net8.0-windows10.0.19041.0 and references:
  - Microsoft.Extensions.Hosting.WindowsServices
  - Microsoft.Extensions.Options.ConfigurationExtensions
  - NAudio (2.2.1)
- Project reference to CallRecorder.core is added.

2) Core models/config (CallRecorder.core)
- Config:
  - Config/RecordingConfig.cs (OutputDirectory, PreBufferSeconds, format hints)
  - Config/CallDetectionConfig.cs (PollIntervalMs, ConfidenceThreshold)
- Models:
  - Models/CallState.cs
  - Models/RecordingModels.cs (AudioChannel, ProcessInfo, RecordingMetadata)

3) Audio capture engine (Service project)
- Audio/AudioCaptureEngine.cs: 
  - Uses WasapiCapture (microphone) and WasapiLoopbackCapture (speakers).
  - Maintains a rolling pre-buffer per channel sized by PreBufferSeconds.
  - Exposes activity metrics for detection (LastMicActivityUtc, LastSpeakerActivityUtc).
  - Exposes device formats and supports attaching/detaching active writers.
  - Thread-safe writes to WAV writers.

4) Detection (audio-first MVP)
- Detection/CallDetectionEngine.cs:
  - Considers a call active when both mic and speaker activity have been observed within ~1.5 seconds.
  - Confidence: 80 (both active), 40 (only one), 0 (none).
  - Threshold set via CallDetectionConfig. Default is 70, so both channels are required to start.

5) Recording manager (local WAV)
- Recording/RecordingManager.cs:
  - On StartAsync:
    - Creates OutputDirectory if needed.
    - Creates two WAV writers (mic and speakers) using actual device formats.
    - Flushes pre-buffer from audio engine, then attaches writers for live passthrough.
  - On StopAsync:
    - Detaches writers and finalizes files.
    - Emits RecordingMetadata containing mic/speaker file paths and duration.

6) Orchestrator (HostedService)
- Hosted/CallRecordingService.cs:
  - Starts continuous audio capture at startup.
  - Polls detection every PollIntervalMs (default 500ms).
  - Debounces state changes by ~1 second to avoid flapping.
  - Starts/stops recording based on detection.

7) DI wiring and configuration
- Program.cs updated to:
  - Configure RecordingConfig and CallDetectionConfig from appsettings.
  - Register IAudioCaptureEngine, CallDetectionEngine, IRecordingManager.
  - Register CallRecordingService as hosted service (replaces the previous Worker loop).
- appsettings.json extended with:
  - "Recording": OutputDirectory, PreBufferSeconds, SampleRate, BitsPerSample, Channels.
  - "CallDetection": PollIntervalMs, ConfidenceThreshold (default 70), MinimumCallDurationSeconds.

Notes
- Worker.cs remains in the project but is not registered in DI; it won’t run.
- This MVP writes two WAV files per call: one for microphone, one for speakers.
- SampleRate/Bits/Channels are not imposed on the devices for MVP; actual device formats are used to avoid resampling complexity. This is typical for a first-step validation.

How to run in Visual Studio 2022 (recommended)
1) Set CallRecorder.Service as the Startup Project.
2) Ensure your Windows privacy settings allow desktop app microphone access:
   - Settings → Privacy & security → Microphone → “Allow desktop apps to access your microphone” = On
3) Verify OutputDirectory exists (default C:\ProgramData\CallRecorder\Temp). If not, create it or edit appsettings.json.
4) Start Debugging (F5). Logs will show:
   - “CallRecordingService starting”
   - “Starting audio capture…” with device names
5) Place or receive a test call (or, for a quick check, speak into mic and play audio from speakers). With both channels active, detection confidence = 80.
6) When call starts, logs: “Call detected… Starting recording…”
7) When call ends (silence), logs: “Call ended. Stopping recording…” and output the file paths:
   - C:\ProgramData\CallRecorder\Temp\{timestamp}_{id}_mic.wav
   - C:\ProgramData\CallRecorder\Temp\{timestamp}_{id}_spk.wav
8) Stop debugging (Shift+F5) when done.

Troubleshooting
- If a call doesn’t start recording, check:
  - Microphone activity: talk into your mic; you should see it in Windows Sound settings as well.
  - Speaker activity: ensure audio is actually being played through the default device (music/other caller).
  - Detection requires both channels active; only one channel active yields confidence = 40 (below threshold 70).
  - You can temporarily lower ConfidenceThreshold in appsettings.json to 40 to validate each channel independently.
- If files aren’t created:
  - Confirm OutputDirectory is writable: C:\ProgramData\CallRecorder\Temp.
  - Check for exceptions in the VS debug output window.
- If audio files contain silence:
  - Confirm the correct devices are default Communication (mic) and default Render (speakers).
  - In Windows Sound settings, verify levels are moving for the intended devices.
  - Some VDI/remote scenarios can affect loopback availability; if in AVD, ensure audio redirection is enabled.

Next optional steps (not implemented now)
- Merge mic/speaker to a stereo file or encode to Opus.
- Add network/process detection to improve accuracy.
- Reintroduce UploadManager and S3 once recording validated.

The code has been integrated and is ready to validate recording locally using Visual Studio 2022 as you requested. No S3 or upload logic is included in this MVP, just local WAV outputs for verification.