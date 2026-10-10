using System;
using System.Collections.Generic;
using Godot;

// PRODUCTION - Arena.Client
// 程序化音效合成（零外部音频素材）。
// 结论依据 docs/audio/sfx-spec-v1.md：以**包络与层次**取胜，而非复杂音色——
//   打击类 = 瞬态噪声 + 低频体感；金属类 = 两个高 Q 正弦拍频；位移类 = 频率扫描。
// 同类音效带**随机微扰**（音高/包络），避免连击时"机关枪式重复"（业余感的根源）。
// 纯表现层：不参与 Sim、不影响确定性。
namespace Arena.Client.Audio;

public enum Sfx
{
    Swing, HitLight, HitMedium, HitHeavy, Guard, Parry,
    Launch, Down, Death, MatchEnd,
    UiMove, UiConfirm, UiCancel,
}

public static class SfxSynth
{
    private const int Rate = 22050;
    private static readonly Dictionary<Sfx, AudioStreamWav> Cache = new();

    /// 取（并缓存）某音效的波形。随机微扰由播放侧通过 PitchScale 施加。
    public static AudioStreamWav Get(Sfx id)
    {
        if (Cache.TryGetValue(id, out var cached)) return cached;
        var wav = Build(id);
        Cache[id] = wav;
        return wav;
    }

    private static AudioStreamWav Build(Sfx id)
    {
        var rng = new Random(0x5EED + (int)id * 7919);   // 每个音效固定种子 → 波形可复现
        float[] s = id switch
        {
            Sfx.Swing => Swing(rng),
            Sfx.HitLight => Impact(rng, 0.075f, 190f, 0.45f, 0.9f),
            Sfx.HitMedium => Impact(rng, 0.105f, 150f, 0.62f, 1.1f),
            Sfx.HitHeavy => Impact(rng, 0.165f, 108f, 0.85f, 1.45f),
            Sfx.Guard => Clang(rng, 1180f, 1760f, 0.14f, 0.30f),
            Sfx.Parry => Clang(rng, 1620f, 2440f, 0.22f, 0.38f),
            Sfx.Launch => Sweep(rng, 260f, 900f, 0.20f, 0.55f),
            Sfx.Down => Thud(rng, 0.22f, 92f, 0.68f),
            Sfx.Death => Sweep(rng, 420f, 90f, 0.55f, 0.60f),
            Sfx.MatchEnd => Chord(rng, 0.9f),
            Sfx.UiMove => Blip(rng, 880f, 0.045f, 0.22f),
            Sfx.UiConfirm => TwoTone(rng, 660f, 990f, 0.16f, 0.30f),
            Sfx.UiCancel => TwoTone(rng, 620f, 415f, 0.16f, 0.26f),
            _ => new float[64],
        };
        return ToWav(s);
    }

    // ---- 音色构造 ----

