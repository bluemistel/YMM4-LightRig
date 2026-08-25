using System.Collections.Concurrent;
using System.Reflection;

namespace LightRig.Effects;

/// <summary>
/// ビルド時に fxc.exe でコンパイルされ、埋め込みリソース（*.cso）として
/// アセンブリに格納されたピクセルシェーダーのバイトコードを読み出す共通ローダー。
///
/// 埋め込み名は csproj の IncludeCompiledShaders ターゲットにより
/// "LightRig.Shaders.&lt;...&gt;.&lt;Name&gt;PS.cso" の形式になる。
/// ここではファイル名サフィックス（例 "SceneRimLightPS.cso"）で一致検索する。
/// </summary>
internal static class ShaderResourceLoader
{
    private static readonly ConcurrentDictionary<string, byte[]> cache = new(StringComparer.Ordinal);

    /// <summary>
    /// 指定サフィックス（例 "SceneRimLightPS.cso"）で終わる埋め込みシェーダーを取得する。
    /// 一度読み込んだバイト列はキャッシュされる。
    /// </summary>
    public static byte[] Get(string csoSuffix) => cache.GetOrAdd(csoSuffix, Load);

    private static byte[] Load(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames();

        string? matched = null;
        foreach (var name in names)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                matched = name;
                break;
            }
        }

        if (matched is null)
            throw new InvalidOperationException(
                $"埋め込みシェーダーリソース '{suffix}' がアセンブリ内に見つかりません。" +
                "Shaders フォルダに対応する .hlsl が存在し、ビルド時にコンパイルされているか確認してください。");

        using var stream = assembly.GetManifestResourceStream(matched)!;
        var buffer = new byte[(int)stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
