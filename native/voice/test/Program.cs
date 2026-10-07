// Parity harness for the voice plugin. Runs the same on macOS (libAudioPluginDissonance.dylib) and Windows
// (the game's AudioPluginDissonance.dll), so the two can be diffed sample for sample.
//
//   test                 sanity checks, then writes parity-<os>.bin
//   test compare A B     compares two parity files
//   test apm TAG NS AGC  WebRTC chain only, NS level (-1 = off) and AGC (0/1), writes apm-<os>-TAG.bin
//   test compare-apm A B compares two apm files
//
// Drives the plugin exactly like DissonanceVoip with Lethal Company's VoiceSettings asset (noise suppression
// Moderate, VAD sensitivity Medium, AEC/AECM off, RNNoise off), using declarations copied from
// AudioPluginDissonanceNative.cs. RNNoise gets its own pass too, though Lethal Company leaves it disabled.
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

static class N {
    const string L = "AudioPluginDissonance";
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr Dissonance_CreateRnnoiseState();
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern void Dissonance_DestroyRnnoiseState(IntPtr state);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern bool Dissonance_RnnoiseProcessFrame(IntPtr state, int count, int sampleRate, float[] input, float[] output);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int Dissonance_RnnoiseGetGains(IntPtr state, float[] output, int length);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr Dissonance_CreatePreprocessor(int nsLevel, int aecLevel, bool aecDelayAgnostic, bool aecExtended, bool aecRefined, int aecmRoutingMode, bool aecmComfortNoise);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern void Dissonance_DestroyPreprocessor(IntPtr handle);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern void Dissonance_ConfigureVadSensitivity(IntPtr handle, int level);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern bool Dissonance_GetVadSpeechState(IntPtr handle);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int Dissonance_PreprocessCaptureFrame(IntPtr handle, int sampleRate, float[] input, float[] output, int streamDelay);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern bool Dissonance_PreprocessorExchangeInstance(IntPtr previous, IntPtr replacement);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int Dissonance_GetFilterState();
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern void Dissonance_SetAgcIsOutputMutedState(IntPtr handle, bool isMuted);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern void Dissonance_GetAecMetrics(float[] buffer, int length);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern void Dissonance_EnableAgc(IntPtr handle, bool enable);
}

// Copied from Dissonance's OpusNative.cs (opus.dll on Windows, libopus.dylib on macOS).
static class O {
    const string L = "opus";
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr opus_encoder_create(int samplingRate, int channels, int application, out int error);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_encode_float(IntPtr st, float[] pcm, int frameSize, byte[] data, int maxDataBytes);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern void opus_encoder_destroy(IntPtr st);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr opus_decoder_create(int samplingRate, int channels, out int error);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_decode_float(IntPtr st, byte[] data, int len, float[] pcm, int frameSize, bool decodeFec);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern void opus_decoder_destroy(IntPtr st);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int dissonance_opus_encoder_ctl_in(IntPtr st, int request, int value);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int dissonance_opus_encoder_ctl_out(IntPtr st, int request, out int value);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr opus_get_version_string();
}

static class Program {
    const int Frame = 480, Rate = 48000;

