Prompt for Cline: We still have speaker-to-mic leakage: the far-end (speaker) audio is being picked up by the microphone, causing echo and muddy recordings. Fix this decisively. Focus only on eliminating echo/bleed and guaranteeing clean separation.

Target

Left = local mic (processed, echo-cancelled, clean)
Right = far-end/system audio (clean, no mic bleed)
Zero audible echo on Left; no mic in Right
Use these libraries/tools

NAudio (NuGet: NAudio) for WASAPI capture/loopback and CoreAudio endpoint/session APIs (IAudioClient2/3, IAudioClock, IAudioSessionManager2).
WebRTC Audio Processing (AEC3) via native lib (vcpkg: webrtc-audio-processing) with a small C++/CLI or P/Invoke wrapper.
Optional fallback: Windows.Media.Capture with AudioProcessing.Communications for built-in AEC (use this only if WebRTC AEC3 integration fails).
Optional: MediaFoundationResampler (NAudio) for tiny drift corrections.
Make these changes

Eliminate mixed/contaminated sources (critical)
Mic capture:
Use the physical Communications capture endpoint (default communications mic). Do NOT use “Stereo Mix,” virtual cables, or any “what-you-hear” device.
Force RAW stream: call IAudioClient2.SetClientProperties with AUDCLNT_STREAMOPTIONS_RAW; Shared mode, event-driven.
Far-end capture:
Prefer the Communications render endpoint loopback (AUDCLNT_STREAMFLAGS_LOOPBACK on that endpoint), not the default device.
Attempt per-app selection: use IAudioSessionManager2 to identify active sessions for call apps (teams.exe, zoom.exe, skype.exe, webex*, slack*, meet*). If you can’t isolate the session, stick to the Communications endpoint but verify no sidetone is present (see detector below).
Sidetone/“Listen to this device” detection:
Add a startup check: measure cross-correlation between near_raw and far_end during a 3–5 s capture with no local speech (VAD-silent). If strong correlation at near-zero lag (|r| > 0.2 within ±10 ms), then the render mix includes the mic (sidetone or “Listen to this device”).
If detected, refuse to start recording and log a clear error: “Mic monitoring/sidetone detected on render path. Disable ‘Listen to this device’ in Recording device properties or switch to headphones.” Provide endpoint name and GUID in the log.
Single AEC path, RAW endpoints (no double processing)
Ensure both mic and loopback are opened with RAW mode (disable APO/SysFx). Do not also enable Windows Communications effects if you use WebRTC AEC3.
If you choose fallback (Windows built-in AEC), then:
Mic: Capture via Windows.Media.Capture (AudioProcessing.Communications); do NOT run WebRTC AEC simultaneously.
Far-end: WASAPI loopback as above.
Make this a compile-time or config switch (aec.mode = “webrtc” | “windows”).
WebRTC AEC3 wiring and timing (if aec.mode=webrtc)
Fixed format: 48 kHz, mono per stream, strict 10 ms frames (480 samples).
Configure APM:
AEC3 enabled
DelayAgnostic=true, ExtendedFilter=true, RefinedAdaptiveFilter=true
EchoSuppression=VeryHigh, HighPassFilter=true, NoiseSuppression=High
GainController2 enabled with limiter; ceiling -1.0 dBFS
Processing order per 10 ms frame: a) Pop the aligned 10 ms far-end frame -> apm.ReverseStream(far10ms) b) Read 10 ms mic -> apm.ProcessStream(near10ms, streamDelayMs) -> nearProcessed
Delay handling:
Compute streamDelayMs = renderLatencyMs + captureLatencyMs + bufferAlignMs
renderLatencyMs/captureLatencyMs from IAudioClient::GetStreamLatency
bufferAlignMs from timestamp alignment (IAudioClock/QPC deltas)
Clamp 0–200 ms; log each second
Never process AEC on the far-end path; only feed it as reverse.
Reverse stream alignment and drift control
Maintain a ~200 ms ring buffer for far-end frames, keyed by QPC timestamps (from IAudioClock).
On each mic frame, choose the far-end frame whose timestamp best matches (targeting the estimated delay). Log the deltaMs used.
If long-run skew > 10 ms, gently resample one path (factor 0.99999–1.00001) using MediaFoundationResampler to keep lock-step without breaking 10 ms framing.
Hard safeguards to prevent recording with echo
Headphones enforcement mode (configurable):
During startup or when echo is detected (see detector), if leakage > threshold, pause and display: “Speaker-to-mic leakage detected. Please use headphones or disable mic monitoring.” Retry periodically.
LeakageGuard runtime check:
When VAD says near-end is silent and far-end is active, compute coherence/correlation between far_end and near_raw over 1 s windows.
If leakage > -25 dB persistently for >3 s, raise an alert and auto-increase AEC suppression and initial delay estimate by +15 ms; if still failing after 10 s, auto-stop and show remediation steps.
Diagnostics to prove it’s fixed
Temporary debug WAVs: near_raw.wav, far_end.wav, near_processed.wav (30–60 s samples).
Metrics every second:
ERLE (near_raw vs near_processed in bands where far_end is active): target ≥ 20 dB
Residual leakage estimate of far_end in near_processed: target ≤ -35 dB
Cross-correlation peak of near_processed vs far_end across ±200 ms: should be very low; no strong peak near 0 lag
Reverse buffer occupancy, streamDelayMs, underruns/overruns
If any metric fails thresholds, print a concrete diagnosis:
“ReverseStream not called before ProcessStream”
“Delay mismatch: adjust initialDelayMs (+/-)”
“Render path contains mic (sidetone). Disable ‘Listen to this device’ or use headphones”
“Using mixed device (Stereo Mix). Select physical Communications mic”
Gains and limiter (to avoid nonlinearity that breaks AEC)
Apply conservative pre-gain:
micPreGainDb = -3 dB
farEndPreGainDb = -6 dB
Enable a lookahead limiter (3–5 ms) with -1.0 dBFS ceiling on both channels before writing to file. No clipping permitted.
Config (appsettings.json)
wasapi.raw=true
aec.mode="webrtc" | "windows"
aec.webrtc: { delayAgnostic=true, extendedFilter=true, refinedAdaptiveFilter=true, suppression="VeryHigh", initialDelayMs=45 }
gains: { micPreDb=-3, farEndPreDb=-6 }
limiter: { enabled=true, ceilingDb=-1.0 }
endpoints: { preferCommunicationsRole=true, blockStereoMix=true, sessionWhitelist=[ "teams.exe", "zoom.exe", "skype.exe", "webex", "slack", "meet" ] }
safeguards: { requireHeadphonesOnLeak=true, leakageThresholdDb=-25 }
diagnostics: { echoDebug=true, writeMonoDumps=true }
Concrete code tasks

