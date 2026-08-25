using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Effects;
using LightRig.Shared;

namespace LightRig.Effects.Relighting;

/// <summary>リライティングのプリセット。C# 側で光色・影色・拡散・ハイライト・回り込みへ解決する。</summary>
public enum RelightingPreset
{
    [Display(Name = "手動", Description = "光色・影色などを手動パラメータで指定")]
    Manual = 0,
    [Display(Name = "夕日", Description = "暖色の光＋寒色の影")]
    Sunset = 1,
    [Display(Name = "室内灯", Description = "暖白色の光＋ニュートラルな影")]
    Indoor = 2,
    [Display(Name = "月明かり", Description = "寒色の光＋濃い青の影＋強めのハイライト")]
    Moonlight = 3,
    [Display(Name = "炎", Description = "橙色の光＋暖かい影（シーン光源のゆらぎと相性が良い）")]
    Fire = 4,
    [Display(Name = "室外/昼光", Description = "白い直射光＋青みの影。晴天の屋外向け")]
    Daylight = 5,
    [Display(Name = "曇り/日陰", Description = "低コントラストで柔らかい。曇天・日陰向け")]
    Overcast = 6,
    [Display(Name = "蛍光灯", Description = "やや緑がかった白色光＋無彩色の影。office・室内向け")]
    Fluorescent = 7,
}

/// <summary>
/// シーン光源に連動するリライティング。アルファのシルエットから擬似法線を作り、
/// 光色で 2 トーン + ハイライトのライティングをし直す（内部ディテールを拾わずシワが出ない）。
/// プリセット（夕日/室内灯/月明かり/炎）でルックを一括設定でき、光源色は連動で上書き・ミックス可能。
/// </summary>
[PluginDetails(AuthorName = "bluemistel")]
[VideoEffect(
    "リライティング(光源連動)",
    ["LightRig"],
    ["リライティング", "relighting", "ライティング", "陰影", "夕日", "シーン光源"],
    IsAviUtlSupported = false)]
public class RelightingEffect : VideoEffectBase
{
    public override string Label => "リライティング(光源連動)";

    [Display(GroupName = "連動", Name = "光源チャンネル", Description = "同じチャンネルの「シーン光源ターゲット」に追従する。無効で単体動作")]
    [EnumComboBox]
    public LightChannelOrOff Channel { get => channel; set => Set(ref channel, value); }
    LightChannelOrOff channel = LightChannelOrOff.Ch1;

    [Display(GroupName = "連動", Name = "角度オフセット", Description = "光源方向への補正角（度）。連動無効時は絶対角（0=上）")]
    [AnimationSlider("F0", "°", -180, 180)]
    public Animation AngleOffset { get; } = new Animation(0, -360, 360);

    [Display(GroupName = "リライティング", Name = "プリセット", Description = "ルックの一括設定。手動を選ぶと下の色・パラメータが有効")]
    [EnumComboBox]
    public RelightingPreset Preset { get => preset; set => Set(ref preset, value); }
    RelightingPreset preset = RelightingPreset.Sunset;

    [Display(GroupName = "リライティング", Name = "強さ", Description = "元画像とリライト結果のミックス")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Intensity { get; } = new Animation(80, 0, 100);

    [Display(GroupName = "リライティング", Name = "フォルム", Description = "擬似法線を取るスケール（px）。大きいほど大きな面で滑らか")]
    [AnimationSlider("F1", "px", 1, 100)]
    public Animation FormScale { get; } = new Animation(40, 1, 500);

    [Display(GroupName = "リライティング", Name = "ぼかし量", Description = "陰影の境界のぼかし（px）。フォルムを上げた時の光と影の縞つきを馴染ませる。絵柄はぼかさない")]
    [AnimationSlider("F1", "px", 0, 100)]
    public Animation Blur { get; } = new Animation(0, 0, 500);

    [Display(GroupName = "リライティング", Name = "光色ミックス", Description = "0=プリセット/手動の光色, 100=シーン光源の色（連動時）")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation ColorMix { get; } = new Animation(100, 0, 100);

    [Display(GroupName = "リライティング", Name = "環境光ミックス", Description = "「環境光サンプラー」の背景色を影色にどれだけ混ぜるか（連動時）。背景と馴染ませる")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation AmbientMix { get; } = new Animation(0, 0, 100);

    [Display(GroupName = "手動（プリセット=手動時）", Name = "光色", Description = "光の色")]
    [ColorPicker]
    public Color LightColor { get => lightColor; set => Set(ref lightColor, value); }
    Color lightColor = Color.FromArgb(255, 255, 240, 210);

    [Display(GroupName = "手動（プリセット=手動時）", Name = "影色", Description = "陰部分の色")]
    [ColorPicker]
    public Color ShadowColor { get => shadowColor; set => Set(ref shadowColor, value); }
    Color shadowColor = Color.FromArgb(255, 70, 78, 105);

    [Display(GroupName = "手動（プリセット=手動時）", Name = "拡散", Description = "拡散光の強さ")]
    [AnimationSlider("F2", "", 0, 200)]
    public Animation Diffuse { get; } = new Animation(100, 0, 400);

    [Display(GroupName = "手動（プリセット=手動時）", Name = "ハイライト", Description = "光に面した面のハイライトの強さ")]
    [AnimationSlider("F0", "%", 0, 200)]
    public Animation Highlight { get; } = new Animation(30, 0, 400);

    [Display(GroupName = "手動（プリセット=手動時）", Name = "光沢", Description = "ハイライトの締まり（大きいほど鋭い）")]
    [AnimationSlider("F0", "", 1, 64)]
    public Animation Shininess { get; } = new Animation(8, 1, 256);

    [Display(GroupName = "手動（プリセット=手動時）", Name = "回り込み", Description = "光と影の境界の柔らかさ")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Wrap { get; } = new Animation(40, 0, 100);

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new RelightingEffectProcessor(devices, this);

    public override IEnumerable<string> CreateExoVideoFilters(
        int keyFrameIndex, ExoOutputDescription exoOutputDescription)
    {
        yield break;
    }

    protected override IEnumerable<IAnimatable> GetAnimatables()
        => [AngleOffset, Intensity, FormScale, Blur, ColorMix, AmbientMix, Diffuse, Highlight, Shininess, Wrap];
}
