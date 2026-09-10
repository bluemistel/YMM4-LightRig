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
/// 【有効範囲（2026-09・実機の不具合）】発信値には<b>発信元アイテムがタイムライン上で
/// 存在する範囲</b>を持たせ、現在フレームがその範囲外なら選ばない。
/// これが無いと、たき火のような<b>途中で終わるアイテムの光が終了後も残り続ける</b>
/// （「最も近いフレーム」フォールバックが最後に発信されたフレームを無限に拾うため）。
/// 範囲で弾くので、キャッシュで再実行されない場合（＝範囲内なのに値が無い）は
/// 従来どおり近いフレームの値を使え、退行しない。
/// </para>
///
/// <para>
/// 【同一フレームで値が変わったら履歴を捨てる（2026-09・実機の不具合）】
/// 光源の位置を編集すると、履歴に残った各フレームの値は<b>すべて編集前のもの</b>になる。
/// 発信側は再描画されたフレームから順に上書きしていくので、まだ上書きされていない
/// フレームでは古い光源位置が読まれ、<b>再生開始時に前の位置の光や影が一瞬描画される</b>。
/// 「同じフレームに対して前回と違う値が来た」＝編集された、と判断して
/// その発信元の他フレームの履歴を捨てると、ちらつきは最初の1フレームだけになる
/// （同一フレーム内の評価順は保証されないので 0 にはできない）。
/// アニメーションによる正常な変化は<b>別フレームへの発信</b>なので誤検知しない。
/// </para>
///
/// <para>
/// 【タイミング注意】同一フレーム内での「発信側 → 消費側」の評価順は保証されない。
/// 消費側は同一フレームの鮮度を前提にせず、直近既知値で許容する設計にすること。
/// </para>
/// </summary>
internal sealed class FrameSignalStore<T>(IEqualityComparer<T>? changeComparer = null)
{
    /// <summary>
    /// 「同じフレームに違う値が来た＝編集された」の判定に使う比較子。
    /// null なら履歴の破棄を行わない（参照型フィールドを持つ値など、
    /// 毎回別インスタンスになって誤検知する型はこちらにする）。
    /// </summary>
    readonly IEqualityComparer<T>? changeComparer = changeComparer;

    /// <summary>チャンネルごとに保持する測定フレーム数の上限。超えた分は古い発信から捨てる。</summary>
    const int MaxHistory = 64;

    long sequence;

    readonly ConcurrentDictionary<(Guid SceneId, LightChannel Channel), ChannelSignals> channels = new();

    /// <summary>発信された値と、その発信元が存在するタイムライン上の範囲 [ValidFrom, ValidTo)。</summary>
    readonly record struct Entry(T Value, long ValidFrom, long ValidTo, bool IsHeld)
    {
        /// <summary>指定フレームでこの値が有効か。範囲が未指定（To&lt;=From）なら常に有効。</summary>
        public bool CoversFrame(long frame)
            => ValidTo <= ValidFrom || (frame >= ValidFrom && frame < ValidTo);

        /// <summary>
        /// 消費側がこの値を使えるか。
        ///
        /// <para>
        /// 【保持中の値は有効範囲を問わない（2026-09・エンコードでのみ再現した不具合）】
        /// <c>IsHeld</c> ＝「終わったアイテムが場面切り替えのために意図的に描画されている」状態。
        /// このとき描かれているのは<b>凍結された過去の瞬間</b>なので、
        /// 発信元アイテムがタイムライン上に存在するかという情報は意味を持たない。
        /// 範囲で弾くと「最も近いフレーム」のフォールバックが機能せず、
        /// <b>同一フレーム内で発信側が消費側より先に走ったかどうかの運任せ</b>になる。
        /// プレビューは同じフレームを何度も描くので履歴が埋まり自然に直るが、
        /// 1パスしか描かないエンコードでは半分の確率で外れ、立ち絵が固定色のまま白く出る。
        /// </para>
        ///
        /// <para>
        /// ただし<b>保持中の値を使えるのは保持中の描画だけ</b>に限る。
        /// そうしないと、切り替えが終わったあとも前の場面の値が
        /// （範囲を問わないので）永久に拾われてしまう。
        /// </para>
        /// </summary>
        public bool IsUsableAt(long frame, bool consumerHeld)
            => IsHeld ? consumerHeld : CoversFrame(frame);
    }

    /// <summary>同一フレームに発信された値。発信元（エフェクトのアイテム）ごとにスロットを持つ。</summary>
    sealed class FrameSlot
    {
        public readonly ConcurrentDictionary<object, Entry> ByPublisher = new(ReferenceEqualityComparer.Instance);
        public long Seq;

