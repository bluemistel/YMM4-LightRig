using System.Numerics;

namespace LightRig.Shared;

/// <summary>
/// シーン光源の座標変換・ゆらぎ計算を集約する共通ヘルパー。
/// 消費エフェクト（リムライト・シェーディング・リライティング）はすべてここを経由することで、
/// ワールド座標↔スクリーン座標の向きの規約を1箇所に閉じ込める（符号ミスの一括修正が可能）。
/// </summary>
public static class LightMath
{
    /// <summary>
    /// 立ち絵アイテムから光源へ向かう方向を、シェーダーの UV/スクリーン向き（Y下）で返す（正規化済み）。
    ///
    /// 【座標系の規約（実機で確認済み）】
    /// YMM4 の drawDesc.Draw は Y+ が下方向で、シェーダーの UV オフセット（uv.zw）と同じ向き。
    /// したがって Y の反転は不要で、差分をそのまま使う。
    /// 光源も立ち絵も同じ drawDesc.Draw 由来なので同一座標系で比較できる。
    /// 遠方のシーン光源は方向性ライトとして扱う（アイテム内で方向一定）ため、これで十分。
    ///
    /// 向きの規約が変わった場合はここの符号だけ直せば全エフェクトに反映される。
    /// 消費エフェクト側の「角度オフセット」で実運用の微調整もできる。
    /// </summary>
    public static Vector2 ScreenDirTowardLight(Vector2 lightWorld, Vector2 itemWorld)
    {
        var d = lightWorld - itemWorld; // Y+ が下で UV と同じ向きなので反転不要
        var len = d.Length();
        return len > 1e-4f ? d / len : new Vector2(0f, -1f); // 既定は「上から」
    }

    /// <summary>
    /// 光源の種類を考慮して、立ち絵から光源へ向かうスクリーン方向（Y下, 正規化）を返す。
    /// 消費エフェクトはこちらを使うこと（<see cref="ScreenDirTowardLight"/> を直接呼ばない）。
    ///
    /// 平行光は太陽・月のような無限遠光源なので、アイテムの位置によらず角度が一定になる。
    /// 点光源・スポットは従来どおり位置の差から求める（アイテムごとに向きが変わる）。
    /// </summary>
    public static Vector2 ScreenDir(in LightState light, Vector2 itemWorld)
        => light.Type == LightSourceType.Directional
            ? DirFromAngle(light.Angle)
            : ScreenDirTowardLight(light.Position, itemWorld);

    /// <summary>
    /// 光源から立ち絵までの距離・スポットの円錐による減衰係数（0..1）を返す。
    /// 消費エフェクトは光の強さ（リムの量・陰影の濃さ・影の濃さ）にこれを掛ける。
    ///
    /// - 平行光は距離の概念を持たないので常に 1。
    /// - 到達距離 0 以下は「無限＝減衰なし」。既定値なので、設定しなければ従来どおりの挙動になる。
    /// - 減衰は「<see cref="LightState.FalloffStart"/> までは等倍 → 到達距離にかけて smoothstep で 0」。
    ///   物理的な逆二乗則ではなく「どこまで届くか」を作画的に指定できる形にしている。
    ///
    /// 【指数カーブ（t^n）にしてはいけない】
    /// 指数は値と見た目の対応が直感に反する。n&lt;1 では到達距離の手前までほぼ等倍を保って縁で一気に落ち、
    /// n=1（直線）では光源の真上以外が常に減光されるため「効きが弱い」と感じる。
    /// つまり有効な調整域が両端に偏り、実用的に詰めにくい。
    /// プラトー（等倍領域）＋ smoothstep なら、パラメータは「境界の位置」を素直に動かすだけになる。
    /// </summary>
    public static float Attenuation(in LightState light, Vector2 itemWorld)
    {
        if (light.Type == LightSourceType.Directional)
            return 1f;

        float att = 1f;

        if (light.Range > 0f)
        {
            float u = Vector2.Distance(light.Position, itemWorld) / light.Range;
            // start==1 だと smoothstep が退化するので少し手前で止める（＝ほぼ硬い縁）
            float start = Math.Clamp(light.FalloffStart, 0f, 0.99f);
            att *= 1f - SmoothStep(start, 1f, u);
        }

        if (light.Type == LightSourceType.Spot)
        {
            var toItem = itemWorld - light.Position;
            float len = toItem.Length();
            if (len > 1e-4f)
            {
                // Angle は「光が来る向き」なので、光が進む向き（＝円錐の軸）は符号を反転させる。
                // 角度 0（＝上から来る光）のスポットは真下を照らす。
                var axis = -DirFromAngle(light.Angle);
                float cos = Vector2.Dot(toItem / len, axis);

                float half = MathF.Max(light.SpotAngle, 1f) * 0.5f * (MathF.PI / 180f);
                half = MathF.Min(half, MathF.PI * 0.5f);
                float cosOuter = MathF.Cos(half);
                float cosInner = MathF.Cos(half * (1f - Math.Clamp(light.SpotSoftness, 0f, 1f)));
                att *= SmoothStep(cosOuter, cosInner, cos);
            }
        }

        return Math.Clamp(att, 0f, 1f);
    }

