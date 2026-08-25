using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Effects;
using LightRig.Shared;

namespace LightRig.Effects.SceneRimLight;

/// <summary>
/// シーン光源に連動するリムライト。立ち絵の輪郭のうち、シーン光源の方向に面した側を光らせる。
/// 「シーン光源ターゲット」と同じチャンネルを指定すると、光源の位置・色に自動追従する。
/// チャンネル=無効、または光源が無い場合は手動の角度・固定色で単体エフェクトとして機能する。
/// </summary>
[PluginDetails(AuthorName = "bluemistel")]
[VideoEffect(
    "リムライト(光源連動)",
    ["LightRig"],
    ["リムライト", "rim light", "縁取り光", "逆光", "エッジライト", "シーン光源"],
    IsAviUtlSupported = false)]
public class SceneRimLightEffect : VideoEffectBase
{
    public override string Label => "リムライト(光源連動)";

    [Display(GroupName = "連動", Name = "光源チャンネル", Description = "同じチャンネルの「シーン光源ターゲット」に追従する。無効で単体動作")]
    [EnumComboBox]
    public LightChannelOrOff Channel { get => channel; set => Set(ref channel, value); }
    LightChannelOrOff channel = LightChannelOrOff.Ch1;

    [Display(GroupName = "連動", Name = "角度オフセット", Description = "光源方向への補正角（度）。連動無効時は絶対角（0=上）")]
    [AnimationSlider("F0", "°", -180, 180)]
    public Animation AngleOffset { get; } = new Animation(0, -360, 360);

    [Display(GroupName = "リムライト", Name = "縁幅", Description = "光る縁の太さ（px）")]
    [AnimationSlider("F1", "px", 1, 50)]
    public Animation RimWidth { get; } = new Animation(8, 1, 500);

    [Display(GroupName = "リムライト", Name = "ぼかし量", Description = "縁光のぼかし（px）")]
    [AnimationSlider("F1", "px", 0, 50)]
    public Animation Blur { get; } = new Animation(6, 0, 1000);

    [Display(GroupName = "リムライト", Name = "締まり", Description = "縁の締まり（0=くっきり, 100=柔らかい）")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Softness { get; } = new Animation(0, 0, 100);

    [Display(GroupName = "リムライト", Name = "強さ", Description = "リムライトの不透明度（ローカル倍率）")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Intensity { get; } = new Animation(100, 0, 100);

    [Display(GroupName = "色", Name = "色ミックス", Description = "0=固定色, 100=光源色。連動時に光源色をどれだけ使うか")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation ColorMix { get; } = new Animation(100, 0, 100);

    [Display(GroupName = "色", Name = "固定色", Description = "連動しない場合・ミックス0側で使う縁光の色")]
    [ColorPicker]
    public Color LocalColor { get => localColor; set => Set(ref localColor, value); }
    Color localColor = Color.FromArgb(255, 255, 245, 220);

    [Display(GroupName = "色", Name = "合成モード", Description = "元画像との合成方法")]
    [EnumComboBox]
    public YukkuriMovieMaker.Project.Blend BlendMode { get => blendMode; set => Set(ref blendMode, value); }
    YukkuriMovieMaker.Project.Blend blendMode = YukkuriMovieMaker.Project.Blend.Add;

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new SceneRimLightEffectProcessor(devices, this);

    public override IEnumerable<string> CreateExoVideoFilters(
        int keyFrameIndex, ExoOutputDescription exoOutputDescription)
    {
        yield break;
    }

    protected override IEnumerable<IAnimatable> GetAnimatables()
        => [AngleOffset, RimWidth, Blur, Softness, Intensity, ColorMix];
}
