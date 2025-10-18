using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using CallRecorder.Core.Config;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace CallRecorder.Service.Recording;

internal static class WavPostProcessor
{
    // Two-pass post-normalization:
    // Pass 1: scan entire file to compute per-channel RMS and peak
    // Pass 2: apply per-channel gain limited by headroom to limiter ceiling, write to temp, then replace original
    public static async Task NormalizeAsync(string inputPath, AudioDspConfig cfg, ILogger logger)
    {
        await Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            {
                logger.LogWarning("NormalizeAsync: file not found: {path}", inputPath);
                return;
            }

            using var reader = new WaveFileReader(inputPath);
            var wf = reader.WaveFormat;
            int channels = Math.Max(1, wf.Channels);

            // First pass: compute integrated RMS and true peak per channel
            double[] sumSq = new double[channels];
            float[] peak = new float[channels];
            long totalFrames = 0;

            using (var spReader = new WaveFileReader(inputPath))
            {
                var sp = spReader.ToSampleProvider();
                int framesPerBlock = 4096;
                int blockSamples = framesPerBlock * channels;
                float[] buf = new float[blockSamples];

                int read;
                while ((read = sp.Read(buf, 0, buf.Length)) > 0)
                {
                    int frames = read / channels;
                    for (int i = 0; i < frames; i++)
                    {
                        for (int c = 0; c < channels; c++)
                        {
                            float v = buf[i * channels + c];
                            sumSq[c] += v * v;
                            float a = MathF.Abs(v);
                            if (a > peak[c]) peak[c] = a;
                        }
                    }
                    totalFrames += frames;
                }
            }

            const float eps = 1e-6f;
            float targetDb = cfg.TargetRmsDbfs;
            float maxGainDb = cfg.MaxGainDb;
            float ceilingLin = DbToLin(cfg.LimiterCeilingDbfs);

            float[] applyGainLin = new float[channels];
            for (int c = 0; c < channels; c++)
            {
                float rms = (float)Math.Sqrt(sumSq[c] / Math.Max(1, totalFrames));
                float curDb = 20f * (float)Math.Log10(Math.Max(eps, rms));
                float needDb = Clamp(targetDb - curDb, 0f, maxGainDb);

                // Headroom limit to ensure peaks stay under ceiling
                float headroomDb = 20f * (float)Math.Log10(ceilingLin / Math.Max(eps, peak[c]));
                if (float.IsInfinity(headroomDb) || float.IsNaN(headroomDb)) headroomDb = maxGainDb;

                float finalDb = MathF.Min(needDb, headroomDb);
                applyGainLin[c] = DbToLin(finalDb);
            }

            // Second pass: apply gain + limiter + soft clip, write to temp WAV with same format
            string dir = Path.GetDirectoryName(inputPath) ?? ".";
            string name = Path.GetFileNameWithoutExtension(inputPath) ?? "recording";
            string ext = Path.GetExtension(inputPath);
            string tmpPath = Path.Combine(dir, $"{name}-norm.tmp");

            using (var spReader2 = new WaveFileReader(inputPath))
            {
                var sp2 = spReader2.ToSampleProvider();
                WaveFormat outFmt = (spReader2.WaveFormat.Encoding == WaveFormatEncoding.Pcm && spReader2.WaveFormat.BitsPerSample == 16)
                    ? new WaveFormat(spReader2.WaveFormat.SampleRate, 16, channels)
                    : WaveFormat.CreateIeeeFloatWaveFormat(spReader2.WaveFormat.SampleRate, channels);

                using var writer = new WaveFileWriter(tmpPath, outFmt);

                int framesPerBlock = 4096;
                int blockSamples = framesPerBlock * channels;
                float[] buf = new float[blockSamples];

                int read;
                while ((read = sp2.Read(buf, 0, buf.Length)) > 0)
                {
                    int frames = read / channels;

                    // Apply per-channel gain and limiter
                    for (int i = 0; i < frames; i++)
                    {
                        for (int c = 0; c < channels; c++)
                        {
                            int idx = i * channels + c;
                            float v = buf[idx] * applyGainLin[c];

                            // Hard limiter to ceiling
                            if (v > ceilingLin) v = ceilingLin;
                            else if (v < -ceilingLin) v = -ceilingLin;

                            // Soft clip safeguard to [-1,1]
                            v = SoftClip(v);

                            buf[idx] = v;
                        }
                    }

                    if (outFmt.Encoding == WaveFormatEncoding.Pcm)
                    {
                        // Convert float [-1,1] to 16-bit PCM
                        int bytesToWrite = frames * channels * 2;
                        byte[] bytes = new byte[bytesToWrite];
                        int bi = 0;
                        for (int i = 0; i < frames; i++)
                        {
                            for (int c = 0; c < channels; c++)
                            {
                                float f = buf[i * channels + c];
                                if (f > 1f) f = 1f;
                                else if (f < -1f) f = -1f;
                                short s = (short)Math.Round(f * short.MaxValue);
                                bytes[bi++] = (byte)(s & 0xFF);
                                bytes[bi++] = (byte)((s >> 8) & 0xFF);
                            }
                        }
                        writer.Write(bytes, 0, bi);
                    }
                    else
                    {
                        // Float32 direct
                        writer.WriteSamples(buf, 0, frames * channels);
                    }
                }

                writer.Flush();
            }

            try
            {
                File.Move(tmpPath, inputPath, true);
                logger.LogInformation("Post-normalization complete: {path}", inputPath);
            }
            catch (Exception moveEx)
            {
                logger.LogWarning(moveEx, "Failed to replace original file with normalized output. Keeping original file.");
                try { File.Delete(tmpPath); } catch { /* ignore */ }
            }
        });
    }

    /// <summary>
    /// Merge multiple WAV segments (same format) into a single output file.
    /// Writes to a temp file then replaces the output atomically when possible.
    /// </summary>
    public static async Task MergeSegmentsAsync(IEnumerable<string> segments, string outputPath, ILogger logger)
    {
        await Task.Run(() =>
        {
            if (segments == null) throw new ArgumentNullException(nameof(segments));
            var segList = new List<string>(segments);
            if (segList.Count == 0)
            {
                logger.LogWarning("MergeSegmentsAsync: no segments provided");
                return;
            }
            for (int i = 0; i < segList.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(segList[i]) || !File.Exists(segList[i]))
                {
                    logger.LogWarning("MergeSegmentsAsync: missing segment: {path}", segList[i]);
                }
            }

            // Use first segment format as target format
            using var firstReader = new WaveFileReader(segList[0]);
            var targetFormat = firstReader.WaveFormat;

            string dir = Path.GetDirectoryName(outputPath) ?? ".";
            Directory.CreateDirectory(dir);
            string tmpPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(outputPath) + ".merge.tmp");

            // Write merged content
            using (var writer = new WaveFileWriter(tmpPath, targetFormat))
            {
                foreach (var seg in segList)
                {
                    if (string.IsNullOrWhiteSpace(seg) || !File.Exists(seg)) continue;

                    using var reader = new WaveFileReader(seg);
                    if (!reader.WaveFormat.Equals(targetFormat))
                    {
                        throw new NotSupportedException($"Segment format mismatch: {seg}");
                    }

                    byte[] buffer = new byte[64 * 1024];
                    int bytesRead;
                    while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        writer.Write(buffer, 0, bytesRead);
                    }
                }
                writer.Flush();
            }

            try
            {
                // Replace or move temp into final output
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
                File.Move(tmpPath, outputPath);
                logger.LogInformation("Merged {count} segments into {out}", segList.Count, outputPath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to finalize merged output");
                try { File.Delete(tmpPath); } catch { /* ignore */ }
            }

            // Cleanup extra segment files (keep the merged output only)
            foreach (var seg in segList)
            {
                try
                {
                    if (!string.Equals(seg, outputPath, StringComparison.OrdinalIgnoreCase) && File.Exists(seg))
                    {
                        File.Delete(seg);
                    }
                }
                catch { /* ignore */ }
            }
        });
    }

    private static float DbToLin(float db) => (float)Math.Pow(10.0, db / 20.0);
    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

    private static float SoftClip(float x)
    {
        const float k = 1.5f;
        const float norm = 1f / 0.9051482536f; // 1/tanh(1.5)
        return (float)Math.Tanh(k * x) * norm;
    }
}
