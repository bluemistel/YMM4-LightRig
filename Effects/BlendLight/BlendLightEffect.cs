using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Effects;
using YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes;
using LightRig.Shared;

namespace LightRig.Effects.BlendLight;

/// <summary>光の乗せ方。</summary>
public enum BlendLightMode
{
    [Display(Name = "グラデーション", Description = "光源方向に沿って面で染める。光源の向きが必要")]
    Gradient = 0,

    [Display(Name = "縁取り", Description = "光源側の輪郭だけを染める。光源の向きが必要")]
    Edge = 1,

    [Display(Name = "全体", Description = "被写体全体へ均一に背景色を乗せる。光源を置かずに背景へ馴染ませたいときはこれ")]
    Uniform = 2,
}

/// <summary>背景色をどう引くか。</summary>
public enum BlendLightColorSource
{
    [Display(Name = "位置に応じた色")] Grid = 0,
    [Display(Name = "代表色（単色）")] Average = 1,
}

/// <summary>
/// 背景なじませ。「環境光サンプラー」が測った背景の色を、被写体（立ち絵）へ
/// 光源方向のグラデーション、または光源側の輪郭として乗せ、背景から浮くのを抑える。
///
/// 「位置に応じた色」を選ぶと、背景を 3x3 に区切った色グリッドを被写体の位置で補間して使うため、
/// 頭には空の色、足元には地面の色というように場所ごとに違う背景色が乗る。
///
/// <b>必要なのは「環境光サンプラー」だけ</b>で、シーン光源ターゲットは任意。
/// 光源があれば「どちら側から光が当たっているか」を反映できるが、
/// 光源を置きたくない場合はモード＝全体にすれば向きを使わず背景色を均一に乗せる。
/// チャンネル=無効、または環境光が無い場合は固定色で単体エフェクトとして機能する。
/// </summary>
[PluginDetails(AuthorName = "bluemistel")]
[VideoEffect(
    "背景なじませ(環境光連動)",
    ["LightRig"],
    ["背景なじませ", "なじませ", "馴染ませ", "環境光", "ambient", "blend", "背景", "シーン光源", "立ち絵"],
    IsAviUtlSupported = false)]
public class BlendLightEffect : VideoEffectBase
{
    public override string Label => "背景なじませ(環境光連動)";

    [Display(GroupName = "連動", Name = "チャンネル", Description = "同じチャンネルの「環境光サンプラー」から背景色を受け取る（必須）。「シーン光源ターゲット」があれば光の向きも使う（任意）。無効で単体動作")]
    [EnumComboBox]
    public LightChannelOrOff Channel { get => channel; set => Set(ref channel, value); }
    LightChannelOrOff channel = LightChannelOrOff.Ch1;

    [Display(GroupName = "連動", Name = "角度オフセット", Description = "光源方向への補正角（度）。光源が無い場合は絶対角（0=上）。モード＝全体では使いません")]
    [AnimationSlider("F0", "°", -180, 180)]
    public Animation AngleOffset { get; } = new Animation(0, -360, 360);

    [Display(GroupName = "なじませ", Name = "モード", Description = "グラデーション=光源側を面で染める / 縁取り=光源側の輪郭だけを染める / 全体=向きを使わず均一に乗せる")]
    [EnumComboBox]
    public BlendLightMode Mode { get => mode; set => Set(ref mode, value); }
    BlendLightMode mode = BlendLightMode.Gradient;