        /// <summary>
        /// この時刻に使える値が1つでもあるか。
        /// <paramref name="requireSameSide"/> が true なら消費側と同じ側の値だけを数える。
        /// </summary>
        public bool HasValueAt(long frame, bool consumerHeld, bool requireSameSide)
        {
            foreach (var e in ByPublisher.Values)
            {
                if (requireSameSide && e.IsHeld != consumerHeld)
                    continue;
                if (e.IsUsableAt(frame, consumerHeld))
                    return true;
            }
            return false;
        }
    }

    sealed class ChannelSignals
    {
        /// <summary>Usage 別の最新値（最後の手段のフォールバック用）。</summary>
        public readonly ConcurrentDictionary<TimelineSourceUsage, (long Seq, Entry Entry)> ByUsage = new();

        /// <summary>タイムライン上のフレーム別の値。Usage は問わない。</summary>
        public readonly ConcurrentDictionary<long, FrameSlot> ByFrame = new();

        public readonly Lock PruneLock = new();
    }

    /// <summary>
    /// 値を発信する。
    /// <paramref name="frame"/> には <c>TimelinePosition.Frame</c> を渡すこと
    /// （消費側と同じ時計でないと一致判定が働かない。<c>ItemPosition</c> ではない）。
    /// <paramref name="validFrom"/> / <paramref name="validTo"/> は<b>発信元アイテムが
    /// タイムライン上に存在する範囲</b>（半開区間）。消費側はこの範囲外のフレームでは
    /// この値を選ばない。範囲が不明なら両方 0 を渡すと常に有効として扱う。
    /// <paramref name="isHeld"/> は <see cref="RenderSide.IsHeld"/>。場面切り替えの前後を見分けるための印。
    /// <paramref name="publisher"/> には<b>発信側エフェクトのアイテム</b>（プロセッサが保持している
    /// <c>item</c>）を渡す。これが同一フレーム内でのスロットの識別子になる。
    /// <b>プロセッサ自身（<c>this</c>）を渡してはいけない。</b>YMM4 は Usage ごとに別のプロセッサを
    /// 作るため、同じ光源が複数スロットを占めて「光源が2個ある」と誤認され明るさが倍になる。
    /// </summary>
    public void Publish(
        Guid sceneId, TimelineSourceUsage usage, LightChannel channel,
        long frame, long validFrom, long validTo, bool isHeld, object publisher, T value)
    {
        var signals = channels.GetOrAdd((sceneId, channel), _ => new ChannelSignals());
        var seq = Interlocked.Increment(ref sequence);

        // 保持中（isHeld）の値は有効範囲を問わずに使われる（Entry.IsUsableAt を参照）。
        // 以前はここで validTo = frame + 1 と「現在フレームだけ」へ伸ばしていたが、
        // それだと各エントリが1フレームしか有効にならず、
        // 同一フレーム内で発信側が先に走ったかどうかの運任せになっていた（エンコードで露見）。
        var entry = new Entry(value, validFrom, validTo, isHeld);

        signals.ByUsage[usage] = (seq, entry);

        var slot = signals.ByFrame.GetOrAdd(frame, _ => new FrameSlot());

        // 【同じフレームに違う値が来た＝設定が編集された】
        // 履歴に残る他フレームの値はすべて編集前のものなので捨てる。
        // これをしないと、まだ再描画されていないフレームで古い光源位置が読まれ、
        // 再生開始時に前の位置の光や影がちらつく。
        // 【場面切り替え中は誤爆する】前後2本の描画は同じアイテムを発信元キーとして共有するため、
        // 同じフレームへ違う値（例: 環境光追従で前後の背景色が違う）を書き合う。
        // これを編集と誤認して履歴を捨てると、1パスしか描かないエンコードで値が消える。
        // 側が一致するときだけ編集と見なす。
        if (changeComparer is not null
            && slot.ByPublisher.TryGetValue(publisher, out var previous)
            && previous.IsHeld == isHeld
            && !changeComparer.Equals(previous.Value, value))
        {
            PurgePublisher(signals, publisher, frame);
        }

        slot.ByPublisher[publisher] = entry;
        slot.Seq = seq;

        if (signals.ByFrame.Count > MaxHistory)
            Prune(signals);
    }

