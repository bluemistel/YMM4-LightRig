using System.Numerics;
using YukkuriMovieMaker.Player.Video;

namespace LightRig.Shared;

/// <summary>
/// 環境光サンプラーが発信する背景の色情報。
///
/// <para><see cref="Color"/> は従来どおり「輝度しきい値以上の画素の平均色（代表色1つ）」。</para>
/// <para>
/// <see cref="Grid"/> は背景を <see cref="GridSize"/>×<see cref="GridSize"/> に区切った
/// セルごとの平均色（しきい値を掛けない生の色）で、消費側が自分の位置に応じた背景色を
/// 引けるようにするためのもの。<see cref="RectMin"/> / <see cref="RectSize"/> は
/// そのグリッドが覆う背景アイテムのシーン矩形（Draw 座標系, px）。
/// </para>
///
/// YMM4 の映像エフェクトは自アイテムの画像しか入力に持たないため、
/// 「立ち絵の背後の背景色」はこのグリッド経由でしか取得できない。
/// </summary>
public readonly struct AmbientState
{
    /// <summary>グリッドの1辺のセル数。</summary>
    public const int GridSize = 3;

    /// <summary>
    /// 実際に読み戻しを行ったフレーム。サンプリングは数フレームおきに間引くため、
    /// この値を配っているフレーム（ストアのキー）とは一致しないことがある。
    /// 選択には使わない（ストア側がフレームをキーに持つ）。診断・将来の補間用の記録。
    /// </summary>
    public long Frame { get; init; }

    /// <summary>代表色（輝度しきい値以上の画素の平均, 非プリマルチプライド sRGB 0..1）。</summary>
    public Vector4 Color { get; init; }

    /// <summary>
    /// 背景を GridSize×GridSize に区切ったセル平均色（row-major, 長さ GridSize^2）。
    /// 取得できていない場合は null。発信後は変更しないこと（値として共有される）。
    /// </summary>
    public Vector3[]? Grid { get; init; }

    /// <summary>グリッドが覆う背景アイテムのシーン矩形の左上（px）。</summary>
    public Vector2 RectMin { get; init; }

    /// <summary>グリッドが覆う背景アイテムのシーン矩形のサイズ（px）。0 以下ならグリッド無効。</summary>
    public Vector2 RectSize { get; init; }

    /// <summary>グリッドと矩形が揃っていて位置対応付けに使えるか。</summary>
    public bool HasGrid =>
        Grid is { Length: GridSize * GridSize } && RectSize.X > 1f && RectSize.Y > 1f;
}

/// <summary>
/// 環境光サンプラー → 消費エフェクト間で「背景の色情報」を受け渡す共有ストア。
/// 光源の位置・色を扱う <see cref="LightSignalStore"/> とは別系統にして、
/// 環境光サンプラーと光源ターゲットが同じチャンネルを使っても互いを上書きしないようにする。
///
/// 選択規則・Usage/フレームまわりの注意は <see cref="FrameSignalStore{T}"/> を参照。
/// </summary>
internal static class AmbientSignalStore
{
    static readonly FrameSignalStore<AmbientState> store = new();

    /// <summary><paramref name="frame"/> は <c>TimelinePosition.Frame</c> を渡すこと。</summary>
    public static void Publish(Guid sceneId, TimelineSourceUsage usage, LightChannel channel, long frame, object publisher, in AmbientState state)
        => store.Publish(sceneId, usage, channel, frame, publisher, state);

    /// <summary><paramref name="frame"/> は <c>TimelinePosition.Frame</c> を渡すこと。</summary>
    public static bool TryGetState(Guid sceneId, TimelineSourceUsage usage, LightChannel channel, long frame, out AmbientState state)
        => store.TryGet(sceneId, usage, channel, frame, out state);

    /// <summary>代表色だけが必要な消費側（リライティングの環境光ミックス等）向けの簡易版。</summary>
    public static bool TryGet(Guid sceneId, TimelineSourceUsage usage, LightChannel channel, long frame, out Vector4 color)
    {
        if (TryGetState(sceneId, usage, channel, frame, out var state))
        {
            color = state.Color;
            return true;
        }
        color = default;
        return false;
    }
}