    [Display(GroupName = "なじませ", Name = "強さ", Description = "背景色を乗せる量")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Intensity { get; } = new Animation(60, 0, 100);

    [Display(GroupName = "なじませ", Name = "広がり", Description = "グラデーションが被写体のどこまで回り込むか（100%で全体）")]
    [ShowPropertyEditorWhen(nameof(Mode), BlendLightMode.Gradient)]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Spread { get; } = new Animation(70, 0, 100);

    [Display(GroupName = "なじませ", Name = "縁幅", Description = "縁取りモードで染める縁の太さ（px）")]
    [ShowPropertyEditorWhen(nameof(Mode), BlendLightMode.Edge)]
    [AnimationSlider("F1", "px", 1, 50)]
    public Animation RimWidth { get; } = new Animation(10, 1, 500);

    [Display(GroupName = "なじませ", Name = "締まり", Description = "縁取りモードの縁の締まり（0=くっきり, 100=柔らかい）")]
    [ShowPropertyEditorWhen(nameof(Mode), BlendLightMode.Edge)]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation Softness { get; } = new Animation(0, 0, 100);

    [Display(GroupName = "なじませ", Name = "ぼかし量", Description = "乗せる光のぼかし（px）。大きいほど柔らかく馴染む")]
    [AnimationSlider("F1", "px", 0, 100)]
    public Animation Blur { get; } = new Animation(20, 0, 1000);

    [Display(GroupName = "なじませ", Name = "合成モード",
        Description = "ソフトライト=背景が明るければ明るく暗ければ暗く自動で寄る（既定・手動切替が不要）/ 通常=背景色へ単純に寄せる（アルファブレンド）/ カラー=元の明るさを保ったまま色味だけ移す / スクリーン・加算=明るくするだけ / 乗算=暗くするだけ")]
    [EnumComboBox]
    public YukkuriMovieMaker.Project.Blend BlendMode { get => blendMode; set => Set(ref blendMode, value); }
    // 既定はソフトライト。スクリーンや加算は「明るくする」ことしかできないため、
    // 夕焼けのような暗く寄せたい背景では手動で乗算へ切り替える必要があった。
    // ソフトライトは背景色の明暗に応じて自動的に明暗どちらへも寄るので切り替えが要らない。
    YukkuriMovieMaker.Project.Blend blendMode = YukkuriMovieMaker.Project.Blend.SoftLight;

    [Display(GroupName = "背景色", Name = "色の取得", Description = "位置に応じた色=背景を3x3に区切って被写体の位置で補間 / 代表色=背景全体の明るい部分の平均色")]
    [EnumComboBox]
    public BlendLightColorSource ColorSource { get => colorSource; set => Set(ref colorSource, value); }
    BlendLightColorSource colorSource = BlendLightColorSource.Grid;

    [Display(GroupName = "背景色", Name = "彩度", Description = "乗せる背景色の鮮やかさ（0でモノクロ）")]
    [AnimationSlider("F0", "%", 0, 200)]
    public Animation Saturation { get; } = new Animation(100, 0, 400);

    [Display(GroupName = "背景色", Name = "明るさ", Description = "乗せる背景色の明るさ倍率")]
    [AnimationSlider("F0", "%", 0, 200)]
    public Animation Gain { get; } = new Animation(100, 0, 400);

    [Display(GroupName = "背景色", Name = "固定色", Description = "環境光サンプラーが無い場合に使う色")]
    [ColorPicker]
    public Color LocalColor { get => localColor; set => Set(ref localColor, value); }
    Color localColor = Color.FromArgb(255, 200, 210, 230);

    [Display(GroupName = "背景の対応付け", Name = "範囲倍率", Description = "被写体が背景のどれだけの広さを参照するか。小さくすると色の変化が緩やかになる")]
    [ShowPropertyEditorWhen(nameof(ColorSource), BlendLightColorSource.Grid)]
    [AnimationSlider("F0", "%", 10, 400)]
    public Animation RangeScale { get; } = new Animation(100, 1, 1000);

    [Display(GroupName = "背景の対応付け", Name = "位置オフセットX", Description = "背景を参照する位置の横方向の補正（px）")]
    [ShowPropertyEditorWhen(nameof(ColorSource), BlendLightColorSource.Grid)]
    [AnimationSlider("F0", "px", -1000, 1000)]
    public Animation OffsetX { get; } = new Animation(0, -10000, 10000);

    [Display(GroupName = "背景の対応付け", Name = "位置オフセットY", Description = "背景を参照する位置の縦方向の補正（px）")]
    [ShowPropertyEditorWhen(nameof(ColorSource), BlendLightColorSource.Grid)]
    [AnimationSlider("F0", "px", -1000, 1000)]
    public Animation OffsetY { get; } = new Animation(0, -10000, 10000);

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new BlendLightEffectProcessor(devices, this);

    public override IEnumerable<string> CreateExoVideoFilters(
        int keyFrameIndex, ExoOutputDescription exoOutputDescription)
    {
        yield break;
    }

    protected override IEnumerable<IAnimatable> GetAnimatables()
        => [AngleOffset, Intensity, Spread, RimWidth, Softness, Blur, Saturation, Gain, RangeScale, OffsetX, OffsetY];
}