    /// 挥空：带通噪声的"咻"，很轻（不抢打击音）
    private static float[] Swing(Random r)
    {
        int n = Ms(140); var a = new float[n];
        float lp = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)n;
            float env = (float)Math.Sin(Math.PI * t) * 0.30f;         // 起-落对称包络
            float noise = (float)(r.NextDouble() * 2 - 1);
            lp += (noise - lp) * 0.35f;                                // 低通 → 去掉白噪的"沙"
            a[i] = lp * env;
        }
        return Color(a, 0.55f);
    }

    /// 打击：瞬态噪声（高频"咔"） + 低频体感（正弦下扫）
    private static float[] Impact(Random r, float dur, float bodyHz, float bodyGain, float brightGain)
    {
        int n = Ms(dur); var a = new float[n];
        float lp = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float tn = i / (float)n;
            // 瞬态：极快衰减的噪声（≈15ms 时间常数）
            float click = (float)(r.NextDouble() * 2 - 1) * (float)Math.Exp(-tn * (dur / 0.015f));
            lp += (click - lp) * 0.55f;
            // 体感：低频正弦随包络下扫
            float f = bodyHz * (1f + 0.45f * (1 - tn));
            float body = (float)Math.Sin(2 * Math.PI * f * t) * (float)Math.Exp(-tn * 6.0f);
            a[i] = lp * brightGain + body * bodyGain;
        }
        return Color(a, dur < 0.10f ? 0.62f : 0.78f);
    }

    /// 金属：两个不成谐波比的高频正弦拍频（"叮"）+ 极短噪声前沿
    private static float[] Clang(Random r, float f1, float f2, float dur, float gain)
    {
        int n = Ms(dur); var a = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate, tn = i / (float)n;
            float env = (float)Math.Exp(-tn * 5.5f);
            float ring = (float)(Math.Sin(2 * Math.PI * f1 * t) * 0.6 + Math.Sin(2 * Math.PI * f2 * t) * 0.4);
            float front = i < Ms(6) ? (float)(r.NextDouble() * 2 - 1) * 0.35f : 0f;
            a[i] = ring * env * gain + front;
        }
        return Color(a, 0.66f);
    }

    /// 位移/浮空：频率扫描（上行）
    private static float[] Sweep(Random r, float f0, float f1, float dur, float gain)
    {
        int n = Ms(dur); var a = new float[n];
        float phase = 0;
        for (int i = 0; i < n; i++)
        {
            float tn = i / (float)n;
            float f = f0 + (f1 - f0) * tn;
            phase += 2 * (float)Math.PI * f / Rate;
            float env = (float)Math.Sin(Math.PI * Math.Min(1f, tn * 1.1f));   // 缓起缓落
            a[i] = (float)Math.Sin(phase) * env * gain;
        }
        return Color(a, 0.60f);
    }

    /// 倒地：低频撞击 + 短噪声尾
    private static float[] Thud(Random r, float dur, float hz, float gain)
    {
        int n = Ms(dur); var a = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate, tn = i / (float)n;
            float body = (float)Math.Sin(2 * Math.PI * hz * t) * (float)Math.Exp(-tn * 4.0f);
            float tail = (float)(r.NextDouble() * 2 - 1) * (float)Math.Exp(-tn * 12f) * 0.25f;
            a[i] = body * gain + tail;
        }
        return Color(a, 0.74f);
    }

    /// 终局：低音三和弦缓起（"落幕"感，不做胜利号角以免廉价的煽情）
    private static float[] Chord(Random r, float dur)
    {
        int n = Ms(dur); var a = new float[n];
        float[] freqs = { 110f, 165f, 220f };
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate, tn = i / (float)n;
            float env = (float)(Math.Sin(Math.PI * tn) * Math.Exp(-tn * 0.8f));
            float v = 0;
            foreach (var f in freqs) v += (float)Math.Sin(2 * Math.PI * f * t);
            a[i] = v / freqs.Length * env;
        }
        return Color(a, 0.62f);
    }

    private static float[] Blip(Random r, float hz, float dur, float gain)
    {
        int n = Ms(dur); var a = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate, tn = i / (float)n;
            a[i] = (float)Math.Sin(2 * Math.PI * hz * t) * (float)Math.Exp(-tn * 8f) * gain;
        }
        return Color(a, 0.5f);
    }

    private static float[] TwoTone(Random r, float f0, float f1, float dur, float gain)
    {
        int n = Ms(dur); var a = new float[n];
        int half = n / 2;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float tn = i < half ? i / (float)half : (i - half) / (float)(n - half);
            float f = i < half ? f0 : f1;
            a[i] = (float)Math.Sin(2 * Math.PI * f * t) * (float)Math.Exp(-tn * 5f) * gain;
        }
        return Color(a, 0.5f);
    }

    // ---- 工具 ----

    private static int Ms(float seconds) => Math.Max(8, (int)(seconds * Rate));

    /// 去直流 + 峰值归一（不爆表），并按 peak 留出余量
    private static float[] Color(float[] a, float peak)
    {
        double mean = 0;
        foreach (var v in a) mean += v;
        mean /= a.Length;
        float max = 1e-6f;
        for (int i = 0; i < a.Length; i++)
        {
            a[i] -= (float)mean;
            if (Math.Abs(a[i]) > max) max = Math.Abs(a[i]);
        }
        float k = peak / max;
        for (int i = 0; i < a.Length; i++) a[i] *= k;

        // 首尾各 2ms 淡入淡出——消除爆音（click）
        int f = Ms(0.002f);
        for (int i = 0; i < f && i < a.Length; i++)
        {
            float w = i / (float)f;
            a[i] *= w;
            a[a.Length - 1 - i] *= w;
        }
        return a;
    }

    private static AudioStreamWav ToWav(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short v = (short)Math.Clamp(samples[i] * short.MaxValue, short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = Rate,
            Stereo = false,
            Data = bytes,
        };
    }
}
