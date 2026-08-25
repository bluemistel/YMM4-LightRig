using System.Collections.Concurrent;
using YukkuriMovieMaker.Player.Video;

namespace LightRig.Shared;

/// <summary>
/// シーン光源ターゲット → 消費エフェクト（リムライト等）間で光源状態を受け渡す共有ストア。
/// YMM4 プラグイン API には他アイテムの状態を問い合わせる手段が無いため、
/// 光源側の「シーン光源ターゲット」エフェクトが毎フレーム発信した <see cref="LightState"/> をここに保持する。
///
/// 2DCamera の AfSignalStore を踏襲した設計。キーはシーンID・チャンネルで分離し、
/// その中で用途（Usage: プレビュー/エクスポート等）別に最新値を保持する。
/// 受信側はまず自分と同じ Usage の値を使い、無ければ同一シーン・同一チャンネルの
/// 最新の発信値へフォールバックする。
///
/// 【なぜフォールバックが要るか】
/// YMM4 は一時停止時などに別 Usage で再描画することがあり、その Usage でまだ光源ターゲットが
/// 評価されていないと値が存在しない。フォールバックが無いとその瞬間だけ光源が消える。
/// エクスポート時は毎フレーム自 Usage の値が発信されるためフォールバックは混入しない。
///
/// 【タイミング注意】
/// 同一フレーム内での「発信側 → 消費側」の評価順は保証されない。消費側は「同一フレームの鮮度」を
/// 前提にせず、直近既知値（last-known-value）で許容する設計にすること。
///
/// 【フレーム一致を優先する理由】
/// 「同じ Usage → 無ければ最新 Seq」だけで選ぶと、再生を止めて別 Usage で描き直された瞬間に
/// <b>再生中の最後に発信された「別の時刻」の値</b>を拾ってしまう。
/// そのため発信値にフレームを刻み、同じフレームで発信された値を優先する。
/// フォールバックの順序は保つので、以前 true を返していた場面で false になることはない。
/// </summary>
internal static class LightSignalStore
{
    static long sequence;

    static readonly ConcurrentDictionary<
        (Guid SceneId, LightChannel Channel),
        ConcurrentDictionary<TimelineSourceUsage, (long Seq, LightState State)>> signals = new();

    public static void Publish(Guid sceneId, TimelineSourceUsage usage, LightChannel channel, in LightState state)
    {
        var perUsage = signals.GetOrAdd((sceneId, channel), _ => new());
        perUsage[usage] = (Interlocked.Increment(ref sequence), state);
    }

    /// <summary>
    /// 光源状態を取得する。<paramref name="frame"/> には消費側の
    /// <c>TimelinePosition.Frame</c>（発信側と同じ時計）を渡すこと。
    /// 優先順位は「同 Usage かつ同フレーム → 同フレーム（別 Usage）→ 同 Usage → 最新 Seq」。
    /// </summary>
    public static bool TryGet(Guid sceneId, TimelineSourceUsage usage, LightChannel channel, long frame, out LightState state)
    {
        state = default;
        if (!signals.TryGetValue((sceneId, channel), out var perUsage))
            return false;

        // 自分と同じ用途、かつ同じフレームで発信された値を最優先
        var hasSameUsage = perUsage.TryGetValue(usage, out var sameUsage);
        if (hasSameUsage && sameUsage.State.Frame == frame)
        {
            state = sameUsage.State;
            return true;
        }

        long bestFrameSeq = -1, bestSeq = -1;
        bool foundSameFrame = false, foundAny = false;
        LightState sameFrameState = default, newestState = default;

        foreach (var entry in perUsage.Values)
        {
            // 別 Usage でも同じフレームで発信されていればそちらを使う（一時停止時の再描画）
            if (entry.State.Frame == frame && entry.Seq > bestFrameSeq)
            {
                bestFrameSeq = entry.Seq;
                sameFrameState = entry.State;
                foundSameFrame = true;
            }
            if (entry.Seq > bestSeq)
            {
                bestSeq = entry.Seq;
                newestState = entry.State;
                foundAny = true;
            }
        }

        if (foundSameFrame) { state = sameFrameState; return true; }
        if (hasSameUsage) { state = sameUsage.State; return true; }
        if (foundAny) { state = newestState; return true; }
        return false;
    }
}
