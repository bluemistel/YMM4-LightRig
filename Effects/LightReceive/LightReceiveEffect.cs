using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Effects;
using LightRig.Shared;

namespace LightRig.Effects.LightReceive;

/// <summary>
/// シーン光源に連動する受光。アルファのシルエットから擬似法線を作り、
/// <b>光の当たる側を光源色で明るくする</b>（内部ディテールを拾わずシワが出ない）。
///
/// LightRig の陰影まわりは「暗くする側」と「明るくする側」で対になっている。
/// <list type="bullet">
/// <item>シェーディング(光源連動) … 光の反対側を暗くする</item>
/// <item>受光(光源連動)（このエフェクト） … 光の当たる側を明るくする</item>
/// </list>
///
/// たき火・ランプ・ネオンのように<b>色を持つ光源</b>を立ち絵の前面に置く用途を想定している。
/// 背景なじませは環境光サンプラー由来の「背景の色」しか使えないため、
/// 光源そのものの色を面で受けるのはこのエフェクトの役割。
///
/// 光レイヤーを別に作ってぼかし → 合成モードで重ねる方式（リムライトと同型）。
/// 乗算で焼き込むと光色が最大 1 である以上 明るくできないため、加算系で重ねている。
/// </summary>
[PluginDetails(AuthorName = "あおもや", ContentId = "sm46782084")]
[VideoEffect(
    "受光(光源連動)",
    ["LightRig"],
    ["受光", "リライティング", "relighting", "ライティング", "陰影", "たき火", "シーン光源", "照らす"],
    IsAviUtlSupported = false)]
public class LightReceiveEffect : VideoEffectBase
{
    public override string Label => "受光(光源連動)";

    [Display(GroupName = "連動", Name = "光源チャンネル", Description = "同じチャンネルの「シーン光源ターゲット」に追従する。無効で単体動作")]
    [EnumComboBox]
    public LightChannelOrOff Channel { get => channel; set => Set(ref channel, value); }
    LightChannelOrOff channel = LightChannelOrOff.Ch1;

    [Display(GroupName = "連動", Name = "角度オフセット", Description = "光源方向への補正角（度）。連動無効時は絶対角（0=上）")]
    [AnimationSlider("F0", "°", -180, 180)]
    public Animation AngleOffset { get; } = new Animation(0, -360, 360);

    [Display(GroupName = "受光", Name = "強さ", Description = "受ける光の量")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Intensity { get; } = new Animation(70, 0, 100);

    [Display(GroupName = "受光", Name = "受光量", Description = "光色の明るさ倍率。100%を超えると白飛び方向へ寄る（強い光を正面から受けている表現）")]
    [AnimationSlider("F0", "%", 0, 300)]
    public Animation LightGain { get; } = new Animation(100, 0, 1000);

    [Display(GroupName = "受光", Name = "フォルム", Description = "擬似法線を取るスケール（px）。大きいほど大きな面で滑らか")]
    [AnimationSlider("F1", "px", 1, 100)]
    public Animation FormScale { get; } = new Animation(40, 1, 500);

    [Display(GroupName = "受光", Name = "広がり", Description = "光の当たる面の広さ。大きいほど広い面が明るくなる")]
    [AnimationSlider("F0", "%", 0, 200)]
    public Animation Diffuse { get; } = new Animation(100, 0, 400);

    [Display(GroupName = "受光", Name = "回り込み", Description = "光と影の境界の柔らかさ。大きいほど側面まで光が回る")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Wrap { get; } = new Animation(40, 0, 100);

    [Display(GroupName = "受光", Name = "ぼかし量", Description = "乗せる光のぼかし（px）。大きいほど柔らかく馴染む")]
    [AnimationSlider("F1", "px", 0, 100)]
    public Animation Blur { get; } = new Animation(12, 0, 1000);

    [Display(GroupName = "受光", Name = "合成モード", Description = "スクリーン=自然に明るくなる（既定）/ 加算=強い発光感 / ソフトライト=控えめ")]
    [EnumComboBox]
    public YukkuriMovieMaker.Project.Blend BlendMode { get => blendMode; set => Set(ref blendMode, value); }
    YukkuriMovieMaker.Project.Blend blendMode = YukkuriMovieMaker.Project.Blend.Screen;

    [Display(GroupName = "ハイライト", Name = "ハイライト", Description = "最も光に面した所に出る光沢の強さ")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Highlight { get; } = new Animation(25, 0, 200);

    [Display(GroupName = "ハイライト", Name = "締まり", Description = "光沢の鋭さ。大きいほど狭く硬い光沢になる")]
    [AnimationSlider("F0", "", 1, 64)]
    public Animation Shininess { get; } = new Animation(12, 1, 256);

    [Display(GroupName = "色", Name = "色ミックス", Description = "0=固定色, 100=シーン光源の色（連動時）")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation ColorMix { get; } = new Animation(100, 0, 100);

    [Display(GroupName = "色", Name = "固定色", Description = "連動しない場合・ミックス0側で使う光の色")]
    [ColorPicker]
    public Color LocalColor { get => localColor; set => Set(ref localColor, value); }
    Color localColor = Color.FromArgb(255, 255, 240, 210);

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new LightReceiveEffectProcessor(devices, this);

    public override IEnumerable<string> CreateExoVideoFilters(
        int keyFrameIndex, ExoOutputDescription exoOutputDescription)
    {
        yield break;
    }

    protected override IEnumerable<IAnimatable> GetAnimatables()
        => [AngleOffset, Intensity, LightGain, FormScale, Diffuse, Wrap, Blur, Highlight, Shininess, ColorMix];
}
