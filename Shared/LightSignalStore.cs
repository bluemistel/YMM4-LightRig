using System.Numerics;
using YukkuriMovieMaker.Player.Video;

namespace LightRig.Shared;

/// <summary>
/// シーン光源ターゲット → 消費エフェクト（リムライト等）間で光源状態を受け渡す共有ストア。
/// YMM4 プラグイン API には他アイテムの状態を問い合わせる手段が無いため、
/// 光源側の「シーン光源ターゲット」エフェクトが毎フレーム発信した <see cref="LightState"/> をここに保持する。
///
/// <b>同じチャンネルに光源をいくつ置いてもよい。</b>消費側は <see cref="TryResolve"/> で
/// 距離減衰を重みにした合成結果を受け取るので、街灯が並ぶような構図でも
/// チャンネルを切り替えずに近い光源から強く光を受けられる。
///
/// 選択規則・Usage/フレームまわりの注意は <see cref="FrameSignalStore{T}"/> を参照。
/// </summary>
internal static class LightSignalStore
{
    // 光源は「同じフレームに違う値が来た＝位置などが編集された」を検出して
    // 古い履歴を捨てる（再生開始時に前の光源位置がちらつくのを防ぐ）。
    static readonly FrameSignalStore<LightState> store = new(EqualityComparer<LightState>.Default);

    /// <summary>
    /// 光源状態を発信する。<paramref name="frame"/> は <c>TimelinePosition.Frame</c>、
    /// <paramref name="validFrom"/>/<paramref name="validTo"/> は光源アイテムが存在する
    /// タイムライン範囲、<paramref name="publisher"/> は発信側エフェクトのアイテムを渡すこと。
    /// </summary>
    public static void Publish(
        Guid sceneId, TimelineSourceUsage usage, LightChannel channel,
        long frame, long validFrom, long validTo, bool isHeld, object publisher, in LightState state)
        => store.Publish(sceneId, usage, channel, frame, validFrom, validTo, isHeld, publisher, state);

    /// <summary>
    /// 同一チャンネルの光源をすべて集め、立ち絵の位置に対する実効的な光へ合成して返す。
    /// <paramref name="frame"/> は消費側の <c>TimelinePosition.Frame</c>（ゆらぎの位相にも使う）。
    /// </summary>
    public static bool TryResolve(
        Guid sceneId, TimelineSourceUsage usage, LightChannel channel,
        long frame, int fps, bool isHeld, Vector2 itemPos, out LightMath.ResolvedLight resolved)
    {
        var buffer = perThreadBuffer ??= new List<LightState>(8);
        if (!store.TryGetAll(sceneId, usage, channel, frame, isHeld, buffer))
        {
            resolved = default;
            return false;
        }
        return LightMath.Combine(buffer, itemPos, frame, fps, out resolved);
    }

    // 合成のたびに List を確保しないための作業用バッファ。
    // YMM4 は複数スレッドから描画しうるのでスレッドごとに持つ。
    [ThreadStatic] static List<LightState>? perThreadBuffer;
}
