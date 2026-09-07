using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Effects;
using LightRig.Shared;

namespace LightRig.Effects.SceneShadow;

/// <summary>
/// シーン光源に連動する落とし影。立ち絵のシルエットを接地線へ投影し、地面に落ちる影を描く。
/// 「シーン光源ターゲット」と同じチャンネルで、影の向き（光の反対側）と長さ（光の高さ）に自動追従する。
///
/// 2D の絵に対して厳密な投影をすると不自然になる場面があるため、
/// 自動算出値に対して「長さ」「傾き」「接地位置」で手動補正できるようにしてある。
///
/// 制約: 影は立ち絵アイテム自身の描画範囲にしか描けない（背景アイテムの上には落とせない）。
/// </summary>
[PluginDetails(AuthorName = "bluemistel")]
[VideoEffect(
    "落とし影(光源連動)",
    ["LightRig"],
    ["影", "落とし影", "shadow", "接地", "シーン光源"],
    IsAviUtlSupported = false)]
public class SceneShadowEffect : VideoEffectBase
{
    public override string Label => "落とし影(光源連動)";

    [Display(GroupName = "連動", Name = "光源チャンネル",
        Description = "同じチャンネルの「シーン光源ターゲット」に追従する。無効で単体動作。"
            + "【重要】このエフェクトは影をアイテムの絵に描き込むため、リムライト・受光・シェーディングより"
            + "後ろ（下）に置いてください。前に置くと影の部分が「被写体の面」とみなされ、"
            + "別チャンネルの光を受けて色が付きます")]
    [EnumComboBox]
    public LightChannelOrOff Channel { get => channel; set => Set(ref channel, value); }
    LightChannelOrOff channel = LightChannelOrOff.Ch1;

    [Display(GroupName = "連動", Name = "角度オフセット", Description = "光源方向への補正角（度）。連動無効時は絶対角（0=上）")]
    [AnimationSlider("F0", "°", -180, 180)]
    public Animation AngleOffset { get; } = new Animation(0, -360, 360);

    [Display(GroupName = "形状", Name = "長さ", Description = "影の長さの倍率。100%で光源の高さから算出した長さ。立ち絵の高さに対する割合として効く")]
    [AnimationSlider("F0", "%", 10, 300)]
    public Animation Length { get; } = new Animation(100, 1, 1000);

    [Display(GroupName = "形状", Name = "傾き", Description = "横への倒れ具合の倍率。光源の水平方向から算出した傾きに掛かる。影の横幅は「長さ×傾き」で決まる")]
    [AnimationSlider("F0", "%", 0, 300)]
    public Animation Lean { get; } = new Animation(100, 0, 1000);

    [Display(GroupName = "形状", Name = "接地位置", Description = "影の起点（足元）の位置。アイテム下端からの距離。+で下へ")]
    [AnimationSlider("F1", "px", -300, 300)]
    public Animation GroundOffset { get; } = new Animation(0, -10000, 10000);

    [Display(GroupName = "見た目", Name = "濃さ", Description = "影の不透明度")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Opacity { get; } = new Animation(50, 0, 100);

    [Display(GroupName = "見た目", Name = "ぼかし量", Description = "影全体のぼかし（px）")]
    [AnimationSlider("F1", "px", 0, 50)]
    public Animation Blur { get; } = new Animation(4, 0, 500);

    [Display(GroupName = "見た目", Name = "先端ぼかし", Description = "影の先端でのぼかし量（px）。接地部はシャープなまま先端だけを柔らかくする。0=全体を均一にぼかす")]
    [AnimationSlider("F1", "px", 0, 50)]
    public Animation TipBlur { get; } = new Animation(0, 0, 500);

    [Display(GroupName = "見た目", Name = "影色", Description = "影の色")]
    [ColorPicker]
    public Color ShadowColor { get => shadowColor; set => Set(ref shadowColor, value); }
    Color shadowColor = Color.FromArgb(255, 20, 22, 30);

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new SceneShadowEffectProcessor(devices, this);

    public override IEnumerable<string> CreateExoVideoFilters(
        int keyFrameIndex, ExoOutputDescription exoOutputDescription)
    {
        yield break;
    }

    protected override IEnumerable<IAnimatable> GetAnimatables()
        => [AngleOffset, Length, Lean, GroundOffset, Opacity, Blur, TipBlur];
}
