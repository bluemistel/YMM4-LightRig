using System.Collections.Concurrent;
using YukkuriMovieMaker.Player.Video;

namespace LightRig.Shared;

/// <summary>
/// 発信側エフェクト → 消費側エフェクト間で値を受け渡す共有ストアの共通実装。
/// <see cref="LightSignalStore"/> と <see cref="AmbientSignalStore"/> が中身の型だけ変えて使う。
///
/// <para>
/// 【なぜ Usage 別の最新値だけでは足りないか（2026-08・実機の不具合）】
/// <see cref="TimelineSourceUsage"/> は Playing / Paused / Exporting の3値で、
/// 一時停止すると YMM4 は Paused として描き直す。ところが
/// <b>YMM4 はアイテムの描画結果をキャッシュする</b>ため、静止している背景アイテムの
/// エフェクトチェーンは Paused で再実行されないことがある。
/// つまり「Paused の値が無い」だけでなく<b>現在時刻の値がどの Usage にも存在しない</b>状態が起きる。
/// この状態で「同じ Usage → 無ければ最新 Seq」と選ぶと、
/// <b>再生中の最後に発信された別の時刻の値</b>を拾ってしまう
/// （夕方のシーンから真夜中へシークして停止すると、夕方の環境光が乗ったまま固まる）。
/// フレームを刻むだけでは直らない。存在しない値は選べないため。
/// </para>
///
/// <para>
/// 【対策】発信値を<b>フレームをキーにした履歴</b>として保持する。
/// 背景アイテムがシークで一度でも描画されればその時刻の測定値が履歴に残るので、
/// 以降キャッシュで再実行されなくても消費側は正しい値を引ける。
/// 消費側は「完全一致 → 最も近いフレーム → Usage 別の最新 → 全体の最新」の順で探す。
/// フォールバックの段は残してあるので、以前 true を返していた場面で false にはならない。
/// </para>
///
/// <para>
/// 【複数の発信元】同一フレームに複数の発信元（例: 街灯ごとに置いた光源ターゲット）が
/// 存在しうるので、フレームごとの値は<b>発信元をキーにした辞書</b>で持つ。
/// 発信元が自分のスロットだけを更新するため、同じチャンネルへ何個置いても上書きされない。
/// 単一の値だけが要る消費側（環境光）は <see cref="TryGet"/>、
/// 全部が要る消費側（光源の合成）は <see cref="TryGetAll"/> を使う。
/// </para>
///
/// <para>
/// 【タイミング注意】同一フレーム内での「発信側 → 消費側」の評価順は保証されない。
/// 消費側は同一フレームの鮮度を前提にせず、直近既知値で許容する設計にすること。
/// </para>
/// </summary>
internal sealed class FrameSignalStore<T>
{
    /// <summary>チャンネルごとに保持する測定フレーム数の上限。超えた分は古い発信から捨てる。</summary>
    const int MaxHistory = 64;

    long sequence;

    readonly ConcurrentDictionary<(Guid SceneId, LightChannel Channel), ChannelSignals> channels = new();

    /// <summary>同一フレームに発信された値。発信元（プロセッサのインスタンス）ごとにスロットを持つ。</summary>
    sealed class FrameSlot
    {
        public readonly ConcurrentDictionary<object, T> ByPublisher = new(ReferenceEqualityComparer.Instance);
        public long Seq;
    }

    sealed class ChannelSignals
    {
        /// <summary>Usage 別の最新値（最後の手段のフォールバック用）。</summary>
        public readonly ConcurrentDictionary<TimelineSourceUsage, (long Seq, T Value)> ByUsage = new();

        /// <summary>タイムライン上のフレーム別の値。Usage は問わない。</summary>
        public readonly ConcurrentDictionary<long, FrameSlot> ByFrame = new();

        public readonly Lock PruneLock = new();
    }

    /// <summary>
    /// 値を発信する。
    /// <paramref name="frame"/> には <c>TimelinePosition.Frame</c> を渡すこと
    /// （消費側と同じ時計でないと一致判定が働かない。<c>ItemPosition</c> ではない）。
    /// <paramref name="publisher"/> には<b>発信側エフェクトのアイテム</b>（プロセッサが保持している
    /// <c>item</c>）を渡す。これが同一フレーム内でのスロットの識別子になる。
    /// <b>プロセッサ自身（<c>this</c>）を渡してはいけない。</b>YMM4 は Usage ごとに別のプロセッサを
    /// 作るため、同じ光源が複数スロットを占めて「光源が2個ある」と誤認され明るさが倍になる。
    /// </summary>
    public void Publish(Guid sceneId, TimelineSourceUsage usage, LightChannel channel, long frame, object publisher, T value)
    {
        var signals = channels.GetOrAdd((sceneId, channel), _ => new ChannelSignals());
        var seq = Interlocked.Increment(ref sequence);

        signals.ByUsage[usage] = (seq, value);

        var slot = signals.ByFrame.GetOrAdd(frame, _ => new FrameSlot());
        slot.ByPublisher[publisher] = value;
        slot.Seq = seq;

        if (signals.ByFrame.Count > MaxHistory)
            Prune(signals);
    }