    static void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) Environment.ExitCode = 1; }

    static float[] ReadWavF32(string path) {
        var b = File.ReadAllBytes(path);
        for (int i = 12; i + 8 <= b.Length; ) {
            int len = BitConverter.ToInt32(b, i + 4);
            if (b[i] == 'd' && b[i + 1] == 'a' && b[i + 2] == 't' && b[i + 3] == 'a') {
                var f = new float[len / 4]; Buffer.BlockCopy(b, i + 8, f, 0, f.Length * 4); return f;
            }
            i += 8 + len + (len & 1);
        }
        throw new InvalidDataException("no data chunk");
    }

    // Deterministic input: 1 s silence, speech, the same speech at -20 dB (exercises AGC), speech over
    // brown noise (exercises noise suppression), 1 s noise alone, 1 s silence.
    static float[] Input() {
        var speech = ReadWavF32("speech.wav");
        var rng = new Random(1234); double brown = 0;
        float Noise() => (float)((brown = 0.98 * brown + 0.2 * (rng.NextDouble() * 2 - 1)) * 0.02);
        var parts = new[] {
            new float[Rate],
            speech,
            speech.Select(s => s * 0.1f).ToArray(),
            speech.Select(s => s + Noise()).ToArray(),
            Enumerable.Range(0, Rate).Select(_ => Noise()).ToArray(),
            new float[Rate],
        };
        var all = parts.SelectMany(p => p).ToArray();
        return all.Take(all.Length / Frame * Frame).ToArray();
    }

    static string Os => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "macos";

    // The WebRTC chain exactly as Dissonance drives it, with optional NS/AGC overrides for isolating differences.
    static (float[] output, bool[] vad) RunApm(float[] input, int nsLevel, bool agc) {
        int frames = input.Length / Frame;
        var output = new float[input.Length]; var vad = new bool[frames];
        var pp = N.Dissonance_CreatePreprocessor(nsLevel, -1, true, true, true, -1, true);
        N.Dissonance_ConfigureVadSensitivity(pp, 1);
        if (!agc) N.Dissonance_EnableAgc(pp, false);
        var frame = new float[Frame]; var outFrame = new float[Frame];
        for (int f = 0; f < frames; f++) {
            Array.Copy(input, f * Frame, frame, 0, Frame);
            N.Dissonance_SetAgcIsOutputMutedState(pp, false);
            if (N.Dissonance_PreprocessCaptureFrame(pp, Rate, frame, outFrame, 40) != 0) throw new Exception($"preprocessor error at frame {f}");
            vad[f] = N.Dissonance_GetVadSpeechState(pp);
            Array.Copy(outFrame, 0, output, f * Frame, Frame);
        }
        N.Dissonance_DestroyPreprocessor(pp);
        return (output, vad);
    }

    static int Main(string[] args) {
        if (args.Length == 3 && args[0] == "compare") return Compare(args[1], args[2]);
        if (args.Length == 3 && args[0] == "compare-apm") {
            (float[] o, bool[] v) Load(string p) {
                using var r = new BinaryReader(File.OpenRead(p)); int n = r.ReadInt32();
                var o = new float[n * Frame]; for (int i = 0; i < o.Length; i++) o[i] = r.ReadSingle();
                var v = new bool[n]; for (int i = 0; i < n; i++) v[i] = r.ReadBoolean(); return (o, v);
            }
            var (xo, xv) = Load(args[1]); var (yo, yv) = Load(args[2]);
            DiffApm(xo, yo);
            Console.WriteLine($"{"VAD",-10} agree {xv.Zip(yv).Count(t => t.First == t.Second)}/{xv.Length} frames");
            return 0;
        }
        if (args.Length == 4 && args[0] == "apm") {
            var (o, v) = RunApm(Input(), int.Parse(args[2]), args[3] == "1");
            using var w = new BinaryWriter(File.Create($"apm-{Os}-{args[1]}.bin"));
            w.Write(v.Length); foreach (var x in o) w.Write(x); foreach (var b in v) w.Write(b);
            Console.WriteLine($"wrote apm-{Os}-{args[1]}.bin");
            return 0;
        }

        var input = Input();
        int frames = input.Length / Frame;
        var apmOut = new float[input.Length]; var vad = new bool[frames]; var rnOut = new float[input.Length];

        var pp = N.Dissonance_CreatePreprocessor(1, -1, true, true, true, -1, true);   // Lethal Company's VoiceSettings
        Check(pp != IntPtr.Zero, "create preprocessor with Lethal Company's settings");
        N.Dissonance_ConfigureVadSensitivity(pp, 1);
        Check(N.Dissonance_PreprocessorExchangeInstance(IntPtr.Zero, pp), "exchange: install instance");
        Check(!N.Dissonance_PreprocessorExchangeInstance(IntPtr.Zero, pp), "exchange: refuses stale 'previous'");
        Check(N.Dissonance_GetFilterState() == 0, "filter state = FilterNotRunning (no AEC mixer effect)");

        var frame = new float[Frame]; var outFrame = new float[Frame];
        for (int f = 0; f < frames; f++) {
            Array.Copy(input, f * Frame, frame, 0, Frame);
            N.Dissonance_SetAgcIsOutputMutedState(pp, false);
            int err = N.Dissonance_PreprocessCaptureFrame(pp, Rate, frame, outFrame, 40);
            if (err != 0) { Check(false, $"preprocessor error {err} at frame {f}"); return 1; }
            vad[f] = N.Dissonance_GetVadSpeechState(pp);
            Array.Copy(outFrame, 0, apmOut, f * Frame, Frame);
        }
        var metrics = new float[10]; N.Dissonance_GetAecMetrics(metrics, metrics.Length);
        Check(N.Dissonance_PreprocessorExchangeInstance(pp, IntPtr.Zero), "exchange: uninstall instance");
        N.Dissonance_DestroyPreprocessor(pp);

        var rn = N.Dissonance_CreateRnnoiseState(); var gains = new float[22];
        for (int f = 0; f < frames; f++) {
            Array.Copy(input, f * Frame, frame, 0, Frame);
            if (!N.Dissonance_RnnoiseProcessFrame(rn, Frame, Rate, frame, outFrame)) { Check(false, "rnnoise returned false"); return 1; }
            Array.Copy(outFrame, 0, rnOut, f * Frame, Frame);
        }
        int gainCount = N.Dissonance_RnnoiseGetGains(rn, gains, 64);
        Check(N.Dissonance_RnnoiseProcessFrame(rn, 960, Rate, new float[960], new float[960]), "rnnoise wrong frame size reads as true (Windows bool/BOOL quirk)");
        N.Dissonance_DestroyRnnoiseState(rn);

        // Sanity: the chain does what Windows' chain does in broad strokes.
        int speechStart = Rate / Frame, speechFrames = ReadWavF32("speech.wav").Length / Frame;
        var voiced = Enumerable.Range(speechStart, speechFrames).Where(f => Energy(input, f) > 1e-4).ToArray();
        double vadSpeech = voiced.Count(f => vad[f]) / (double)voiced.Length;
        double vadSilence = Enumerable.Range(0, speechStart).Count(f => vad[f]) / (double)speechStart;
        Console.WriteLine($"      VAD on speech {vadSpeech:P0}, on leading silence {vadSilence:P0}");
        Check(vadSpeech > 0.6, "VAD fires on speech");
        Check(vadSilence < 0.05, "VAD quiet on silence");
        int noiseStart = frames - 2 * Rate / Frame;
        double noiseIn = Enumerable.Range(noiseStart, Rate / Frame).Sum(f => Energy(input, f)), noiseOut = Enumerable.Range(noiseStart, Rate / Frame).Sum(f => Energy(apmOut, f));
        Console.WriteLine($"      WebRTC NS (Moderate) + AGC on noise-only: {10 * Math.Log10(noiseOut / noiseIn):F1} dB");
        Check(apmOut.All(float.IsFinite) && rnOut.All(float.IsFinite), "outputs finite");
        Check(gainCount == 22, "RNNoise gains clamp to 22 bands");

        // Opus as Dissonance configures it for Lethal Company: 48 kHz mono VOIP, FEC on, 17 kbps (Medium),
        // 960-sample (Small) frames. Encodes the raw input (identical on both OSes, so Opus is compared on its
        // own), decodes it, then one lost packet (PLC) and one FEC recovery so those paths get compared too.
        Console.WriteLine($"      opus: {Marshal.PtrToStringAnsi(O.opus_get_version_string())}");
        const int OpusFrame = 960, SetBitrate = 4002, GetBitrate = 4003, SetInbandFec = 4012, SetPacketLoss = 4014;
        var enc = O.opus_encoder_create(Rate, 1, 2048, out int encErr);
        var dec = O.opus_decoder_create(Rate, 1, out int decErr);
        Check(encErr == 0 && decErr == 0, "opus encoder/decoder created");
        O.dissonance_opus_encoder_ctl_in(enc, SetInbandFec, 1);
        O.dissonance_opus_encoder_ctl_in(enc, SetBitrate, 17000);
        O.dissonance_opus_encoder_ctl_in(enc, SetPacketLoss, 10);
        O.dissonance_opus_encoder_ctl_out(enc, GetBitrate, out int bitrate);
        Check(bitrate == 17000, "opus ctl wrappers set/get bitrate");
        int opusFrames = input.Length / OpusFrame;
        var packets = new byte[opusFrames][]; var decoded = new float[(opusFrames + 2) * OpusFrame];
        var pcm = new float[OpusFrame]; var buf = new byte[4000]; var outPcm = new float[OpusFrame];
        for (int f = 0; f < opusFrames; f++) {
            Array.Copy(input, f * OpusFrame, pcm, 0, OpusFrame);
            int len = O.opus_encode_float(enc, pcm, OpusFrame, buf, buf.Length);
            if (len <= 0) { Check(false, $"opus encode error {len}"); return 1; }
            packets[f] = buf.Take(len).ToArray();
            O.opus_decode_float(dec, packets[f], len, outPcm, OpusFrame, false);
            Array.Copy(outPcm, 0, decoded, f * OpusFrame, OpusFrame);
        }
        O.opus_decode_float(dec, null, 0, outPcm, OpusFrame, false);                         // packet loss -> PLC
        Array.Copy(outPcm, 0, decoded, opusFrames * OpusFrame, OpusFrame);
        O.opus_decode_float(dec, packets[^1], packets[^1].Length, outPcm, OpusFrame, true);  // FEC recovery
        Array.Copy(outPcm, 0, decoded, (opusFrames + 1) * OpusFrame, OpusFrame);
        O.opus_encoder_destroy(enc); O.opus_decoder_destroy(dec);
        Check(decoded.All(float.IsFinite), "opus decode finite");

        var path = $"parity-{Os}.bin";
        using (var w = new BinaryWriter(File.Create(path))) {
            w.Write(frames);
            foreach (var x in apmOut) w.Write(x);
            foreach (var v in vad) w.Write(v);
            foreach (var x in rnOut) w.Write(x);
            foreach (var g in gains) w.Write(g);
            foreach (var m in metrics) w.Write(m);
            w.Write(opusFrames);
            foreach (var p in packets) { w.Write(p.Length); w.Write(p); }
            foreach (var x in decoded) w.Write(x);
        }
        Console.WriteLine($"      wrote {path} ({RuntimeInformation.ProcessArchitecture})");
        return Environment.ExitCode;
    }

    static void Diff(string name, float[] p, float[] q) {
        int identical = p.Zip(q).Count(t => BitConverter.SingleToInt32Bits(t.First) == BitConverter.SingleToInt32Bits(t.Second));
        double maxAbs = p.Zip(q).Max(t => Math.Abs(t.First - t.Second));
        double sig = p.Sum(v => (double)v * v), err = p.Zip(q).Sum(t => (double)(t.First - t.Second) * (t.First - t.Second));
        string snr = err == 0 ? "inf" : $"{10 * Math.Log10(sig / err):F1}";
        Console.WriteLine($"{name,-10} bit-identical {identical}/{p.Length} ({identical / (double)p.Length:P2})  max|diff| {maxAbs:E2}  SNR {snr} dB");
    }

    // Where WebRTC output first diverges, and whether the divergence is loudness-level or just waveform detail.
    static void DiffApm(float[] xa, float[] ya) {
        Diff("WebRTC", xa, ya);
        // Is the second output just the first shifted in time? Best-matching lag over +/-10 ms of speech.
        int from = Rate, span = 4 * Rate;
        double SnrAt(int lag) {
            double sig = 0, err = 0;
            for (int i = from; i < from + span; i++) { double a = xa[i], b = ya[i + lag]; sig += a * a; err += (a - b) * (a - b); }
            return 10 * Math.Log10(sig / Math.Max(err, 1e-30));
        }
        // Gain vs waveform: per 10 ms frame of the first speech segment, correlation and RMS ratio (second/first).
        var stats = Enumerable.Range(from / Frame, span / Frame).Select(f => {
            double sxy = 0, sxx = 0, syy = 0;
            for (int i = f * Frame; i < (f + 1) * Frame; i++) { sxy += (double)xa[i] * ya[i]; sxx += (double)xa[i] * xa[i]; syy += (double)ya[i] * ya[i]; }
            return (f, corr: sxx > 1e-9 && syy > 1e-9 ? sxy / Math.Sqrt(sxx * syy) : double.NaN, gainDb: 10 * Math.Log10((syy + 1e-20) / (sxx + 1e-20)), loud: sxx > 1e-6);
        }).Where(t => t.loud && !double.IsNaN(t.corr)).ToArray();
        var corrs = stats.Select(t => t.corr).OrderBy(v => v).ToArray();
        Console.WriteLine($"{"",-10} speech frames: correlation median {corrs[corrs.Length / 2]:F4} p5 {corrs[corrs.Length / 20]:F4}; gain(2nd/1st) dB at 0.5 s steps: " +
            string.Join(" ", Enumerable.Range(0, 8).Select(k => stats.FirstOrDefault(t => t.f >= from / Frame + k * 50)).Where(t => t.f > 0).Select(t => t.gainDb.ToString("+0.00;-0.00"))));
        int best = Enumerable.Range(-480, 961).OrderByDescending(SnrAt).First();
        Console.WriteLine($"{"",-10} best alignment: second output shifted by {best} samples -> SNR {SnrAt(best):F1} dB (vs {SnrAt(0):F1} dB unshifted)");
        int first = Enumerable.Range(0, xa.Length).FirstOrDefault(i => BitConverter.SingleToInt32Bits(xa[i]) != BitConverter.SingleToInt32Bits(ya[i]), -1);
        Console.WriteLine($"{"",-10} first differing sample {first} (frame {first / Frame}, {first / (double)Rate:F3} s): {(first >= 0 ? $"{xa[first]:E3} vs {ya[first]:E3}" : "-")}");
        int speechLen = ReadWavF32("speech.wav").Length;
        var segments = new (string name, int len)[] { ("silence", Rate), ("speech", speechLen), ("quiet speech", speechLen), ("speech+noise", speechLen), ("noise", Rate), ("silence", Rate) };
        int at = 0;
        foreach (var (name, len) in segments) {
            int n = Math.Min(len, xa.Length - at); if (n <= 0) break;
            double sig = 0, err = 0; for (int i = at; i < at + n; i++) { sig += (double)xa[i] * xa[i]; err += (double)(xa[i] - ya[i]) * (xa[i] - ya[i]); }
            var dbDiff = Enumerable.Range(at / Frame, n / Frame).Select(f => {
                double ex = 0, ey = 0; for (int i = f * Frame; i < (f + 1) * Frame; i++) { ex += (double)xa[i] * xa[i]; ey += (double)ya[i] * ya[i]; }
                return (ex: 10 * Math.Log10(ex / Frame + 1e-12), ey: 10 * Math.Log10(ey / Frame + 1e-12));
            }).Where(t => Math.Max(t.ex, t.ey) > -60).Select(t => Math.Abs(t.ex - t.ey)).OrderBy(v => v).ToArray();
            string lv = dbDiff.Length == 0 ? "all frames below -60 dBFS" : $"frame level |dB diff| median {dbDiff[dbDiff.Length / 2]:F2} p95 {dbDiff[(int)(dbDiff.Length * 0.95)]:F2} max {dbDiff[^1]:F2}";
            Console.WriteLine($"{"",-10} {name,-13} SNR {(err == 0 ? "inf" : (10 * Math.Log10(sig / err)).ToString("F1"))} dB, {lv}");
            at += n;
        }
    }

    static double Energy(float[] x, int frame) { double e = 0; for (int i = frame * Frame; i < (frame + 1) * Frame; i++) e += (double)x[i] * x[i]; return e; }

    static int Compare(string a, string b) {
        (float[] apm, bool[] vad, float[] rn, float[] gains, float[] metrics, byte[][] packets, float[] decoded) Load(string p) {
            using var r = new BinaryReader(File.OpenRead(p));
            int frames = r.ReadInt32(), n = frames * Frame;
            var apm = new float[n]; for (int i = 0; i < n; i++) apm[i] = r.ReadSingle();
            var vad = new bool[frames]; for (int i = 0; i < frames; i++) vad[i] = r.ReadBoolean();
            var rn = new float[n]; for (int i = 0; i < n; i++) rn[i] = r.ReadSingle();
            var g = new float[22]; for (int i = 0; i < 22; i++) g[i] = r.ReadSingle();
            var m = new float[10]; for (int i = 0; i < 10; i++) m[i] = r.ReadSingle();
            int of = r.ReadInt32();
            var packets = new byte[of][]; for (int i = 0; i < of; i++) packets[i] = r.ReadBytes(r.ReadInt32());
            var dec = new float[(of + 2) * 960]; for (int i = 0; i < dec.Length; i++) dec[i] = r.ReadSingle();
            return (apm, vad, rn, g, m, packets, dec);
        }
        var x = Load(a); var y = Load(b);
        DiffApm(x.apm, y.apm);
        Console.WriteLine($"{"VAD",-10} agree {x.vad.Zip(y.vad).Count(t => t.First == t.Second)}/{x.vad.Length} frames");
        Diff("RNNoise", x.rn, y.rn);
        Diff("gains", x.gains, y.gains);
        Console.WriteLine($"{"metrics",-10} {string.Join(" ", x.metrics)}  |  {string.Join(" ", y.metrics)}");
        int samePackets = x.packets.Zip(y.packets).Count(t => t.First.AsSpan().SequenceEqual(t.Second));
        Console.WriteLine($"{"opus pkts",-10} byte-identical {samePackets}/{x.packets.Length}  bytes {x.packets.Sum(p => p.Length)} vs {y.packets.Sum(p => p.Length)}");
        Diff("opus pcm", x.decoded, y.decoded);
        return 0;
    }
}
