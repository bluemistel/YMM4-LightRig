using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Effects;
using LightRig.Shared;

namespace LightRig.Effects.DepthFog;

/// <summary>カメラ距離の測り方。</summary>
public enum FogDistanceMode
{
    [Display(Name = "平面", Description = "カメラの視軸方向の距離で測る（画面端でも濃度が揃う）")]
    Planar = 0,
    [Display(Name = "放射", Description = "カメラからの直線距離で測る（画面端ほど遠くなる）")]
    Radial = 1,
}

/// <summary>
/// 空気遠近（デプスフォグ）。カメラからアイテムまでの距離に応じて、遠いレイヤーほど霞ませる。
/// レイヤー単位で濃度が決まるため「立ち絵は霞まず遠景だけ青く霞む」という正確な遠近が作れる。
///
/// 距離は <see cref="DrawDescription"/> の Camera / Draw から算出するので、他プラグインへの依存はない。
/// 2DCamera プラグインと併用しても、視軸方向の距離は保存されるため「平面」モードなら値がずれない。
///
/// フォグ色は「環境光サンプラー」から自動取得できる（背景の空の色をそのまま霞の色にできる）。
/// </summary>
[PluginDetails(AuthorName = "あおもや", ContentId = "sm46782084")]
[VideoEffect(
    "空気遠近(デプスフォグ)",
    ["LightRig"],
    ["空気遠近", "フォグ", "fog", "霞", "奥行き", "デプス"],
    IsAviUtlSupported = false)]
public class DepthFogEffect : VideoEffectBase
{
    public override string Label => "空気遠近(デプスフォグ)";

    [Display(GroupName = "距離", Name = "測り方", Description = "カメラ距離の測定方法")]
    [EnumComboBox]
    public FogDistanceMode Mode { get => mode; set => Set(ref mode, value); }
    FogDistanceMode mode = FogDistanceMode.Planar;

    [Display(GroupName = "距離", Name = "開始距離", Description = "この距離から霞み始める。カメラからの距離ではなく、標準カメラ位置（距離1000）を 0 とした奥行き")]
    [AnimationSlider("F0", "", 0, 1000)]
    public Animation NearDistance { get; } = new Animation(0, -100000, 100000);

    [Display(GroupName = "距離", Name = "終了距離", Description = "この距離で最大濃度になる")]
    [AnimationSlider("F0", "", 0, 1000)]
    public Animation FarDistance { get; } = new Animation(1000, -100000, 100000);

    [Display(GroupName = "距離", Name = "カーブ", Description = "1=直線的、大きいほど遠景に寄ってから急に霞む")]
    [AnimationSlider("F2", "", 0.2, 4)]
    public Animation Curve { get; } = new Animation(1.5, 0.01, 10);

    [Display(GroupName = "フォグ", Name = "最大濃度", Description = "終了距離での霞の強さ")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation MaxDensity { get; } = new Animation(70, 0, 100);

    [Display(GroupName = "フォグ", Name = "フォグ色", Description = "霞の色。環境光ミックスが 0 のときはこの色を使う")]
    [ColorPicker]
    public Color FogColor { get => fogColor; set => Set(ref fogColor, value); }
    Color fogColor = Color.FromArgb(255, 175, 195, 220);

    [Display(GroupName = "環境光連動", Name = "環境光チャンネル", Description = "「環境光サンプラー」から霞の色を取得するチャンネル")]
    [EnumComboBox]
    public LightChannelOrOff AmbientChannel { get => ambientChannel; set => Set(ref ambientChannel, value); }
    LightChannelOrOff ambientChannel = LightChannelOrOff.Off;

    [Display(GroupName = "環境光連動", Name = "環境光ミックス", Description = "0=フォグ色, 100=環境光サンプラーが測った背景色")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation AmbientMix { get; } = new Animation(100, 0, 100);

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new DepthFogEffectProcessor(devices, this);

    public override IEnumerable<string> CreateExoVideoFilters(
        int keyFrameIndex, ExoOutputDescription exoOutputDescription)
    {
        yield break;
    }

    protected override IEnumerable<IAnimatable> GetAnimatables()
        => [NearDistance, FarDistance, Curve, MaxDensity, AmbientMix];
}