    /// <summary>古い発信から順に履歴を上限まで削る。</summary>
    static void Prune(ChannelSignals signals)
    {
        lock (signals.PruneLock)
        {
            if (signals.ByFrame.Count <= MaxHistory)
                return;

            // Seq の小さい（古い）ものから捨てる
            var ordered = signals.ByFrame.ToArray();
            Array.Sort(ordered, static (a, b) => a.Value.Seq.CompareTo(b.Value.Seq));
            var removeCount = ordered.Length - MaxHistory;
            for (int i = 0; i < removeCount; i++)
                signals.ByFrame.TryRemove(ordered[i].Key, out _);
        }
    }

    /// <summary>
    /// 現在フレームに最も適したフレームスロットを探す。
    /// 「完全一致 → 最も近いフレーム」の順。見つからなければ null。
    /// </summary>
    static FrameSlot? FindSlot(ChannelSignals signals, long frame)
    {
        if (signals.ByFrame.TryGetValue(frame, out var exact) && !exact.ByPublisher.IsEmpty)
            return exact;

        // 最も近いフレームの値。
        // 背景アイテムがキャッシュされて再実行されなくても、シーク時に一度測っていれば拾える。
        // 距離が同じなら新しい発信（Seq が大きい方）を採る。
        long bestDistance = long.MaxValue, bestSeq = -1;
        FrameSlot? nearest = null;
        foreach (var entry in signals.ByFrame)
        {
            if (entry.Value.ByPublisher.IsEmpty)
                continue;

            // Math.Abs は long.MinValue で例外になるため、引き算の向きで絶対値を作る
            long distance = entry.Key >= frame ? entry.Key - frame : frame - entry.Key;
            if (distance < bestDistance || (distance == bestDistance && entry.Value.Seq > bestSeq))
            {
                bestDistance = distance;
                bestSeq = entry.Value.Seq;
                nearest = entry.Value;
            }
        }
        return nearest;
    }

    /// <summary>
    /// 値を1つ取得する。<paramref name="frame"/> には消費側の <c>TimelinePosition.Frame</c> を渡すこと。
    /// 同一フレームに複数の発信元がある場合はそのうちの1つを返す（環境光のように1つで足りる用途向け）。
    /// </summary>
    public bool TryGet(Guid sceneId, TimelineSourceUsage usage, LightChannel channel, long frame, out T value)
    {
        value = default!;
        if (!channels.TryGetValue((sceneId, channel), out var signals))
            return false;

        var slot = FindSlot(signals, frame);
        if (slot is not null)
        {
            foreach (var v in slot.ByPublisher.Values)
            {
                value = v;
                return true;
            }
        }

        // 同じ Usage の最新値
        if (signals.ByUsage.TryGetValue(usage, out var sameUsage))
        {
            value = sameUsage.Value;
            return true;
        }

        // 最後の手段（同一シーン・同一チャンネルで最後に発信された値）
        long newestSeq = -1;
        var foundAny = false;
        foreach (var entry in signals.ByUsage.Values)
        {
            if (entry.Seq > newestSeq)
            {
                newestSeq = entry.Seq;
                value = entry.Value;
                foundAny = true;
            }
        }
        return foundAny;
    }

    /// <summary>
    /// 同一フレームに発信されたすべての値を取得する（複数光源の合成用）。
    /// 見つからない場合はフォールバックとして単一値を1件だけ返す。
    /// </summary>
    public bool TryGetAll(Guid sceneId, TimelineSourceUsage usage, LightChannel channel, long frame, List<T> destination)
    {
        destination.Clear();
        if (!channels.TryGetValue((sceneId, channel), out var signals))
            return false;

        var slot = FindSlot(signals, frame);
        if (slot is not null)
        {
            foreach (var v in slot.ByPublisher.Values)
                destination.Add(v);
            if (destination.Count > 0)
                return true;
        }

        if (TryGet(sceneId, usage, channel, frame, out var single))
        {
            destination.Add(single);
            return true;
        }
        return false;
    }
}