Update WASAPI init for both devices to RAW mode (IAudioClient2.SetClientProperties with AUDCLNT_STREAMOPTIONS_RAW).
Ensure mic uses the Communications capture endpoint; ensure loopback uses Communications render endpoint; log endpoint friendly names and IDs.
Implement ReverseBuffer keyed by QPC timestamps; use IAudioClock::GetPosition + QPC for both streams.
APM wrapper:
Configure(AEC3 params), ReverseStream(ReadOnlySpan<float> far10ms), ProcessStream(ReadOnlySpan<float> near10ms, int delayMs, Span<float> out10ms), GetStats()
Add LeakageDetector:
Uses WebRTC VAD (or simple energy VAD) to find near-silent segments
Computes correlation/coherence far_end -> near_raw; returns leakage dB and lag
Enforce safeguards: if leakage high at startup or runtime, block/stop and show actionable guidance.
Add limiter and ensure float->int16 conversion uses TPDF dither; no clipping.
Acceptance criteria

With laptop speakers at normal volume:
Left (near_processed) has no audible far-end echo; ERLE ≥ 20 dB, leakage ≤ -35 dB
Right (far_end) has no mic bleed (≤ -35 dB)
Cross-correlation between near_processed and far_end shows no strong peak at any lag
If sidetone/monitor is enabled in OS, the app refuses to record and instructs the user to disable it or use headphones.
Peaks ≤ -1.0 dBFS; 0 clipped samples.
After implementation

Add a 60 s diagnostic run. Paste logs of ERLE, leakage, cross-correlation peak/lag, average streamDelayMs, and reverse buffer occupancy. If any threshold fails, print which step to adjust (e.g., raise initialDelayMs by +20 ms, verify ReverseStream order, or confirm RAW mode/endpoints).