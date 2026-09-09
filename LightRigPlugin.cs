using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Update;

namespace LightRig;

/// <summary>
/// LightRig（シーンライティング）プラグインのエントリポイント。
/// シーンに置いた「シーン光源」を、各立ち絵のリムライト・シェーディング・逆光・受光等が
/// 同じチャンネルで自動参照し、ライティングの統一と調整コスト削減を図る。
/// エフェクトは各クラスの [VideoEffect] 属性で自動登録される（ここでの手動登録は不要）。
/// </summary>
[PluginDetails(AuthorName = "あおもや", ContentId = "sm46782084")]
public class LightRigPlugin : IPlugin
{
    /// <summary>
    /// YMM4 の PluginLoader は起動時に IPlugin 実装を生成する（IL で確認済み）。
    /// ここを更新確認の起点にすれば、映像エフェクトが何個あっても確認は1回で済む。
    /// </summary>
    public LightRigPlugin() => UpdateNotifier.EnsureCheckedOnce();

    public string Name => "LightRig";

    /// <summary>
    /// 更新の確認。YMM4 標準の更新機構に乗せる（YMM4 側が通知 UI とダウンロードまで行う）。
    /// バージョンの取得元とリンク先の使い分けは <see cref="LightRigUpdater"/> を参照。
    /// </summary>
    public IPluginUpdater? Updater { get; } = new LightRigUpdater();
}
