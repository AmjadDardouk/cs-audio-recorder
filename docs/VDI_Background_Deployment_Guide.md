# CallRecorder – VDI Background Deployment Guide

Last updated: 2025-10-18

Scope
This guide explains how to deploy CallRecorder.Service as a background recorder on Windows VDI. It runs in the logged‑in user’s session via a Scheduled Task, ensuring access to user‑mode audio devices. Do not install it as a Windows Service; Session 0 cannot capture user audio devices in most VDI environments.

Recommended audience
- IT admins preparing gold images or user environments
- Engineers producing the EXE and config for distribution

Repository components referenced
- Service project: CallRecorder.Service
- Configuration: CallRecorder.Service/appsettings.json

Why Option B?
- Windows Services run in Session 0 and typically cannot access per‑user audio sessions/devices.
- A per‑user Scheduled Task runs in the interactive session and can access WASAPI input/loopback as permitted by policy.

----------------------------------------

1) Prerequisites

1.1 OS and platform
- Windows 10 or 11 VDI (x64).
- Build target: net8.0 / win-x64 recommended.

1.2 Audio in VDI
- RDP client (mstsc.exe):
  - Show Options → Local Resources → Remote audio → Settings:
    - Remote audio playback: Play on this computer
    - Remote audio recording: Record from this computer
- Windows privacy in the VDI session:
  - Settings → Privacy & Security → Microphone → Allow desktop apps to access your microphone = On
- Other stacks (Citrix, AVD, VMware):
  - Enable microphone and speaker redirection (check your policy/GPO).
  - “Teams/Zoom optimization” may offload/redirect audio, preventing loopback capture of far‑end audio. See Troubleshooting.

1.3 File permissions and paths
- Choose a writable output path for recordings:
  - Recommended (per‑user): %LOCALAPPDATA%\CallRecorder\Recordings
  - Or (system‑wide): C:\ProgramData\CallRecorder\Recordings (ensure ACLs allow writing by the run context)
- Executable install path (suggested):
  - C:\Program Files\CallRecorder.Service (requires elevation to write)

1.4 Legal/compliance
- Ensure compliance with laws and company policies on call recording, consent, banners, data retention, and storage (local vs network share).

----------------------------------------

2) Build the self‑contained EXE (on your build machine)

Run from repository root:

- dotnet restore
- dotnet publish .\CallRecorder.Service\CallRecorder.Service.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true

Output:
- .\CallRecorder.Service\bin\Release\net8.0\win-x64\publish\CallRecorder.Service.exe

Notes:
- Self‑contained single‑file means .NET runtime is bundled; nothing to install on VDI.
- Keep the EXE and appsettings.json together in the final install folder.

Using the included publish script
- Alternatively, run tools\publish\publish.bat which restores and publishes a self‑contained single‑file EXE, and copies appsettings.json into the publish output.
- From repo root:
  - tools\publish\publish.bat
- Output path:
  - CallRecorder.Service\bin\Release\net8.0\win-x64\publish\CallRecorder.Service.exe

----------------------------------------

3) Configuration (appsettings.json)

The project’s current configuration (CallRecorder.Service/appsettings.json):

