using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YukkuriMovieMaker.Plugin.Update;

namespace LightRig;

/// <summary>
/// GitHub のリリース1件分。<see cref="UpdateNotifier"/> が通知に使う。
/// </summary>
internal sealed partial class ReleaseInfo
{
    public required PluginVersion Version { get; init; }
    public required string Tag { get; init; }

    /// <summary>リリースのタイトル（GitHub の <c>name</c>）。</summary>
    public string Title { get; init; } = "";

    /// <summary>リリースノート本文（Markdown）。</summary>
    public string Body { get; init; } = "";

    /// <summary>
    /// リリース一覧の JSON（GitHub の releases 形式）から、最も新しい正式リリースを選ぶ。
    /// 単一オブジェクトの JSON（<c>releases/latest</c> の応答）も受け付ける。
    /// </summary>
    public static ReleaseInfo? PickLatest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // manjubox の API は見つからない場合 {"error":"Plugin not found"} を返す
        if (root.ValueKind == JsonValueKind.Object)
            return Parse(root);

        if (root.ValueKind != JsonValueKind.Array)
            return null;

        ReleaseInfo? best = null;
        foreach (var element in root.EnumerateArray())
        {
            var info = Parse(element);
            if (info is null)
                continue;
            if (best is null || info.Version.CompareTo(best.Version) > 0)
                best = info;
        }
        return best;
    }

    static ReleaseInfo? Parse(JsonElement e)
    {
        if (e.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            return null;
        if (e.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
            return null;
        if (!e.TryGetProperty("tag_name", out var tagElement))
            return null;

        var tag = tagElement.GetString();
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        // タグは "v1.1.0" の形を想定。PluginVersion は "v" を解釈しないので落とす。
        if (!PluginVersion.TryParse(tag.TrimStart('v', 'V'), out var version))
            return null;

        return new ReleaseInfo
        {
            Version = version,
            Tag = tag,
            Title = (e.TryGetProperty("name", out var n) ? n.GetString() : null) ?? "",
            Body = (e.TryGetProperty("body", out var b) ? b.GetString() : null) ?? "",
        };
    }

    /// <summary>
    /// リリースノートの書き出しを平文で取り出す。
    ///
    /// <para>
    /// 本文は Markdown で、画像・バッジ・HTML タグが冒頭に置かれることが多い
    /// （実際に他プラグインのリリースでは <c>&lt;img&gt;</c> から始まっていた）。
    /// メッセージボックスは平文しか出せないので、飾りを飛ばして最初の段落だけを取る。
    /// </para>
    ///
    /// <para>
    /// <b>リリースノートは見出しの前に2〜3行の要約段落を置くこと。</b>
    /// いきなり見出しから始めると、ここで拾えるのが見出し1行だけになる。
    /// </para>
    /// </summary>
    public string GetSummary(int maxLines = 4, int maxChars = 240)
    {
        if (string.IsNullOrWhiteSpace(Body))
            return "";

        var sb = new StringBuilder();
        var lines = 0;

        foreach (var raw in Body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();

            if (sb.Length == 0)
            {
                // 本文が始まるまでは飾りを読み飛ばす
                if (line.Length == 0) continue;
                if (line.StartsWith('<')) continue;          // HTML タグ（画像など）
                if (line.StartsWith("![")) continue;         // 画像
                if (line.StartsWith("---") || line.StartsWith("===")) continue;
            }
            else if (line.Length == 0)
            {
                break; // 最初の段落の終わり
            }

            var text = ToPlainText(line);
            if (text.Length == 0)
                continue;

            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(text);

            if (++lines >= maxLines)
                break;
        }

        var summary = sb.ToString();
        return summary.Length > maxChars ? summary[..maxChars].TrimEnd() + "…" : summary;
    }

    /// <summary>Markdown の記法を落として平文にする。厳密な変換は不要なので必要な分だけ。</summary>
    static string ToPlainText(string line)
    {
        line = HeadingPattern().Replace(line, "");        // 見出しの #
        line = ListPattern().Replace(line, "・");          // 箇条書き
        line = LinkPattern().Replace(line, "$1");         // [表示](URL) → 表示
        line = EmphasisPattern().Replace(line, "");       // ** __ ` *
        return line.Trim();
    }

    [GeneratedRegex(@"^#{1,6}\s*")] private static partial Regex HeadingPattern();
    [GeneratedRegex(@"^[-*+]\s+")] private static partial Regex ListPattern();
    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")] private static partial Regex LinkPattern();
    [GeneratedRegex(@"\*\*|__|`|\*")] private static partial Regex EmphasisPattern();
}
