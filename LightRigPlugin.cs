using YukkuriMovieMaker.Plugin;

namespace LightRig;

/// <summary>
/// LightRig（シーンライティング）プラグインのエントリポイント。
/// シーンに1つ置いた「シーン光源」を、各立ち絵のリムライト・シェーディング・逆光・
/// リライティング等が同じチャンネルで自動参照し、ライティングの統一と調整コスト削減を図る。
/// エフェクトは各クラスの [VideoEffect] 属性で自動登録される（ここでの手動登録は不要）。
/// </summary>
[PluginDetails(AuthorName = "bluemistel")]
public class LightRigPlugin : IPlugin
{
    public string Name => "LightRig";
}