{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },

  "Recording": {
    "OutputDirectory": "C:\\ProgramData\\CallRecorder\\Temp",
    "PreBufferSeconds": 3,
    "PostRollSeconds": 3,
    "DiscardInitialMs": 300,
    "SampleRate": 48000,
    "BitsPerSample": 16,
    "Channels": 2
  },

  "CallDetection": {
    "PollIntervalMs": 100,
    "ConfidenceThreshold": 50,
    "MinimumCallDurationSeconds": 3,
    "StopHangoverMs": 2000,
    "StartOnAnySignal": true
  },

  "AudioDsp": {
    "EchoCancellation": true,
    "NoiseSuppression": true,
    "HighPass": true,
    "Agc": true,
    "SuppressionLevel": "High",
    "FrameMs": 15,
    "SampleRate": 48000,
    "NearGainDb": -3.0,
    "FarGainDb": -6.0,
    "Normalize": true,
    "TargetRmsDbfs": -20,
    "MaxGainDb": 6,
    "AttackMs": 30,
    "ReleaseMs": 500,
    
    "EnableLimiter": true,
    "LimiterCeilingDbfs": -1.0,
    "LimiterLookaheadMs": 3,
    
    "EchoSuppressionLevel": "VeryHigh",
    "DelayAgnostic": true,
    "ExtendedFilter": true,
    "RefinedAdaptiveFilter": true,
    "InitialDelayMs": 200,

    "DiagnosticsEnableMonoDumps": false,
    "DiagnosticsTestToneCheck": false,
    
    "EnableDithering": true,
    "DitherType": "TriangularPdf",
    "DitherAmountDb": -96,
    
    "LowPass": true,
    "LowPassHz": 9000
  },

  "CallState": {
    "CommunicationsOnly": true,
    "ProcessWhitelist": [ "teams.exe", "zoom.exe", "skype.exe", "lync.exe", "webex.exe" ],
    "ProcessBlacklist": [],
    "RingHoldMs": 2000,
    "ConnectBidirectionalWindowMs": 1500,
    "EndSilenceMs": 3000,
    "MinAverageRmsDbfs": -45.0,
    "PreferWhitelistedProcesses": true,
    "AllowGeneralRenderFallback": true
  },

  "AudioDevice": {
    "PreferCommunicationsEndpoints": true,
    "MicInclude": [ "Microphone", "Headset", "Mic" ],
    "MicExclude": [ "Line In", "Stereo Mix", "What U Hear", "Loopback", "Virtual", "Monitor", "Listen" ],
    "SpeakerInclude": [ "Speakers", "Headset", "Headphones" ],
    "SpeakerExclude": [ "Virtual", "VB-Audio", "Voicemeeter" ],
    "LogDeviceEnumeration": true,
    "MinSampleRateHz": 44100,
    "MinBitsPerSample": 16,
    "ForceRawMode": true,
    "RejectSidetone": true
  }
}

VDI‑specific recommendation
- Prefer a per‑user output directory:
  {
    "Recording": {
      "OutputDirectory": "%LOCALAPPDATA%\\CallRecorder\\Recordings",
      ...
    }
  }
- Ensure the directory exists or is created at runtime; see installer snippet below to pre‑create it.

----------------------------------------

4) Install on VDI

Using the included installer script (recommended)
- A one‑shot PowerShell script is provided at tools\publish\Install-CallRecorderService.ps1 to copy or download the EXE/config, pre‑create the output directory, register a per‑user Scheduled Task (At logon, Highest), and optionally start it now.
- Examples:
  - From local publish folder:
    - PowerShell (Admin): .\tools\publish\Install-CallRecorderService.ps1 -ExePath ".\CallRecorder.Service\bin\Release\net8.0\win-x64\publish\CallRecorder.Service.exe" -ConfigPath ".\CallRecorder.Service\bin\Release\net8.0\win-x64\publish\appsettings.json" -StartNow
  - From remote URLs:
    - PowerShell (Admin): .\tools\publish\Install-CallRecorderService.ps1 -ExeUrl "https://your.domain/packages/CallRecorder.Service.exe" -ConfigUrl "https://your.domain/packages/appsettings.json" -StartNow
  - Use files already in the install directory:
    - PowerShell (Admin): .\tools\publish\Install-CallRecorderService.ps1 -StartNow
- Parameters:
  - -InstallDir "C:\Program Files\CallRecorder.Service" (default)
  - -TaskName "CallRecorderService" (default)
  - -UserId "$env:USERNAME" (default; set DOMAIN\user when running elevated)

4.1 Create install folder and copy files
- Create: C:\Program Files\CallRecorder.Service
- Copy:
  - CallRecorder.Service.exe
  - appsettings.json (tweak Recording.OutputDirectory if desired)
- Pre‑create the output directory if using %LOCALAPPDATA%\CallRecorder\Recordings (optional but recommended).

4.2 Register a per‑user Scheduled Task (runs at logon)
- Run the following PowerShell in the user context (or specify -UserId):
  $exe = "C:\Program Files\CallRecorder.Service\CallRecorder.Service.exe"
  $taskName = "CallRecorderService"
  $action = New-ScheduledTaskAction -Execute $exe
  $trigger = New-ScheduledTaskTrigger -AtLogOn
  $principal = New-ScheduledTaskPrincipal -UserId "$env:USERNAME" -LogonType Interactive -RunLevel Highest
  if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
  }
  Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal
  Start-ScheduledTask -TaskName $taskName

