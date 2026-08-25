using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Effects;
using LightRig.Shared;

namespace LightRig.Effects.LightTarget;

/// <summary>点光源・スポットの距離減衰の有無。</summary>
public enum LightFalloffMode
{
    [Display(Name = "減衰なし", Description = "距離によらず一定の明るさで届く（既定・従来の挙動）")]
    None = 0,

    [Display(Name = "到達距離で減衰", Description = "到達距離で光が 0 になるよう距離で減衰させる")]
    Range = 1,
}

/// <summary>
/// シーンに1つ置く「シーン光源」の発信役マーカーエフェクト。
/// 映像には一切手を加えず、自アイテムのワールド座標＋オフセットと光色・強度・ゆらぎ係数を
/// 毎フレーム共有ストア（<see cref="LightSignalStore"/>）へ発信する。
/// 同じチャンネルを指定した消費エフェクト（リムライト等）がこの光源を自動参照する。
///
/// 同じチャンネルの「環境光サンプラー」が背景色を発信している場合は、
/// 光の色・強さをその背景へ自動追従させられる（<see cref="AmbientSignalStore"/>）。
/// 光源側で一度追従させれば、消費エフェクト全部（リム・シェーディング・逆光・リライティング・
/// 落とし影・背景なじませ）が一斉にシーンの明るさへ揃うのが狙い。
///
/// UX: 図形や空アイテムに挿してタイムライン上で動かすと、その位置が光源位置になる。
/// さらにオフセットX/Yで微調整でき、位置・光色・強度・ゆらぎはすべてキーフレーム可能。
/// </summary>
[PluginDetails(AuthorName = "bluemistel")]
[VideoEffect(
    "シーン光源ターゲット",
    ["LightRig"],
    ["シーン光源", "ライト", "light", "光源", "リムライト", "逆光"],
    IsAviUtlSupported = false)]
public class LightTargetEffect : VideoEffectBase
{
    public override string Label => "シーン光源ターゲット";

    [Display(GroupName = "光源", Name = "チャンネル", Description = "同じチャンネルを指定した消費エフェクト（リムライト等）がこの光源を参照する")]
    [EnumComboBox]
    public LightChannel Channel { get => channel; set => Set(ref channel, value); }
    LightChannel channel = LightChannel.Ch1;

    [Display(GroupName = "光源", Name = "種類", Description = "点光源=位置から放射状に照らし距離で減衰 / 平行光=位置によらず角度一定 / スポット=指定方向へ円錐状")]
    [EnumComboBox]
    public LightSourceType SourceType { get => sourceType; set => Set(ref sourceType, value); }
    LightSourceType sourceType = LightSourceType.Point;

    [Display(GroupName = "光源", Name = "オフセットX", Description = "アイテム位置からの光源横オフセット（px）")]
    [AnimationSlider("F0", "px", -1000, 1000)]
    public Animation OffsetX { get; } = new Animation(0, -100000, 100000);

    [Display(GroupName = "光源", Name = "オフセットY", Description = "アイテム位置からの光源縦オフセット（px）")]
    [AnimationSlider("F0", "px", -1000, 1000)]
    public Animation OffsetY { get; } = new Animation(0, -100000, 100000);

    [Display(GroupName = "光源", Name = "高さ", Description = "画面に垂直な奥行き（Z）。シェーディング/リライティングの回り込みに影響（リムライトには影響しない）")]
    [AnimationSlider("F0", "", -1000, 1000)]
    public Animation Height { get; } = new Animation(200, -100000, 100000);

    [Display(GroupName = "光源", Name = "強さ", Description = "光の強度（100=等倍）")]
    [AnimationSlider("F0", "%", 0, 300)]
    public Animation Intensity { get; } = new Animation(100, 0, 100000);

    [Display(GroupName = "光源", Name = "光の色", Description = "光源の色")]
    [ColorPicker]
    public Color Color { get => color; set => Set(ref color, value); }
    Color color = Color.FromArgb(255, 255, 244, 224);

