using YukkuriMovieMaker.Plugin.Update;

namespace LightRig;

/// <summary>
/// LightRig の更新確認（YMM4 本体向けの実装）。
///
/// <para>
/// <b>【重要】YMM4 4.55.1.1 はこの API を呼ばない。</b>インストール全体を IL で調べたところ
/// <c>get_Updater</c> / <c>GetUpdatesAsync</c> / <c>get_PluginUrl</c> / <c>get_CanDownload</c> の
/// 呼び出しが1箇所も無く、実装しても何も起きない。
/// 実際の通知は <see cref="UpdateNotifier"/> が行う。本体が将来対応したときのためにここは残す。
/// </para>
///
///
/// <para>
/// <b>バージョンの確認は GitHub Releases、案内先は配布ページ</b>という組み合わせにするための薄い委譲。
/// YMM4 標準の <see cref="GitHubReleasesPluginUpdater{T}"/> はリリース一覧の取得・バージョン比較・
/// <c>.ymme</c> のダウンロードまで面倒を見てくれるが、<c>PluginUrl</c> は GitHub 固定になる。
/// GitHub と BOOTH の両方で配布するため、リンク先だけを差し替える。
/// </para>
///
/// <para>
/// 【動作条件】リポジトリが<b>公開</b>されていること（Releases API は匿名アクセスなので
/// private では 404 になり、更新が一切検知されない）。タグは <c>v1.1.0</c> のように
/// バージョンとして解釈できる形にすること。現在のバージョンはアセンブリの
/// InformationalVersion（csproj の <c>Version</c>）から読まれる。
/// </para>
/// </summary>
internal sealed class LightRigUpdater : IPluginUpdater
{
    /// <summary>GitHub リポジトリの所有者とリポジトリ名。リリースのタグをバージョンの正とする。</summary>
    const string Owner = "bluemistel";
    const string Repo = "YMM4-LightRig";

    /// <summary>
    /// 更新が見つかったときに案内する配布ページ。
    /// <b>BOOTH の商品ページ URL をここに入れる。</b>
    /// 空のままなら GitHub のリポジトリを開くので、未設定でも壊れない。
    /// </summary>
    internal const string StoreUrl = "";

    readonly GitHubReleasesPluginUpdater<LightRigPlugin> gitHub = new(Owner, Repo);

    public string PluginUrl =>
        string.IsNullOrWhiteSpace(StoreUrl) ? $"https://github.com/{Owner}/{Repo}" : StoreUrl;

    public PluginVersion CurrentVersion => gitHub.CurrentVersion;

    public Task<IPluginUpdateInfo[]> GetUpdatesAsync() => gitHub.GetUpdatesAsync();
}
