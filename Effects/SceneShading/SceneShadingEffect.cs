using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Effects;
using LightRig.Shared;

namespace LightRig.Effects.SceneShading;

/// <summary>シェーディングのモード。</summary>
public enum ShadingMode
{
    [Display(Name = "エッジシェード", Description = "光源と反対側の輪郭を暗くする（陰の縁）")]
    Edge = 0,
    [Display(Name = "擬似ノーマル", Description = "アルファのシルエットから擬似法線を作り陰影を付ける（逆光向け・シワが出ない）")]
    PseudoNormal = 1,
}

/// <summary>
/// シーン光源に連動するシェーディング。立ち絵のうち光源と反対側を暗くし、立体感や逆光の陰を作る。
/// 「シーン光源ターゲット」と同じチャンネルで光源方向・高さに追従する。無効時は手動角度で単体動作。
/// </summary>
[PluginDetails(AuthorName = "bluemistel")]
[VideoEffect(
    "シェーディング(光源連動)",
    ["LightRig"],
    ["シェーディング", "shading", "陰影", "影", "逆光", "シーン光源"],
    IsAviUtlSupported = false)]
public class SceneShadingEffect : VideoEffectBase
{
    public override string Label => "シェーディング(光源連動)";

    [Display(GroupName = "連動", Name = "光源チャンネル", Description = "同じチャンネルの「シーン光源ターゲット」に追従する。無効で単体動作")]
    [EnumComboBox]
    public LightChannelOrOff Channel { get => channel; set => Set(ref channel, value); }
    LightChannelOrOff channel = LightChannelOrOff.Ch1;

    [Display(GroupName = "連動", Name = "角度オフセット", Description = "光源方向への補正角（度）。連動無効時は絶対角（0=上）")]
    [AnimationSlider("F0", "°", -180, 180)]
    public Animation AngleOffset { get; } = new Animation(0, -360, 360);

    [Display(GroupName = "シェーディング（共通）", Name = "モード", Description = "エッジシェード=縁のみ / 擬似ノーマル=シルエットから全面陰影")]
    [EnumComboBox]
    public ShadingMode Mode { get => mode; set => Set(ref mode, value); }
    ShadingMode mode = ShadingMode.Edge;

    [Display(GroupName = "シェーディング（共通）", Name = "濃さ", Description = "影の濃さ")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Strength { get; } = new Animation(60, 0, 100);

    [Display(GroupName = "シェーディング（共通）", Name = "影色", Description = "陰部分に掛ける色（乗算）")]
    [ColorPicker]
    public Color ShadeColor { get => shadeColor; set => Set(ref shadeColor, value); }
    Color shadeColor = Color.FromArgb(255, 70, 80, 110);

    [Display(GroupName = "エッジシェード（モード=エッジシェード時）", Name = "幅", Description = "影を出す縁の太さ（px）")]
    [AnimationSlider("F1", "px", 1, 100)]
    public Animation Width { get; } = new Animation(20, 1, 1000);

    [Display(GroupName = "エッジシェード（モード=エッジシェード時）", Name = "締まり", Description = "エッジの締まり（0=くっきり, 100=柔らかい）")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Softness { get; } = new Animation(20, 0, 100);

    [Display(GroupName = "エッジシェード（モード=エッジシェード時）", Name = "ぼかし量", Description = "影の境界のぼかし（px）。絵柄はぼかさない")]
    [AnimationSlider("F1", "px", 0, 100)]
    public Animation Blur { get; } = new Animation(8, 0, 500);

    [Display(GroupName = "擬似ノーマル（モード=擬似ノーマル時）", Name = "フォルム", Description = "シルエットから拾う形の大きさ（px）。大きいほど大きな面で滑らか")]
    [AnimationSlider("F1", "px", 1, 100)]
    public Animation FormScale { get; } = new Animation(40, 1, 500);

    [Display(GroupName = "擬似ノーマル（モード=擬似ノーマル時）", Name = "回り込み", Description = "光と影の境界の柔らかさ（テルミネータ）")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Wrap { get; } = new Animation(30, 0, 100);

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new SceneShadingEffectProcessor(devices, this);

    public override IEnumerable<string> CreateExoVideoFilters(
        int keyFrameIndex, ExoOutputDescription exoOutputDescription)
    {
        yield break;
    }

    protected override IEnumerable<IAnimatable> GetAnimatables()
        => [AngleOffset, Strength, Width, Wrap, Softness, Blur, FormScale];
}