    [Display(GroupName = "範囲・減衰", Name = "距離で減衰", Description = "点光源・スポットで、光源から離れるほど暗くするかどうか。平行光では効かない")]
    [EnumComboBox]
    public LightFalloffMode FalloffMode { get => falloffMode; set => Set(ref falloffMode, value); }
    LightFalloffMode falloffMode = LightFalloffMode.None;

    [Display(GroupName = "範囲・減衰", Name = "到達距離", Description = "この距離で光が完全に届かなくなる（px）。プレビューの外側の円がこの距離")]
    [AnimationSlider("F0", "px", 100, 5000)]
    public Animation Range { get; } = new Animation(1000, 1, 100000);

    [Display(GroupName = "範囲・減衰", Name = "減衰の始まり", Description = "到達距離のどこから暗くなり始めるか。ここまでは等倍。0=到達距離いっぱいをかけて緩やかに落ちる, 100に近い=縁で急に切れる")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation FalloffStart { get; } = new Animation(40, 0, 100);

    [Display(GroupName = "範囲・減衰", Name = "光の角度", Description = "平行光の光が来る向き／スポットの照らす向き（0=上から下へ, 時計回り）")]
    [AnimationSlider("F0", "°", -180, 180)]
    public Animation Angle { get; } = new Animation(0, -360, 360);

    [Display(GroupName = "範囲・減衰", Name = "スポット角", Description = "スポットの円錐の広がり（全角）")]
    [AnimationSlider("F0", "°", 1, 180)]
    public Animation SpotAngle { get; } = new Animation(60, 1, 180);

    [Display(GroupName = "範囲・減衰", Name = "スポットの縁", Description = "スポットの縁のぼけ具合（0=くっきり）")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation SpotSoftness { get; } = new Animation(30, 0, 100);

    [Display(GroupName = "環境光連動", Name = "色の追従", Description = "同じチャンネルの「環境光サンプラー」が測った背景の色へ光の色を寄せる割合。0で手動の色のまま")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation AmbientColorMix { get; } = new Animation(0, 0, 100);

    [Display(GroupName = "環境光連動", Name = "明るさの追従", Description = "背景の明るさに応じて光の強さを増減させる割合。0で手動の強さのまま")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation AmbientIntensityMix { get; } = new Animation(0, 0, 100);

    [Display(GroupName = "環境光連動", Name = "基準の明るさ", Description = "この明るさの背景で強さが等倍になる。背景がこれより明るければ強く、暗ければ弱くなる")]
    [AnimationSlider("F0", "%", 1, 100)]
    public Animation AmbientReference { get; } = new Animation(50, 1, 100);

    [Display(GroupName = "ゆらぎ", Name = "ゆらぎ量", Description = "明滅の振幅（0=なし, 20=±20%）")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation FlickerAmount { get; } = new Animation(0, 0, 100);

    [Display(GroupName = "ゆらぎ", Name = "ゆらぎ周期", Description = "明滅が一巡する時間（秒）。大きいほどゆっくり。0=ゆらぎ無し")]
    [AnimationSlider("F1", "s", 0, 30)]
    public Animation FlickerPeriod { get; } = new Animation(4, 0, 1000);

    [Display(GroupName = "ゆらぎ", Name = "位相シード", Description = "光源ごとに揺らぎの位相をずらす")]
    [TextBoxSlider("F0", "", 0, 100)]
    public double FlickerSeed { get => flickerSeed; set => Set(ref flickerSeed, value); }
    double flickerSeed = 0;

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new LightTargetProcessor(this);

    public override IEnumerable<string> CreateExoVideoFilters(
        int keyFrameIndex, ExoOutputDescription exoOutputDescription) => [];

    protected override IEnumerable<IAnimatable> GetAnimatables()
        => [OffsetX, OffsetY, Height, Intensity, Range, FalloffStart, Angle, SpotAngle, SpotSoftness,
            AmbientColorMix, AmbientIntensityMix, AmbientReference, FlickerAmount, FlickerPeriod];
}
