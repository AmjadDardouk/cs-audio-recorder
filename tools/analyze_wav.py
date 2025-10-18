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
        # Unpack as little-endian 32-bit
        vals = struct.unpack('<' + 'f'*count, frames[:count*4])
        # Heuristic: if absurd magnitudes, treat as int32
        maxabs = max(abs(v) for v in vals) if vals else 0.0
        if maxabs > 10.0:
            # reinterpret as int32
            vals_i32 = struct.unpack('<' + 'i'*count, frames[:count*4])
            norm = 2147483648.0
            vals = [vi / norm for vi in vals_i32]
        # De-interleave
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

def downsample(arr, step):
    if step <= 1:
        return arr
    return arr[0::step]

def truncate_same_len(a, b):
    n = min(len(a), len(b))
    return a[:n], b[:n]

def corr_at_lag(a, b, lag):
    # lag > 0: b leads a by lag samples (b shifted right)
    if lag == 0:
        a2, b2 = truncate_same_len(a, b)
    elif lag > 0:
        a2 = a[lag:]
        b2 = b[:len(a2)]
    else:
        b2 = b[-lag:]
        a2 = a[:len(b2)]
    if not a2 or not b2:
        return 0.0
    # normalized cross-correlation
    ma = mean(a2)
    mb = mean(b2)
    num = 0.0
    da = 0.0
    db = 0.0
    for x,y in zip(a2,b2):
        dx = x - ma
        dy = y - mb
        num += dx*dy
        da += dx*dx
        db += dy*dy
    if da <= 0.0 or db <= 0.0:
        return 0.0
    return num / math.sqrt(da*db)

def best_lag(a, b, sr, max_ms=200, step=1):
    maxlag = int(sr * max_ms / 1000.0)
    best = 0
    bestc = -2.0
    for lag in range(-maxlag, maxlag+1, step):
        c = corr_at_lag(a,b,lag)
        if c > bestc:
            bestc = c
            best = lag
    return best, bestc

def leakage_db(target, reference):
    # Estimate linear leakage gain by least squares: target ≈ g * reference
    # Compute g = sum(r*t) / sum(r*r)
    rr = 0.0
    rt = 0.0
    n = min(len(target), len(reference))
    if n == 0:
        return None, None
    for i in range(n):
        r = reference[i]
        t = target[i]
        rr += r*r
        rt += r*t
    if rr <= 0.0:
        return 0.0, 0.0
    g = rt / rr
    # Leakage level relative to target RMS
    # Return gain and its dB: 20log10(|g|)
    db = -999.0
    if abs(g) > 0.0:
        db = 20.0*math.log10(abs(g))
    return g, db

def noise_floor_db(arr, sr, win_ms=50, percentile=0.2):
    # Estimate noise floor as RMS of the quietest percentile of short windows
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

def main():
    if len(sys.argv) < 2:
        print("Usage: python tools/analyze_wav.py <path-to-stereo-wav>")
        sys.exit(1)
    path = sys.argv[1]
    if not os.path.exists(path):
        print(f"File not found: {path}")
        sys.exit(2)

    sr, L, R = read_wav_stereo(path)

    # Basic metrics
    L_rms = rms(L); R_rms = rms(R)
    L_peak = peak(L); R_peak = peak(R)
    L_dc = mean(L); R_dc = mean(R)

    # Downsample for correlation computations
    step = max(1, sr // 8000)  # target ~8kHz for analysis
    Ld = downsample(L, step); Rd = downsample(R, step)
    # Trim to same length
    Ld, Rd = truncate_same_len(Ld, Rd)

    # Best lag (positive means R leads L)
    lag, corr = best_lag(Ld, Rd, sr//step, max_ms=200, step=1)

    # Leakage estimates (how much R appears in L, and L in R)
    g_RtoL, gdb_RtoL = leakage_db(Ld, Rd)
    g_LtoR, gdb_LtoR = leakage_db(Rd, Ld)

    # Noise floor estimates
    L_floor = noise_floor_db(L, sr, win_ms=50, percentile=0.2)
    R_floor = noise_floor_db(R, sr, win_ms=50, percentile=0.2)

    print("==== WAV Analysis Report ====")
    print(f"File: {path}")
    print(f"Sample rate: {sr} Hz, Length: {len(L)/sr:.2f} s")
    print("-- Levels (dBFS) --")
    print(f"L (mic)  RMS: {dbfs(L_rms):6.1f} dBFS   Peak: {dbfs(L_peak):6.1f} dBFS   DC offset: {L_dc:+.5f}")
    print(f"R (spkr) RMS: {dbfs(R_rms):6.1f} dBFS   Peak: {dbfs(R_peak):6.1f} dBFS   DC offset: {R_dc:+.5f}")
    print(f"Estimated noise floor:  L {L_floor:6.1f} dBFS   R {R_floor:6.1f} dBFS")

    print("-- Crosstalk / Echo --")
    print(f"Best lag (R->L): {lag} samples at {sr//step} Hz (~{lag/(sr/step)*1000:.1f} ms), corr={corr:.3f}")
    if g_RtoL is not None:
        print(f"Leakage R->L gain: {g_RtoL:+.6f}  ({gdb_RtoL:.1f} dB)")
    if g_LtoR is not None:
        print(f"Leakage L->R gain: {g_LtoR:+.6f}  ({gdb_LtoR:.1f} dB)")

    # Simple quality hints
    print("-- Hints --")
    if abs(L_dc) > 0.01 or abs(R_dc) > 0.01:
        print("• Non-trivial DC offset detected; consider high-pass filtering at ~80–120 Hz.")
    if L_floor > -50.0:
        print("• Mic noise floor is relatively high; enable NoiseSuppression=High and consider a soft gate.")
    if gdb_RtoL is not None and gdb_RtoL > -20.0:
        print("• Significant R->L leakage; improve echo cancellation (WebRTC AEC) or reduce speaker volume / use headset.")
    if abs(corr) > 0.3 and abs(lag) > 0:
        print("• Measurable echo correlation with delay; adding delay alignment prior to AEC may help.")

if __name__ == '__main__':
    main()
