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
/// GPU→CPU の画素読み戻しを行うため、負荷軽減のためサンプリングは数フレームおきに間引く。
/// 読み戻しに失敗した場合はパススルーのみに縮退し、描画は継続する。
/// </summary>
[PluginDetails(AuthorName = "bluemistel")]
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

    [Display(GroupName = "環境光", Name = "輝度しきい値", Description = "この明るさ以上の画素だけを環境光として拾う。上げるほど空・光源など明るい部分のみになる")]
    [TextBoxSlider("F0", "%", 0, 100)]
    public double LuminanceThreshold { get => luminanceThreshold; set => Set(ref luminanceThreshold, Math.Clamp(value, 0, 100)); }
    double luminanceThreshold = 50;

    [Display(GroupName = "環境光", Name = "サンプリング間隔", Description = "何フレームおきに背景色を測り直すか（大きいほど軽い）")]
    [TextBoxSlider("F0", "frame", 1, 30)]
    public int SampleInterval { get => sampleInterval; set => Set(ref sampleInterval, Math.Max(1, value)); }
    int sampleInterval = 5;

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new AmbientSamplerProcessor(devices, this);

    public override IEnumerable<string> CreateExoVideoFilters(
        int keyFrameIndex, ExoOutputDescription exoOutputDescription) => [];

    protected override IEnumerable<IAnimatable> GetAnimatables() => [];
}