    /// <summary>指定した発信元の履歴を <paramref name="keepFrame"/> 以外のフレームから取り除く。</summary>
    static void PurgePublisher(ChannelSignals signals, object publisher, long keepFrame)
    {
        foreach (var (frame, slot) in signals.ByFrame)
        {
            if (frame == keepFrame)
                continue;
            slot.ByPublisher.TryRemove(publisher, out _);
        }
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
    /// 現在フレームに最も適したフレームスロットを探す。「完全一致 → 最も近いフレーム」の順。
    /// 使える値（<see cref="Entry.IsUsableAt"/>）が入っているスロットだけを対象にする。
    /// <paramref name="requireSameSide"/> が true なら、場面切り替えの同じ側の値を持つスロットに限る。
    /// 見つからなければ null。
    /// </summary>
    static FrameSlot? FindSlot(ChannelSignals signals, long frame, bool consumerHeld, bool requireSameSide)
    {
        if (signals.ByFrame.TryGetValue(frame, out var exact)
            && exact.HasValueAt(frame, consumerHeld, requireSameSide))
            return exact;

        // 最も近いフレームの値。
        // 背景アイテムがキャッシュされて再実行されなくても、シーク時に一度測っていれば拾える。
        // 保持中の値は範囲を問わないので、場面切り替えの間ずっとここで拾える
        // （同一フレーム内の評価順に依存しなくなる）。
        // 距離が同じなら新しい発信（Seq が大きい方）を採る。
        long bestDistance = long.MaxValue, bestSeq = -1;
        FrameSlot? nearest = null;
        foreach (var entry in signals.ByFrame)
        {
            // 現在フレームに存在しないアイテムの値は拾わない
            // （終了したたき火の光が残り続けるのを防ぐ）。
            if (!entry.Value.HasValueAt(frame, consumerHeld, requireSameSide))
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
    public bool TryGet(Guid sceneId, TimelineSourceUsage usage, LightChannel channel, long frame, bool isHeld, out T value)
    {
        value = default!;
        if (!channels.TryGetValue((sceneId, channel), out var signals))
            return false;

        // 同じ側（場面切り替えの前／後）の値を優先し、無ければ側を問わず拾う。
        // フォールバックを残すので、以前 true を返していた場面で false にはならない。
        var slot = FindSlot(signals, frame, isHeld, requireSameSide: true)
                ?? FindSlot(signals, frame, isHeld, requireSameSide: false);
        if (slot is not null)
        {
            foreach (var e in slot.ByPublisher.Values)
            {
                if (e.IsHeld == isHeld && e.IsUsableAt(frame, isHeld))
                {
                    value = e.Value;
                    return true;
                }
            }
            foreach (var e in slot.ByPublisher.Values)
            {
                if (!e.IsUsableAt(frame, isHeld))
                    continue;
                value = e.Value;
                return true;
            }
        }

        // 同じ Usage の最新値
        if (signals.ByUsage.TryGetValue(usage, out var sameUsage) && sameUsage.Entry.IsUsableAt(frame, isHeld))
        {
            value = sameUsage.Entry.Value;
            return true;
        }

        // 最後の手段（同一シーン・同一チャンネルで最後に発信された値）
        long newestSeq = -1;
        var foundAny = false;
        foreach (var entry in signals.ByUsage.Values)
        {
            if (entry.Seq > newestSeq && entry.Entry.IsUsableAt(frame, isHeld))
            {
                newestSeq = entry.Seq;
                value = entry.Entry.Value;
                foundAny = true;
            }
        }
        return foundAny;
    }

    /// <summary>
    /// 同一フレームに発信されたすべての値を取得する（複数光源の合成用）。
    /// 見つからない場合はフォールバックとして単一値を1件だけ返す。
    /// </summary>
    public bool TryGetAll(Guid sceneId, TimelineSourceUsage usage, LightChannel channel, long frame, bool isHeld, List<T> destination)
    {
        destination.Clear();
        if (!channels.TryGetValue((sceneId, channel), out var signals))
            return false;

        // 同じ側の値だけを集める。無ければ側を問わず集める。
        var slot = FindSlot(signals, frame, isHeld, requireSameSide: true)
                ?? FindSlot(signals, frame, isHeld, requireSameSide: false);
        if (slot is not null)
        {
            foreach (var e in slot.ByPublisher.Values)
            {
                if (e.IsHeld == isHeld && e.IsUsableAt(frame, isHeld))
                    destination.Add(e.Value);
            }
            if (destination.Count > 0)
                return true;

            foreach (var e in slot.ByPublisher.Values)
            {
                if (e.IsUsableAt(frame, isHeld))
                    destination.Add(e.Value);
            }
            if (destination.Count > 0)
                return true;
        }

        if (TryGet(sceneId, usage, channel, frame, isHeld, out var single))
        {
            destination.Add(single);
            return true;
        }
        return false;
    }
}
