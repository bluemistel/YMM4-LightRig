using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using YukkuriMovieMaker.Plugin.Update;

namespace LightRig;

/// <summary>
/// 更新の確認と通知。
///
/// <para>
/// 【なぜ自前で持つのか（2026-09・IL 調査済み）】
/// YMM4 には <see cref="IPluginUpdater"/> という更新機構の口が用意されているが、
/// <b>4.55.1.1 の時点でこの API を呼び出しているコードがインストール全体に1箇所も無い</b>
/// （<c>get_Updater</c> / <c>GetUpdatesAsync</c> / <c>get_PluginUrl</c> / <c>get_CanDownload</c>
/// の呼び出し数がいずれも 0）。つまり実装しても何も起きない。
/// 本体側が将来対応したときに備えて <see cref="LightRigUpdater"/> は残してあるが、
/// 実際の通知はこのクラスが行う。
/// </para>
///
/// <para>
/// バージョンの比較には YMM4 の <see cref="PluginVersion"/> をそのまま使う
/// （自前でバージョン文字列を解釈しない）。
/// </para>
///
/// <para>
/// 失敗（オフライン・API 制限・リポジトリが private 等）は<b>すべて黙って無視する</b>。
/// 更新確認の失敗で編集作業を止めてはいけない。
/// </para>
/// </summary>
internal static class UpdateNotifier
{
    const string Owner = "bluemistel";
    const string Repo = "YMM4-LightRig";

    /// <summary>通知後に開くページ。空なら GitHub のリリースページ。</summary>
    static string StoreUrl => LightRigUpdater.StoreUrl;

    /// <summary>起動直後にダイアログを出すと邪魔なので少し待つ。</summary>
    static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    static int started;

    /// <summary>
    /// 更新確認を一度だけ開始する。何度呼んでも安全。
    /// LightRig は映像エフェクトが9個ある構成なので、どのエフェクトから呼ばれても
    /// 実際の確認は1回だけになるようにしてある。
    /// </summary>
    public static void EnsureCheckedOnce()
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            return;

        // 起動を遅らせないよう完全に切り離す。例外は RunAsync 内で握りつぶす。
        _ = Task.Run(RunAsync);
    }

    static async Task RunAsync()
    {
        try
        {
            var current = PluginVersion.FromAssemblyInformationalVersion(typeof(LightRigPlugin));
            if (current is null)
                return;

            var latest = await FetchLatestAsync().ConfigureAwait(false);
            if (latest is null)
                return;

            if (latest.Version.CompareTo(current) <= 0)
                return; // 最新か、手元のほうが新しい

            // 同じバージョンを毎回知らせない
            if (AlreadyNotified(latest.Tag))
                return;

            await Task.Delay(StartupDelay).ConfigureAwait(false);
            Notify(current, latest);
            RememberNotified(latest.Tag);
        }
        catch
        {
            // オフライン・API 制限・リポジトリが private 等。編集の邪魔をしない。
        }
    }

    /// <summary>
    /// 最新のリリースを取得する。
    ///
    /// <para>
    /// <b>manjubox のプラグイン API を優先し、失敗したら GitHub を直接見る。</b>
    /// GitHub API は未認証だと 1時間あたり 60 リクエスト／IP の制限があり、
    /// プラグインを複数入れている利用者では現実的に到達しうる。
    /// manjubox 側は GitHub のリリース情報を15分おきにキャッシュしているのでこの心配が無い。
    /// 一方でリリース直後は反映が遅れ、第三者サービスなので落ちることもあるため、
    /// GitHub 直取得を予備に残す。
    /// </para>
    ///
    /// <para>
    /// manjubox 側は<b>プラグイン一覧に登録されたリポジトリのみ</b>が対象で、
    /// 未登録だと <c>{"error":"Plugin not found"}</c> が返る（その場合も予備へ落ちる）。
    /// </para>
    /// </summary>
    static async Task<ReleaseInfo?> FetchLatestAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub API は User-Agent 必須（無いと 403 になる）
        http.DefaultRequestHeaders.Add("User-Agent", $"{Repo}-UpdateCheck");
        http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

        string[] endpoints =
        [
            $"https://manjubox.net/api/ymm4plugins/github/detail/{Owner}/{Repo}",
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest",
        ];

        foreach (var url in endpoints)
        {
            try
            {
                var json = await http.GetStringAsync(url).ConfigureAwait(false);
                var info = ReleaseInfo.PickLatest(json);
                if (info is not null)
                    return info;
            }
            catch
            {
                // 次の取得先へ
            }
        }
        return null;
    }

    static void Notify(PluginVersion current, ReleaseInfo latest)
    {
        var app = Application.Current;
        if (app is null)
            return;

        var url = string.IsNullOrWhiteSpace(StoreUrl)
            ? $"https://github.com/{Owner}/{Repo}/releases/latest"
            : StoreUrl;

        app.Dispatcher.InvokeAsync(() =>
        {
            var sb = new StringBuilder();
            sb.Append("LightRig の新しいバージョンがあります。\n\n")
              .Append($"　お使いのバージョン: {current.Version}\n")
              .Append($"　最新のバージョン　: {latest.Version.Version}\n");

            // リリースのタイトル。バージョン番号だけでは何が変わったのか分からないため。
            if (!string.IsNullOrWhiteSpace(latest.Title))
                sb.Append($"\n{latest.Title}\n");

            // リリースノートの書き出し（最初の段落）。無ければ何も足さない。
            var summary = latest.GetSummary();
            if (summary.Length > 0)
                sb.Append($"\n{summary}\n");

            sb.Append("\n配布ページを開きますか？");

            var result = MessageBox.Show(
                sb.ToString(), "LightRig の更新", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // 既定のブラウザが無い等。ここで落ちる必要は無い。
            }
        });
    }

    // --- 通知済みバージョンの記録 ---------------------------------------------
    // 毎回起動するたびに同じ通知が出ると煩わしいので、知らせたタグを覚えておく。
    // 保存に失敗した場合は「未通知」として扱う（次回また出るだけで害はない）。

    static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LightRig", "last-notified-version.txt");

    static bool AlreadyNotified(string tag)
    {
        try
        {
            return File.Exists(StatePath)
                && string.Equals(File.ReadAllText(StatePath).Trim(), tag, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    static void RememberNotified(string tag)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, tag);
        }
        catch
        {
        }
    }
}