- Alternative (schtasks):
  schtasks /Create /TN "CallRecorderService" /TR "\"C:\Program Files\CallRecorder.Service\CallRecorder.Service.exe\"" /SC ONLOGON /RL HIGHEST /F
  schtasks /Run /TN "CallRecorderService"

Why Scheduled Task?
- Ensures the process runs in the user’s interactive session for audio access.
- Avoids Session 0 isolation issues typical with Windows Services.

----------------------------------------

5) Validation

- Audio redirection:
  - RDP (or equivalent) must redirect mic and speakers into the VDI session.
  - Windows privacy must allow desktop apps to access the microphone.
- Task running:
  - Task Scheduler → Task Scheduler Library → CallRecorderService → Status: Running
- Output files:
  - Check the configured Recording.OutputDirectory for .wav (or your output format) during/after calls.
- Manual run (optional):
  - Run the EXE once from an elevated command prompt to confirm it starts without errors (it’s a worker; observe logs/console).

----------------------------------------

6) Troubleshooting

- Only microphone recorded; no far‑end/remote audio:
  - “Teams/Zoom optimization” (offloading) may prevent loopback capture of the far‑end mix. Test with optimization disabled or accept single‑ended capture per policy.
- No recordings appear:
  - Confirm the scheduled task is running in the user context (not LocalSystem).
  - Verify a playback device exists in the session (even a virtual sink).
  - Ensure Recording.OutputDirectory is writable by the user (prefer %LOCALAPPDATA%).
- Access denied / SmartScreen:
  - If distributing unsigned binaries, SmartScreen or policy may block first run. Prefer code‑signing internally in enterprise environments.
- Service install doesn’t record:
  - Uninstall any Windows Service attempts. Use Scheduled Task in user session instead.
- Device selection issues:
  - AudioDevice.PreferCommunicationsEndpoints=true targets communications endpoints. Adjust include/exclude lists as needed for your environment.

----------------------------------------

7) Update / Uninstall

Update:
- Stop the task:
  - Stop-ScheduledTask -TaskName "CallRecorderService"
- Replace:
  - Overwrite EXE and/or appsettings.json in C:\Program Files\CallRecorder.Service
- Restart:
  - Start-ScheduledTask -TaskName "CallRecorderService"
  - Or log off/on.

Uninstall:
- schtasks /Delete /TN "CallRecorderService" /F
- Remove-Item "C:\Program Files\CallRecorder.Service" -Recurse -Force

----------------------------------------

8) Optional: One‑shot remote download & install snippet

Use this if hosting artifacts on HTTPS or a share. Run PowerShell elevated if writing to Program Files.

Param(
  [string]$InstallDir = "C:\Program Files\CallRecorder.Service",
  [string]$ExeUrl = "https://your.domain/packages/CallRecorder.Service.exe",
  [string]$ConfigUrl = "https://your.domain/packages/appsettings.json"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Invoke-WebRequest -Uri $ExeUrl -OutFile (Join-Path $InstallDir "CallRecorder.Service.exe")
Invoke-WebRequest -Uri $ConfigUrl -OutFile (Join-Path $InstallDir "appsettings.json")

# Pre-create output directory if configured
try {
  $cfg = Get-Content (Join-Path $InstallDir "appsettings.json") -Raw | ConvertFrom-Json
  $outDir = [Environment]::ExpandEnvironmentVariables($cfg.Recording.OutputDirectory)
  if ($outDir) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
} catch {}

$action = New-ScheduledTaskAction -Execute (Join-Path $InstallDir "CallRecorder.Service.exe")
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERNAME" -LogonType Interactive -RunLevel Highest

if (Get-ScheduledTask -TaskName "CallRecorderService" -ErrorAction SilentlyContinue) {
  Unregister-ScheduledTask -TaskName "CallRecorderService" -Confirm:$false
}
Register-ScheduledTask -TaskName "CallRecorderService" -Action $action -Trigger $trigger -Principal $principal | Out-Null
Start-ScheduledTask -TaskName "CallRecorderService"

Write-Host "Installed. Verify recordings in the configured OutputDirectory."

----------------------------------------

9) Security and compliance notes

- Code‑sign the EXE with your enterprise certificate for smoother deployment (SmartScreen, trust).
- Lock down install directory ACLs (Program Files default is restricted). Ensure only Administrators can modify binaries.
- Store recordings per policy (local, network share, encryption at rest if required).
- Set retention and purge policies aligned with legal/compliance requirements.

