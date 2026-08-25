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

    public static bool TryGet(Guid sceneId, TimelineSourceUsage usage, LightChannel channel, out LightState state)
    {
        state = default;
        if (!signals.TryGetValue((sceneId, channel), out var perUsage))
            return false;

        // 自分と同じ用途の値を最優先
        if (perUsage.TryGetValue(usage, out var exact))
        {
            state = exact.State;
            return true;
        }

        // 無ければ同一シーン・同一チャンネルで最後に発信された値へフォールバック
        var found = false;
        long bestSeq = -1;
        foreach (var entry in perUsage.Values)
        {
            if (entry.Seq > bestSeq)
            {
                bestSeq = entry.Seq;
                state = entry.State;
                found = true;
            }
        }
        return found;
    }
}
