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

    /// <summary>
    /// セルごとの被覆率（0..1）。そのセルの画素の平均アルファで、
    /// <b>「この場所にこの画像が実際に存在するか」</b>を表す。
    /// 手前に重ねる透過画像（窓枠・前景オブジェクト等）は大部分が透明なので、
    /// 透明な場所では<b>その後ろの画像のサンプラーを使う</b>ための判断材料になる。
    /// 取得できていない場合は null。
    /// </summary>
    public float[]? GridCoverage { get; init; }

    /// <summary>画像全体の平均アルファ（0..1）。</summary>
    public float Coverage { get; init; }

    /// <summary>指定シーン座標におけるこの画像の被覆率。矩形外は 0。</summary>
    public float CoverageAt(Vector2 scenePos)
    {
        if (GridCoverage is not { Length: GridSize * GridSize } || !HasGrid)
            return Coverage;

        var t = (scenePos - RectMin) / RectSize;
        if (t.X < 0f || t.X > 1f || t.Y < 0f || t.Y > 1f)
            return 0f;

        int gx = Math.Clamp((int)(t.X * GridSize), 0, GridSize - 1);
        int gy = Math.Clamp((int)(t.Y * GridSize), 0, GridSize - 1);
        return GridCoverage[gy * GridSize + gx];
    }

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
    // 比較子は渡さない。AmbientState は Grid（配列）を持ち毎回別インスタンスになるため、
    // 「同じフレームで値が変わった＝編集」の判定が常に真になって履歴が消えてしまう。
    static readonly FrameSignalStore<AmbientState> store = new();

    /// <summary><paramref name="frame"/> は <c>TimelinePosition.Frame</c> を渡すこと。</summary>
    public static void Publish(
        Guid sceneId, TimelineSourceUsage usage, LightChannel channel,
        long frame, long validFrom, long validTo, bool isHeld, object publisher, in AmbientState state)
        => store.Publish(sceneId, usage, channel, frame, validFrom, validTo, isHeld, publisher, state);

    /// <summary>
    /// 背景色を取得する。<paramref name="frame"/> は <c>TimelinePosition.Frame</c>。
    ///
    /// <para>
    /// 【複数の背景アイテムに同じチャンネルのサンプラーを付けてよい（2026-09）】
    /// 背景を複数の画像で組む構成では、同一チャンネルに複数のサンプラーが発信する。
    /// 単に1つ返すと <c>ConcurrentDictionary</c> の列挙順（＝不定）で決まってしまい、
    /// どの画像の色が来るか分からず、フレームによって入れ替わってちらつく。
    /// <b><paramref name="itemPos"/>（消費側のシーン座標）を含む背景を選ぶ</b>ことで
    /// 「自分の背後にある背景の色」が決定的に得られる。
    /// </para>
    /// </summary>
    public static bool TryGetState(
        Guid sceneId, TimelineSourceUsage usage, LightChannel channel,
        long frame, bool isHeld, Vector2 itemPos, out AmbientState state)
    {
        var buffer = perThreadBuffer ??= new List<AmbientState>(4);
        if (!store.TryGetAll(sceneId, usage, channel, frame, isHeld, buffer) || buffer.Count == 0)
        {
            state = default;
            return false;
        }

        state = Select(buffer, itemPos);
        return true;
    }

    /// <summary>
    /// 消費側の位置に最も相応しい背景を選ぶ。
    ///
    /// <para>
    /// 【被覆率を最優先にする（2026-09・実機の不具合）】
    /// 背景を複数枚で組むとき、<b>手前の画像はほぼ必ず透過画像</b>（窓枠・前景オブジェクト等）になる。
    /// 矩形の大小だけで選ぶと、手前の透過画像が広い矩形を持っているせいで
    /// <b>その場所が透明なのに選ばれてしまい</b>、後ろの背景の色が使われない。
    /// そこで<b>その位置に実際に画素があるか（被覆率）</b>を先に見る。
    /// </para>
    ///
    /// 順序は「被覆のあるもののうち最も小さい矩形 → 被覆が最大のもの → 矩形の中心が最も近いもの」。
    /// </summary>
    static AmbientState Select(List<AmbientState> candidates, Vector2 itemPos)
    {
        if (candidates.Count == 1)
            return candidates[0];

        // その位置に実体がある（透明ではない）とみなす下限。
        // 縮小時に縁がぼけるので、わずかに掛かっているだけの画像は選ばない。
        const float CoverageThreshold = 0.35f;

        AmbientState bestCovered = default;
        float bestArea = float.MaxValue;
        var hasCovered = false;

        AmbientState bestCoverage = candidates[0];
        float topCoverage = -1f;

        AmbientState bestNear = candidates[0];
        float bestDistance = float.MaxValue;

        foreach (var c in candidates)
        {
            if (!c.HasGrid)
                continue;

            var min = c.RectMin;
            var max = c.RectMin + c.RectSize;
            var inside = itemPos.X >= min.X && itemPos.X <= max.X
                      && itemPos.Y >= min.Y && itemPos.Y <= max.Y;

            if (inside)
            {
                float coverage = c.CoverageAt(itemPos);
                if (coverage > topCoverage)
                {
                    topCoverage = coverage;
                    bestCoverage = c;
                }

                if (coverage >= CoverageThreshold)
                {
                    // 十分に覆っているものの中では、より局所的な（小さい）画像を優先する。
                    float area = c.RectSize.X * c.RectSize.Y;
                    if (!hasCovered || area < bestArea)
                    {
                        bestCovered = c;
                        bestArea = area;
                        hasCovered = true;
                    }
                }
                continue;
            }

            // どれにも入っていない場合の保険
            float d = Vector2.DistanceSquared(c.RectMin + c.RectSize * 0.5f, itemPos);
            if (d < bestDistance)
            {
                bestDistance = d;
                bestNear = c;
            }
        }

        if (hasCovered)
            return bestCovered;
        if (topCoverage >= 0f)
            return bestCoverage;
        return bestNear;
    }

    /// <summary>代表色だけが必要な消費側向けの簡易版。</summary>
    public static bool TryGet(
        Guid sceneId, TimelineSourceUsage usage, LightChannel channel,
        long frame, bool isHeld, Vector2 itemPos, out Vector4 color)
    {
        if (TryGetState(sceneId, usage, channel, frame, isHeld, itemPos, out var state))
        {
            color = state.Color;
            return true;
        }
        color = default;
        return false;
    }

    // 選択のたびに List を確保しないための作業用バッファ（描画は複数スレッドから走りうる）。
    [ThreadStatic] static List<AmbientState>? perThreadBuffer;
}
