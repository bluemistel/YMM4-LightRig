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
