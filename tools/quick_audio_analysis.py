#!/usr/bin/env python3
import sys, wave, struct, math, os
from array import array

def read_wav_stereo(path):
    with wave.open(path, 'rb') as wf:
        nch = wf.getnchannels()
        sr = wf.getframerate()
        sw = wf.getsampwidth()
        nframes = wf.getnframes()
        frames = wf.readframes(nframes)

    if nch != 2:
        raise RuntimeError(f"Expected stereo WAV (2 channels), got {nch} channels")

    # Interpret samples
    if sw == 2:
        # int16 PCM
        a = array('h')
        a.frombytes(frames)
        # normalize to [-1,1]
        norm = 32768.0
        left = [a[i*2] / norm for i in range(len(a)//2)]
        right = [a[i*2+1] / norm for i in range(len(a)//2)]
    elif sw == 4:
        # Try float32 little-endian, else int32
        count = len(frames) // 4
        vals = struct.unpack('<' + 'f'*count, frames[:count*4])
        maxabs = max(abs(v) for v in vals) if vals else 0.0
        if maxabs > 10.0:
            vals_i32 = struct.unpack('<' + 'i'*count, frames[:count*4])
            norm = 2147483648.0
            vals = [vi / norm for vi in vals_i32]
        left = [vals[i*2] for i in range(len(vals)//2)]
        right = [vals[i*2+1] for i in range(len(vals)//2)]
    else:
        raise RuntimeError(f"Unsupported sample width {sw} bytes")

    return sr, left, right

def rms(arr):
    if not arr:
        return 0.0
    s = 0.0
    for v in arr:
        s += v*v
    return math.sqrt(s/len(arr))

def peak(arr):
    return max((abs(v) for v in arr), default=0.0)

def mean(arr):
    if not arr:
        return 0.0
    return sum(arr)/len(arr)

def dbfs(x):
    if x <= 0.0:
        return -999.0
    return 20.0 * math.log10(x)

def noise_floor_db(arr, sr, win_ms=50, percentile=0.2):
    win = max(1, int(sr*win_ms/1000))
    if len(arr) < win:
        r = rms(arr)
        return dbfs(r)
    rms_list = []
    for i in range(0, len(arr)-win, win):
        r = rms(arr[i:i+win])
        rms_list.append(r)
    if not rms_list:
        return dbfs(rms(arr))
    rms_list.sort()
    idx = max(0, int(len(rms_list)*percentile)-1)
    return dbfs(rms_list[idx])

def analyze_clipping(arr, threshold=0.95):
    clipped_samples = sum(1 for v in arr if abs(v) >= threshold)
    return clipped_samples, (clipped_samples / len(arr)) * 100 if arr else 0

def analyze_dynamic_range(arr):
    if not arr:
        return 0.0
    peak_val = peak(arr)
    rms_val = rms(arr)
    if rms_val > 0:
        return dbfs(peak_val) - dbfs(rms_val)
    return 0.0

def main():
    if len(sys.argv) < 2:
        print("Usage: python tools/quick_audio_analysis.py <path-to-stereo-wav>")
        sys.exit(1)
    path = sys.argv[1]
    if not os.path.exists(path):
        print(f"File not found: {path}")
        sys.exit(2)

    print("Reading audio file...")
    sr, L, R = read_wav_stereo(path)

    # Basic metrics
    L_rms = rms(L); R_rms = rms(R)
    L_peak = peak(L); R_peak = peak(R)
    L_dc = mean(L); R_dc = mean(R)

    # Clipping analysis
    L_clipped, L_clip_pct = analyze_clipping(L)
    R_clipped, R_clip_pct = analyze_clipping(R)

    # Dynamic range
    L_dr = analyze_dynamic_range(L)
    R_dr = analyze_dynamic_range(R)

    # Noise floor estimates
    L_floor = noise_floor_db(L, sr, win_ms=50, percentile=0.2)
    R_floor = noise_floor_db(R, sr, win_ms=50, percentile=0.2)

    # Sample a small portion for basic correlation (avoid hang)
    sample_size = min(44100, len(L), len(R))  # 1 second max
    L_sample = L[:sample_size]
    R_sample = R[:sample_size]
    
    # Simple correlation coefficient
    corr_coeff = 0.0
    if L_sample and R_sample:
        L_mean = mean(L_sample)
        R_mean = mean(R_sample)
        numerator = sum((l - L_mean) * (r - R_mean) for l, r in zip(L_sample, R_sample))
        L_var = sum((l - L_mean) ** 2 for l in L_sample)
        R_var = sum((r - R_mean) ** 2 for r in R_sample)
        if L_var > 0 and R_var > 0:
            corr_coeff = numerator / math.sqrt(L_var * R_var)

    print("==== AUDIO QUALITY ANALYSIS REPORT ====")
    print(f"File: {path}")
    print(f"Sample rate: {sr} Hz, Length: {len(L)/sr:.2f} s")
    print(f"File size: {os.path.getsize(path) / (1024*1024):.1f} MB")
    print()
    
    print("-- FORMAT COMPLIANCE --")
    target_sr = 48000
    if sr != target_sr:
        print(f"❌ Sample rate is {sr}Hz, should be {target_sr}Hz for professional quality")
    else:
        print(f"✅ Sample rate: {sr}Hz (professional standard)")
    
    print("-- LEVELS (dBFS) --")
    print(f"L (mic)  RMS: {dbfs(L_rms):6.1f} dBFS   Peak: {dbfs(L_peak):6.1f} dBFS   DC offset: {L_dc:+.5f}")
    print(f"R (spkr) RMS: {dbfs(R_rms):6.1f} dBFS   Peak: {dbfs(R_peak):6.1f} dBFS   DC offset: {R_dc:+.5f}")
    print()
    
    print("-- QUALITY METRICS --")
    print(f"Estimated noise floor:  L {L_floor:6.1f} dBFS   R {R_floor:6.1f} dBFS")
    print(f"Dynamic range:          L {L_dr:6.1f} dB      R {R_dr:6.1f} dB")
    print(f"Channel correlation: {corr_coeff:+.3f}")
    print()
    
    print("-- CLIPPING ANALYSIS --")
    print(f"L channel clipped samples: {L_clipped:,} ({L_clip_pct:.3f}%)")
    print(f"R channel clipped samples: {R_clipped:,} ({R_clip_pct:.3f}%)")
    print()

    print("-- QUALITY ISSUES DETECTED --")
    issues_found = False
    
    # Sample rate check
    if sr != 48000:
        print(f"⚠️  Sample rate is {sr}Hz instead of professional 48kHz standard")
        issues_found = True
    
    # DC offset check
    if abs(L_dc) > 0.01:
        print(f"⚠️  Left channel has significant DC offset ({L_dc:+.5f})")
        issues_found = True
    if abs(R_dc) > 0.01:
        print(f"⚠️  Right channel has significant DC offset ({R_dc:+.5f})")
        issues_found = True
    
    # Level checks
    if dbfs(L_peak) > -1.0:
        print(f"⚠️  Left channel peaks near 0dBFS ({dbfs(L_peak):.1f}dBFS) - likely clipping")
        issues_found = True
    if dbfs(R_peak) > -1.0:
        print(f"⚠️  Right channel peaks near 0dBFS ({dbfs(R_peak):.1f}dBFS) - likely clipping")
        issues_found = True
        
    if dbfs(L_rms) > -12.0:
        print(f"⚠️  Left channel RMS too high ({dbfs(L_rms):.1f}dBFS) - overdriven")
        issues_found = True
    if dbfs(R_rms) > -12.0:
        print(f"⚠️  Right channel RMS too high ({dbfs(R_rms):.1f}dBFS) - overdriven")
        issues_found = True
        
    if dbfs(L_rms) < -40.0:
        print(f"⚠️  Left channel RMS very low ({dbfs(L_rms):.1f}dBFS) - weak signal")
        issues_found = True
    if dbfs(R_rms) < -40.0:
        print(f"⚠️  Right channel RMS very low ({dbfs(R_rms):.1f}dBFS) - weak signal")
        issues_found = True

    # Noise floor checks
    if L_floor > -50.0:
        print(f"⚠️  Left channel noise floor too high ({L_floor:.1f}dBFS)")
        issues_found = True
    if R_floor > -50.0:
        print(f"⚠️  Right channel noise floor too high ({R_floor:.1f}dBFS)")
        issues_found = True
        
    # Dynamic range checks
    if L_dr < 20.0:
        print(f"⚠️  Left channel low dynamic range ({L_dr:.1f}dB) - may be over-compressed")
        issues_found = True
    if R_dr < 20.0:
        print(f"⚠️  Right channel low dynamic range ({R_dr:.1f}dB) - may be over-compressed")
        issues_found = True
        
    # Clipping checks
    if L_clip_pct > 0.01:
        print(f"⚠️  Left channel has {L_clip_pct:.3f}% clipped samples")
        issues_found = True
    if R_clip_pct > 0.01:
        print(f"⚠️  Right channel has {R_clip_pct:.3f}% clipped samples")
        issues_found = True
        
    # Correlation checks
    if abs(corr_coeff) > 0.8:
        print(f"⚠️  High channel correlation ({corr_coeff:+.3f}) - possible echo/crosstalk")
        issues_found = True
        
    if not issues_found:
        print("✅ No major quality issues detected")
    
    print()
    print("-- RECOMMENDATIONS --")
    if sr != 48000:
        print("• Configure recording to use 48kHz sample rate")
    if abs(L_dc) > 0.01 or abs(R_dc) > 0.01:
        print("• Enable DC removal/high-pass filtering at 80-120Hz")
    if L_floor > -50.0 or R_floor > -50.0:
        print("• Enable noise suppression and consider noise gating")
    if L_clip_pct > 0.01 or R_clip_pct > 0.01:
        print("• Enable limiter/compressor to prevent clipping")
        print("• Reduce input gain or enable AGC")
    if abs(corr_coeff) > 0.8:
        print("• Improve echo cancellation settings")
        print("• Check microphone placement and speaker levels")

if __name__ == '__main__':
    main()