----------------------------------------

Appendix A – Recommended VDI appsettings.json (example)

{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },
  "Recording": {
    "OutputDirectory": "%LOCALAPPDATA%\\CallRecorder\\Recordings",
    "PreBufferSeconds": 3,
    "PostRollSeconds": 3,
    "DiscardInitialMs": 300,
    "SampleRate": 48000,
    "BitsPerSample": 16,
    "Channels": 2
  },
  "CallDetection": {
    "PollIntervalMs": 100,
    "ConfidenceThreshold": 50,
    "MinimumCallDurationSeconds": 3,
    "StopHangoverMs": 2000,
    "StartOnAnySignal": true
  },
  "AudioDsp": {
    "EchoCancellation": true,
    "NoiseSuppression": true,
    "HighPass": true,
    "Agc": true,
    "SuppressionLevel": "High",
    "FrameMs": 15,
    "SampleRate": 48000,
    "NearGainDb": -3.0,
    "FarGainDb": -6.0,
    "Normalize": true,
    "TargetRmsDbfs": -20,
    "MaxGainDb": 6,
    "AttackMs": 30,
    "ReleaseMs": 500,
    "EnableLimiter": true,
    "LimiterCeilingDbfs": -1.0,
    "LimiterLookaheadMs": 3,
    "EchoSuppressionLevel": "VeryHigh",
    "DelayAgnostic": true,
    "ExtendedFilter": true,
    "RefinedAdaptiveFilter": true,
    "InitialDelayMs": 200,
    "DiagnosticsEnableMonoDumps": false,
    "DiagnosticsTestToneCheck": false,
    "EnableDithering": true,
    "DitherType": "TriangularPdf",
    "DitherAmountDb": -96,
    "LowPass": true,
    "LowPassHz": 9000
  },
  "CallState": {
    "CommunicationsOnly": true,
    "ProcessWhitelist": [ "teams.exe", "zoom.exe", "skype.exe", "lync.exe", "webex.exe" ],
    "ProcessBlacklist": [],
    "RingHoldMs": 2000,
    "ConnectBidirectionalWindowMs": 1500,
    "EndSilenceMs": 3000,
    "MinAverageRmsDbfs": -45.0,
    "PreferWhitelistedProcesses": true,
    "AllowGeneralRenderFallback": true
  },
  "AudioDevice": {
    "PreferCommunicationsEndpoints": true,
    "MicInclude": [ "Microphone", "Headset", "Mic" ],
    "MicExclude": [ "Line In", "Stereo Mix", "What U Hear", "Loopback", "Virtual", "Monitor", "Listen" ],
    "SpeakerInclude": [ "Speakers", "Headset", "Headphones" ],
    "SpeakerExclude": [ "Virtual", "VB-Audio", "Voicemeeter" ],
    "LogDeviceEnumeration": true,
    "MinSampleRateHz": 44100,
    "MinBitsPerSample": 16,
    "ForceRawMode": true,
    "RejectSidetone": true
  }
}

----------------------------------------

Appendix B – Publish checklist

- [ ] dotnet restore succeeds
- [ ] dotnet publish produces CallRecorder.Service.exe (self‑contained, single‑file)
- [ ] appsettings.json reviewed and OutputDirectory appropriate for VDI
- [ ] EXE and config copied to C:\Program Files\CallRecorder.Service (or chosen folder)
- [ ] Scheduled Task created (Interactive, Highest, At logon)
- [ ] Recording directory exists and is writable
- [ ] Validation done with a quick call test

----------------------------------------

Appendix C – Quick commands (copy/paste)

Build (on your machine):
- dotnet publish .\CallRecorder.Service\CallRecorder.Service.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true

Register task (on VDI):
- $exe = "C:\Program Files\CallRecorder.Service\CallRecorder.Service.exe"
- $taskName = "CallRecorderService"
- $action = New-ScheduledTaskAction -Execute $exe
- $trigger = New-ScheduledTaskTrigger -AtLogOn
- $principal = New-ScheduledTaskPrincipal -UserId "$env:USERNAME" -LogonType Interactive -RunLevel Highest
- if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
- Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal
- Start-ScheduledTask -TaskName $taskName

That’s it. Share this document with your team or IT admins to deploy the background recorder reliably in VDI.
