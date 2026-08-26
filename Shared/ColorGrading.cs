using System.Numerics;

namespace LightRig.Shared;

/// <summary>
/// 「背景から拾った色」を光源らしい色へ整形するヘルパー。
///
/// <para>
/// 【なぜ必要か（2026-08・YMM4-AutoBlendLight との比較で判明）】
/// 背景の色をそのまま光として使うと、暗い背景では暗い色を乗せることになり、
/// 「馴染ませ」ではなく<b>色を薄くしただけ</b>の見た目になる。
/// 実際の光は「明るく、彩度がほどほど」なので、拾った色を
/// <b>彩度に上限を、明度に下限を設けた範囲へ寄せる</b>と一気に光らしくなる。
/// </para>
///
/// <para>
/// 色が定まらない背景（彩度が低い・暗すぎる）は<b>信頼度</b>を下げて白へ寄せる。
/// 白＝無彩なので「色を変えない」という安全側へ倒れる。
/// </para>
///
/// 参考: <see href="https://github.com/routersys/YMM4-AutoBlendLight"/> の TuneLightColor。
/// </summary>
public static class ColorGrading
{
    /// <summary>Rec.709 の輝度。</summary>
    public static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    /// <summary>
    /// 拾った色を光源らしい色へ寄せる。
    /// <paramref name="amount"/> は 0=無補正, 1=標準の補正, 2=ほぼ無彩の白へ。
    /// </summary>
    public static Vector3 TuneLightColor(Vector3 raw, float amount)
    {
        if (amount <= 1e-4f)
            return raw;

        var hsl = RgbToHsl(raw);
        float lum = Luminance(raw);
        float max = MathF.Max(raw.X, MathF.Max(raw.Y, raw.Z));
        float min = MathF.Min(raw.X, MathF.Min(raw.Y, raw.Z));
        float chroma = max - min;

        // 彩度が低い／暗すぎる色は「何色か」が定まらない。信頼度として扱い白へ寄せる。
        float confidence = Math.Clamp((chroma - 0.025f) / 0.16f, 0f, 1f);
        if (lum < 0.10f)
            confidence *= Math.Clamp((max - 8f / 255f) / (36f / 255f), 0f, 1f);

        // 彩度は上限つきで抑え、明度は「光らしい」帯へ持ち上げる。
        float tunedSat = Math.Clamp(hsl.Y * 0.58f * confidence, 0f, 0.46f);
        float tunedLit = hsl.Z + (1f - hsl.Z) * 0.16f;
        tunedLit = lum < 0.06f ? 0.70f : Math.Clamp(tunedLit, 0.52f, 0.84f);

        var tuned = HslToRgb(new Vector3(hsl.X, tunedSat, tunedLit));

        // 暗い色・信頼度の低い色ほど白を多めに混ぜる
        float whiteMix = 0.06f + (1f - lum) * 0.05f + (1f - confidence) * 0.08f;
        tuned = Vector3.Lerp(tuned, Vector3.One, whiteMix);

        float a = Math.Clamp(amount, 0f, 2f);
        if (a <= 1f)
            return Vector3.Lerp(raw, tuned, a);

        // 1 を超えたぶんはさらに無彩・高明度へ倒す（色被りを消したいとき）
        var tunedHsl = RgbToHsl(tuned);
        var neutral = HslToRgb(new Vector3(
            tunedHsl.X, 0.05f, Math.Clamp(tunedHsl.Z + (1f - tunedHsl.Z) * 0.55f, 0f, 0.94f)));
        return Vector3.Lerp(tuned, neutral, a - 1f);
    }

    /// <summary>最大成分を 1 に正規化して「色味だけ」を取り出す。ほぼ黒なら白（＝色を変えない）。</summary>
    public static Vector3 NormalizeTone(Vector3 c)
    {
        float m = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        return m > 1e-3f ? c / m : Vector3.One;
    }

    public static Vector3 RgbToHsl(Vector3 c)
    {
        float max = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        float min = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float l = (max + min) * 0.5f;
        float delta = max - min;
        if (delta <= 1e-6f)
            return new Vector3(0f, 0f, l);

        float s = l > 0.5f
            ? delta / MathF.Max(2f - max - min, 1e-6f)
            : delta / MathF.Max(max + min, 1e-6f);

        float h;
        if (max == c.X) h = (c.Y - c.Z) / delta + (c.Y < c.Z ? 6f : 0f);
        else if (max == c.Y) h = (c.Z - c.X) / delta + 2f;
        else h = (c.X - c.Y) / delta + 4f;

        return new Vector3(h / 6f, s, l);
    }

    public static Vector3 HslToRgb(Vector3 hsl)
    {
        if (hsl.Y <= 0f)
        {
            float g = Math.Clamp(hsl.Z, 0f, 1f);
            return new Vector3(g, g, g);
        }

        float q = hsl.Z < 0.5f ? hsl.Z * (1f + hsl.Y) : hsl.Z + hsl.Y - hsl.Z * hsl.Y;
        float p = 2f * hsl.Z - q;
        return new Vector3(
            Math.Clamp(HueToRgb(p, q, hsl.X + 1f / 3f), 0f, 1f),
            Math.Clamp(HueToRgb(p, q, hsl.X), 0f, 1f),
            Math.Clamp(HueToRgb(p, q, hsl.X - 1f / 3f), 0f, 1f));
    }

    static float HueToRgb(float p, float q, float t)
    {
        t -= MathF.Floor(t);
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 0.5f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }
}
