using System.ComponentModel.DataAnnotations;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Effects;
using LightRig.Shared;

namespace LightRig.Effects.AmbientSampler;

/// <summary>
/// 環境光サンプラー。背景アイテムに挿すと、そのアイテムの明るい部分の平均色を毎数フレーム算出し、
/// 環境光色として <see cref="AmbientSignalStore"/> へ発信する。映像はパススルー。
/// 同じチャンネルを指定したリライティング等が、暗部（影色）にこの色を混ぜて背景と馴染ませる。
///
/// 単純な全体平均だと建物・木・道路といった暗色に引きずられて environment の印象と合わないため、
/// 輝度しきい値以上の画素だけを平均する（空・光源・明部＝実際に光を投げている部分を拾う）。
///
/// GPU→CPU の画素読み戻しを行うため、負荷軽減のためサンプリングは一定時間おきに間引く
/// （間隔はミリ秒指定。フレーム単位にするとプロジェクトの FPS で追従速度が変わってしまう）。
/// 読み戻しに失敗した場合はパススルーのみに縮退し、描画は継続する。
/// </summary>
[PluginDetails(AuthorName = "あおもや", ContentId = "sm46782084")]
[VideoEffect(
    "環境光サンプラー",
    ["LightRig"],
    ["環境光", "ambient", "背景色", "シーン光源", "馴染ませ"],
    IsAviUtlSupported = false)]
public class AmbientSamplerEffect : VideoEffectBase
{
    public override string Label => "環境光サンプラー";

    [Display(GroupName = "環境光", Name = "チャンネル", Description = "同じチャンネルを指定した消費エフェクトがこの環境光色を参照する")]
    [EnumComboBox]
    public LightChannel Channel { get => channel; set => Set(ref channel, value); }
    LightChannel channel = LightChannel.Ch1;

    [Display(GroupName = "詳細", Name = "輝度しきい値",
        Description = "代表色として拾う明るさの下限。画面内で最も明るい部分を100%とした相対値で、0=全体の平均色、上げるほど空や光源など明るい部分だけの色になる。"
                    + "【注意】これは「代表色」にのみ影響します。背景なじませの『色の取得＝位置に応じた色』では使われません")]
    [TextBoxSlider("F0", "%", 0, 100)]
    public double LuminanceThreshold { get => luminanceThreshold; set => Set(ref luminanceThreshold, Math.Clamp(value, 0, 100)); }
    double luminanceThreshold = 50;

    // 【フレーム単位にしないこと】プロジェクトの FPS で実時間が変わってしまう。
    // 30fps の 5 フレームは 167ms、60fps なら 83ms で追従の速さが別物になる。
    // ゆらぎ周期を Hz から「秒」へ直したのと同じ理由（FPS 非依存にする）。
    [Display(GroupName = "詳細", Name = "サンプリング間隔",
        Description = "背景色を測り直す間隔（ミリ秒）。0で毎フレーム。背景が静止している場合は変えても見た目に影響しません")]
    [TextBoxSlider("F0", "ms", 0, 1000)]
    public double SampleIntervalMs { get => sampleIntervalMs; set => Set(ref sampleIntervalMs, Math.Clamp(value, 0, 10000)); }
    double sampleIntervalMs = 100;

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new AmbientSamplerProcessor(devices, this);

    public override IEnumerable<string> CreateExoVideoFilters(
        int keyFrameIndex, ExoOutputDescription exoOutputDescription) => [];

    protected override IEnumerable<IAnimatable> GetAnimatables() => [];
}