    /// <summary>HLSL の smoothstep 相当。edge0==edge1 のときは階段状に落とす。</summary>
    static float SmoothStep(float edge0, float edge1, float x)
    {
        if (MathF.Abs(edge1 - edge0) < 1e-6f)
            return x >= edge1 ? 1f : 0f;
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// 同一チャンネルの複数光源をまとめた「実効的な光」。消費エフェクトはこれだけを見る。
    /// ゆらぎと距離減衰は <see cref="Intensity"/> に織り込み済みなので、消費側で再度掛けないこと。
    /// </summary>
    public readonly struct ResolvedLight
    {
        /// <summary>立ち絵から光源へ向かうスクリーン方向（Y下, 正規化）。</summary>
        public Vector2 Dir { get; init; }

        /// <summary>寄与で重み付けした平均の光色。</summary>
        public Vector4 Color { get; init; }

        /// <summary>各光源の「強度 × ゆらぎ × 距離減衰」の合計。</summary>
        public float Intensity { get; init; }

        /// <summary>寄与で重み付けした平均の擬似高さ（Z）。</summary>
        public float Height { get; init; }

        /// <summary>
        /// 光がどれだけ届いているか（0..1）。強度の大小に依存しない「届き具合」で、
        /// 陰影の濃さや影の濃さのように<b>強度を掛けたくない</b>量へ使う。
        /// </summary>
        public float Reach { get; init; }

        /// <summary>合成に使った光源の数（1 なら従来と完全に同じ結果になる）。</summary>
        public int Count { get; init; }
    }

    /// <summary>
    /// 同一チャンネルに置かれた複数の光源を1つの実効的な光へ合成する。
    ///
    /// <para>
    /// 各光源の重みは <c>強度 × ゆらぎ × 距離減衰</c>。方向は重み付き平均、色と高さも重み付き平均、
    /// 強度は合計にする。街灯が並ぶ道を歩くと、<b>近い街灯の重みが自然に大きくなる</b>ので、
    /// 利用者がチャンネルを切り替えなくても受ける光が移り変わる。
    /// </para>
    ///
    /// <para>
    /// 【ゆらぎはタイムライン基準のフレームで計算すること】
    /// アイテム内フレーム（<c>ItemPosition</c>）を使うと、開始位置の違う立ち絵どうしで
    /// 同じ光源なのに明滅の位相がずれる。<paramref name="timelineFrame"/> には
    /// <c>TimelinePosition.Frame</c> を渡す。
    /// </para>
    ///
    /// <para>
    /// 【正反対の光は打ち消し合う】方向の重み付き平均がゼロ付近になった場合は、
    /// 最も寄与の大きい光源の向きへフォールバックする（向きが不定になるのを避ける）。
    /// </para>
    /// </summary>
    public static bool Combine(
        IReadOnlyList<LightState> lights, Vector2 itemWorld, long timelineFrame, int fps,
        out ResolvedLight result)
    {
        result = default;
        if (lights is null || lights.Count == 0)
            return false;

        Vector2 dirSum = default;
        Vector3 colorSum = default;
        float alphaSum = 0f, heightSum = 0f, totalWeight = 0f, nominalSum = 0f;

        // 全部の光が届かなかった場合に色・高さ・向きを借りる「最も寄与の大きい光源」
        var best = lights[0];
        var bestDir = new Vector2(0f, -1f);
        float bestWeight = -1f;

        foreach (var light in lights)
        {
            var dir = ScreenDir(light, itemWorld);
            float weight = MathF.Max(light.Intensity, 0f)
                         * Flicker(timelineFrame, fps, light.FlickerAmount, light.FlickerSpeed, light.FlickerSeed)
                         * Attenuation(light, itemWorld);

            if (weight > bestWeight)
            {
                bestWeight = weight;
                bestDir = dir;
                best = light;
            }

            nominalSum += MathF.Max(light.Intensity, 0f);
            dirSum += dir * weight;
            colorSum += new Vector3(light.Color.X, light.Color.Y, light.Color.Z) * weight;
            alphaSum += light.Color.W * weight;
            heightSum += light.Height * weight;
            totalWeight += weight;
        }

        float len = dirSum.Length();
        var dirOut = len > 1e-4f ? dirSum / len : bestDir;

        Vector4 colorOut;
        float heightOut;
        if (totalWeight > 1e-6f)
        {
            colorOut = new Vector4(colorSum / totalWeight, alphaSum / totalWeight);
            heightOut = heightSum / totalWeight;
        }
        else
        {
            // どの光も届いていない。向き・色は最も近い（＝寄与が最大だった）光源のものを使い、
            // 強度 0 で「当たっていない」ことを表す。消費側は強度で判断する。
            colorOut = best.Color;
            heightOut = best.Height;
        }

        result = new ResolvedLight
        {
            Dir = dirOut,
            Color = colorOut,
            Intensity = totalWeight,
            Reach = nominalSum > 1e-6f ? Math.Clamp(totalWeight / nominalSum, 0f, 1f) : 0f,
            Height = heightOut,
            Count = lights.Count,
        };
        return true;
    }

    /// <summary>スクリーン向きの方向ベクトルを角度（度）だけ回転する。正の角度で時計回り（Y下系）。</summary>
    public static Vector2 Rotate(Vector2 dir, float degrees)
    {
        float r = degrees * (MathF.PI / 180f);
        float c = MathF.Cos(r), s = MathF.Sin(r);
        return new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
    }

    /// <summary>角度（度, 0=上/(0,-1), 時計回り）からスクリーン向きの単位ベクトルを作る。</summary>
    public static Vector2 DirFromAngle(float degrees) => Rotate(new Vector2(0f, -1f), degrees);

    /// <summary>
    /// ゆらぎ係数（明滅の倍率）を frame/fps から決定的に計算する。
    /// 乱数を使わず sin の重ね合わせで擬似的な炎の揺らぎを作るため、
    /// プレビュー・エクスポート・一時停止再描画のいずれでも同一 frame は同一値になる。
    ///
    /// 時刻は frame/fps［秒］なので、プロジェクトのフレームレートに依存しない
    /// （30fps でも 60fps でも同じ秒数で同じ明るさになる）。
    /// speed は Hz（1秒あたりの周期数）。UI 側は「周期［秒］」で入力し、1/周期 で Hz に変換して渡す。
    ///
    /// 主周期を体感しやすいよう基本波を厚く（0.75）、うねりを与える第2波を薄く（0.25）配分する。
    /// amount=0 で常に 1.0（ゆらぎ無し）。戻り値は 0 以上。
    /// </summary>
    public static float Flicker(long frame, int fps, float amount, float speed, float seed)
    {
        if (amount <= 0f || speed <= 0f)
            return 1f;
        float t = fps > 0 ? (float)frame / fps : 0f;
        float ph = t * speed * (MathF.PI * 2f) + seed;
        float f = MathF.Sin(ph) * 0.75f + MathF.Sin(ph * 2.7f + seed) * 0.25f;
        return MathF.Max(0f, 1f + amount * f);
    }
}
