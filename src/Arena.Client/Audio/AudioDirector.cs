using Godot;
using Arena.Infra.Presentation;

// PRODUCTION - Arena.Client
// 声音调度：把**表现线索**（PresentationCue）翻成音效，并给界面提供统一入口。
// 播放侧带**随机微扰**（音高 ±jitter），连击时不会变成"机关枪式重复"。
// 纯表现层——不读 Sim 状态、不影响判定。
namespace Arena.Client.Audio;

public sealed partial class AudioDirector : Node
{
    private const int Voices = 12;      // 声部池：连击叠加足够，且不无限增长

    /// 全局入口（界面层用它播音；未初始化时为 null，调用方需 ?.）
    public static AudioDirector? Instance { get; private set; }

    private readonly AudioStreamPlayer[] _pool = new AudioStreamPlayer[Voices];
    private int _next;
    private uint _rng = 0x9E3779B9;

    public AudioDirector()
    {
        for (int i = 0; i < Voices; i++)
        {
            _pool[i] = new AudioStreamPlayer();
            AddChild(_pool[i]);
        }
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    /// 播放一个音效。volumeDb 由调用方按"层次"给（普攻 < 重击 < 大招）。
    public void Play(Sfx id, float volumeDb = -6f, float pitchJitter = 0.05f)
    {
        var p = _pool[_next];
        _next = (_next + 1) % Voices;
        p.Stream = SfxSynth.Get(id);
        p.VolumeDb = volumeDb;
        p.PitchScale = 1f + ((float)NextUnit() - 0.5f) * 2f * pitchJitter;
        p.Play();
    }

    /// 表现线索 → 音效。伤害量级决定"轻重档"，这是打击感的核心分层。
    public void PlayCue(in PresentationCue cue)
    {
        switch (cue.Kind)
        {
            case PresentationCueKind.WeaponSwing:
                Play(Sfx.Swing, -17f, 0.10f);
                break;
            case PresentationCueKind.HitSpark:
                Play(Sfx.HitLight, -13f, 0.08f);
                break;
            case PresentationCueKind.HitFlash:
                // 按伤害分档：轻 / 中 / 重（与表现层 HitStop 的分档同源）
                if (cue.Damage >= 2000) Play(Sfx.HitHeavy, -5.5f, 0.04f);
                else if (cue.Damage >= 800) Play(Sfx.HitMedium, -8f, 0.05f);
                else Play(Sfx.HitLight, -11f, 0.06f);
                break;
            case PresentationCueKind.GuardSpark:
                Play(Sfx.Guard, -9f, 0.05f);
                break;
            case PresentationCueKind.ParrySpark:
                Play(Sfx.Parry, -5f, 0.03f);
                break;
            case PresentationCueKind.Launch:
                Play(Sfx.Launch, -9f, 0.05f);
                break;
            case PresentationCueKind.Down:
                Play(Sfx.Down, -7f, 0.05f);
                break;
            case PresentationCueKind.Death:
                Play(Sfx.Death, -4f, 0f);
                break;
            case PresentationCueKind.MatchEnd:
                Play(Sfx.MatchEnd, -8f, 0f);
                break;
        }
    }

    /// xorshift32 —— 表现层随机，确定性无关（不回流 Sim）
    private float NextUnit()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (_rng & 0xFFFFFF) / (float)0x1000000;
    }
}
