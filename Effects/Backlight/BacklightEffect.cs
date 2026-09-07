using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Effects;
using LightRig.Shared;

namespace LightRig.Effects.Backlight;

/// <summary>
/// 逆光。被写体を減光・脱色してシルエット寄りにし、光源側の輪郭に強いリムを足す複合表現。
/// 「シーン光源ターゲット」と同じチャンネルで光源方向・光色に連動する。無効時は手動角度で単体動作。
/// 「逆光の強さ」1本で減光と脱色をまとめて調整でき、リムは別途強さ・幅・色を持つ。
/// </summary>
[PluginDetails(AuthorName = "bluemistel")]
[VideoEffect(
    "逆光",
    ["LightRig"],
    ["逆光", "backlight", "シルエット", "リムライト", "シーン光源"],
    IsAviUtlSupported = false)]
public class BacklightEffect : VideoEffectBase
{
    public override string Label => "逆光";

    [Display(GroupName = "連動", Name = "光源チャンネル", Description = "同じチャンネルの「シーン光源ターゲット」に追従する。無効で単体動作")]
    [EnumComboBox]
    public LightChannelOrOff Channel { get => channel; set => Set(ref channel, value); }
    LightChannelOrOff channel = LightChannelOrOff.Ch1;

    [Display(GroupName = "連動", Name = "角度オフセット", Description = "光源方向への補正角（度）。連動無効時は絶対角（0=上）")]
    [AnimationSlider("F0", "°", -180, 180)]
    public Animation AngleOffset { get; } = new Animation(0, -360, 360);

    [Display(GroupName = "逆光", Name = "逆光の強さ", Description = "全体の減光と脱色の量")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Amount { get; } = new Animation(60, 0, 100);

    [Display(GroupName = "リム", Name = "リム強さ", Description = "光源側の輪郭光の強さ")]
    [AnimationSlider("F0", "%", 0, 300)]
    public Animation RimStrength { get; } = new Animation(120, 0, 1000);

    [Display(GroupName = "リム", Name = "輪郭のぼかし",
        Description = "帯を作る前にシルエットをぼかす量（px）。大きいほどリムが柔らかく広がる。0でアルファ差分（従来の硬い縁）")]
    [AnimationSlider("F1", "px", 0, 100)]
    public Animation SilhouetteBlur { get; } = new Animation(20, 0, 500);

    [Display(GroupName = "リム", Name = "リム幅", Description = "光る縁の太さ（px）")]
    [AnimationSlider("F1", "px", 1, 50)]
    public Animation RimWidth { get; } = new Animation(6, 1, 500);

    [Display(GroupName = "リム", Name = "締まり", Description = "縁の締まり（0=くっきり, 100=柔らかい）")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Softness { get; } = new Animation(0, 0, 100);

    [Display(GroupName = "リム", Name = "ぼかし量", Description = "リムの境界のぼかし（px）。絵柄はぼかさずリムだけを柔らかくする")]
    [AnimationSlider("F1", "px", 0, 50)]
    public Animation Blur { get; } = new Animation(6, 0, 500);

    [Display(GroupName = "リム", Name = "合成モード", Description = "リムを本体へ重ねる方法")]
    [EnumComboBox]
    public YukkuriMovieMaker.Project.Blend BlendMode { get => blendMode; set => Set(ref blendMode, value); }
    YukkuriMovieMaker.Project.Blend blendMode = YukkuriMovieMaker.Project.Blend.Add;

    [Display(GroupName = "色", Name = "色ミックス", Description = "0=固定色, 100=光源色。リムに光源色をどれだけ使うか")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation ColorMix { get; } = new Animation(100, 0, 100);

    [Display(GroupName = "色", Name = "固定色", Description = "連動しない場合・ミックス0側で使うリムの色")]
    [ColorPicker]
    public Color LocalColor { get => localColor; set => Set(ref localColor, value); }
    Color localColor = Color.FromArgb(255, 255, 240, 210);

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new BacklightEffectProcessor(devices, this);

    public override IEnumerable<string> CreateExoVideoFilters(
        int keyFrameIndex, ExoOutputDescription exoOutputDescription)
    {
        yield break;
    }

    protected override IEnumerable<IAnimatable> GetAnimatables()
        => [AngleOffset, Amount, RimStrength, SilhouetteBlur, RimWidth, Softness, Blur, ColorMix];
}
