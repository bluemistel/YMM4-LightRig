using System.Collections.Immutable;
using System.Numerics;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Player.Video;
using LightRig.Shared;

namespace LightRig.Effects.LightTarget;

/// <summary>
/// シーン光源ターゲットのプロセッサ。自アイテムのワールド座標＋オフセットと、
/// 光色・強度・ゆらぎ係数を <see cref="LightState"/> にまとめて毎フレーム共有ストアへ発信する。
/// 映像は入力をそのままパススルーする（D2D エフェクトは生成しない）。
///
/// プレビュー上には光源位置を示すドラッグ可能な操作点を表示する（<see cref="DrawDescription.Controllers"/>）。
/// アイテム中心から光源へのガイド線も引き、どちらから光が来るかを可視化する。
/// </summary>
internal sealed class LightTargetProcessor(LightTargetEffect item) : IVideoEffectProcessor
{
    ID2D1Image? input;

    // 操作点はオフセットが変わった時だけ作り直す（毎フレームの再生成を避ける）
    // DrawDescription.Controllers は ImmutableList<VideoEffectController> 型
    ImmutableList<VideoEffectController> cachedControllers = ImmutableList<VideoEffectController>.Empty;
    (float X, float Y, LightSourceType Type, float Range, float Start, float Angle, float SpotAngle)? cachedShape;

    public ID2D1Image Output => input!;

    public void SetInput(ID2D1Image? input) => this.input = input;

    public void ClearInput() => input = null;

    public DrawDescription Update(EffectDescription desc)
    {
        var drawDesc = desc.DrawDescription;

        var frame = desc.ItemPosition.Frame;
        var length = desc.ItemDuration.Frame;
        var fps = desc.FPS;

        var offsetX = (float)item.OffsetX.GetValue(frame, length, fps);
        var offsetY = (float)item.OffsetY.GetValue(frame, length, fps);
        var height = (float)item.Height.GetValue(frame, length, fps);
        var intensity = (float)(item.Intensity.GetValue(frame, length, fps) / 100.0);
        var flickerAmount = (float)(item.FlickerAmount.GetValue(frame, length, fps) / 100.0);
        // UI は「周期［秒］」。内部表現（LightState.FlickerSpeed）は Hz なので 1/周期 に変換する。
        // 周期 0 はゆらぎ無しとして速度 0 を渡す（LightMath.Flicker が 1.0 を返す）。
        var flickerPeriod = (float)item.FlickerPeriod.GetValue(frame, length, fps);
        var flickerSpeed = flickerPeriod > 1e-3f ? 1f / flickerPeriod : 0f;
        var flickerSeed = (float)item.FlickerSeed;

        // 「距離で減衰」が無効なら到達距離 0（＝無限、減衰なし）として発信する。
        // 0 に「無限」の意味を持たせたまま UI へ出すと、0 の隣の 1px がほぼ全滅という崖になる。
        var range = item.FalloffMode == LightFalloffMode.Range
            ? (float)item.Range.GetValue(frame, length, fps)
            : 0f;
        var falloffStart = (float)(item.FalloffStart.GetValue(frame, length, fps) / 100.0);
        var angle = (float)item.Angle.GetValue(frame, length, fps);
        var spotAngle = (float)item.SpotAngle.GetValue(frame, length, fps);
        var spotSoftness = (float)(item.SpotSoftness.GetValue(frame, length, fps) / 100.0);

        var ambientColorMix = (float)(item.AmbientColorMix.GetValue(frame, length, fps) / 100.0);
        var ambientIntensityMix = (float)(item.AmbientIntensityMix.GetValue(frame, length, fps) / 100.0);
        var ambientReference = (float)(item.AmbientReference.GetValue(frame, length, fps) / 100.0);

        var c = item.Color;
        var lightColor = new Vector3(c.R / 255f, c.G / 255f, c.B / 255f);

        // --- 環境光サンプラーへの追従 ---
        // 同じチャンネルの環境光サンプラーが背景の代表色（明るい部分の平均）を発信していれば、
        // 光の色と強さをそれに寄せる。ここで一度寄せておけば、消費エフェクトは何も変えずに
        // シーンの明るさ・色温度へ揃う（チャンネル基盤の本来の使い方）。
        //
        // 色と明るさは分離して扱う。代表色をそのまま混ぜると暗い背景で光色まで暗くなり、
        // 「明るさの追従」と二重に効いてしまうため、色は最大成分で正規化して色味だけを取り出す。
        if ((ambientColorMix > 0f || ambientIntensityMix > 0f)
            && AmbientSignalStore.TryGet(desc.SceneId, desc.Usage, item.Channel,
                desc.TimelinePosition.Frame, out var ambient))
        {
            var amb = new Vector3(ambient.X, ambient.Y, ambient.Z);

            if (ambientColorMix > 0f)
                lightColor = Vector3.Lerp(lightColor, NormalizeTone(amb), Math.Clamp(ambientColorMix, 0f, 1f));

            if (ambientIntensityMix > 0f)
            {
                float lum = 0.299f * amb.X + 0.587f * amb.Y + 0.114f * amb.Z;
                // 基準の明るさで等倍。明るい背景ほど強く、暗い背景ほど弱く。
                // 極端な背景で光が暴走しないよう上限を設ける。
                float ratio = Math.Clamp(lum / MathF.Max(ambientReference, 1e-3f), 0f, 4f);
                intensity *= float.Lerp(1f, ratio, Math.Clamp(ambientIntensityMix, 0f, 1f));
            }
        }

        var state = new LightState
        {
            Type = item.SourceType,
            // アイテムのワールド座標（drawDesc.Draw）を基準に、オフセットを加えた点を光源位置とする
            // （平行光では使われない）
            Position = new Vector2(drawDesc.Draw.X + offsetX, drawDesc.Draw.Y + offsetY),
            Angle = angle,
            Range = range,
            FalloffStart = falloffStart,
            SpotAngle = spotAngle,
            SpotSoftness = spotSoftness,
            Height = height,
            Color = new Vector4(lightColor.X, lightColor.Y, lightColor.Z, c.A / 255f),
            Intensity = intensity,
            FlickerAmount = flickerAmount,
            FlickerSpeed = flickerSpeed,
            FlickerSeed = flickerSeed,
            // 環境光色は AmbientSignalStore で別途配られるので、消費側はそちらを直接読む（ここは未使用）
            AmbientColor = default,
        };

        // 【発信元キーはプロセッサ（this）ではなくエフェクトのアイテム（item）にすること】
        // YMM4 は Usage（Playing/Paused/Exporting）ごとに別のプロセッサを作るため、
        // this をキーにすると同じ光源が複数スロットを占め、合成時に「光源が2個ある」と
        // 誤認されて明るさが倍になる。item は Usage をまたいで同一インスタンスなので重複しない。
        LightSignalStore.Publish(desc.SceneId, desc.Usage, item.Channel, desc.TimelinePosition.Frame, item, state);

        // プレビュー上の操作点（位置はアイテム中心からのオフセット＝OffsetX/Y と同じ座標系）
        var shape = (offsetX, offsetY, item.SourceType, range, falloffStart, angle, spotAngle);
        if (cachedShape != shape)
        {
            cachedControllers = BuildControllers(offsetX, offsetY, item.SourceType, range, falloffStart, angle, spotAngle);
            cachedShape = shape;
        }

        return drawDesc with { Controllers = cachedControllers };
    }

    /// <summary>
    /// 色味だけを取り出す（最大成分を 1 に正規化する）。
    /// 明るさは「明るさの追従」が別に担当するので、色の追従では明度を持ち込まない。
    /// ほぼ黒（色味が定まらない）場合は白＝色を変えないものとして扱う。
    /// </summary>
    static Vector3 NormalizeTone(Vector3 c)
    {
        float m = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        return m > 1e-3f ? c / m : new Vector3(1f, 1f, 1f);
    }

    /// <summary>
    /// プレビュー上のガイドを組み立てる。光源位置のドラッグ点に加えて、
    /// 光源の種類に応じて「どこまで届くか」「どこを照らすか」を可視化する。
    /// 減衰を入れた以上、到達範囲が見えないと調整できないため。
    ///
    /// 表示専用の点は <see cref="VideoControllerPointShape.None"/> にしてドラッグ点と区別する
    /// （コールバックを渡さない点はそもそも動かせない）。
    /// </summary>
    ImmutableList<VideoEffectController> BuildControllers(
        float offsetX, float offsetY, LightSourceType type,
        float range, float falloffStart, float angle, float spotAngle)
    {
        var lightPos = new Vector3(offsetX, offsetY, 0f);
        var controllers = new List<VideoEffectController>();

        // 中心 → 光源のガイド線（表示のみ）
        controllers.Add(new VideoEffectController(item, [
            new ControllerPoint(new Vector3(0f, 0f, 0f)),
            new ControllerPoint(lightPos),
        ])
        {
            Connection = VideoControllerPointConnection.Line,
        });

        // 到達距離の円＋ドラッグハンドル（平行光は距離の概念が無いので出さない）
        if (type != LightSourceType.Directional && range > 1f)
        {
            controllers.Add(BuildRing(offsetX, offsetY, range));

            // 等倍で届く内側の円。「減衰の始まり」がどこかを目で見て決められるようにする。
            float inner = range * Math.Clamp(falloffStart, 0f, 0.99f);
            if (inner > 1f)
                controllers.Add(BuildRing(offsetX, offsetY, inner));

            var rangeHandle = new ControllerPoint(
                new Vector3(offsetX + range, offsetY, 0f),
                arg => item.Range.AddToEachValues(arg.Delta.X))
            {
                Shape = VideoControllerPointShape.Square,
            };
            controllers.Add(new VideoEffectController(item, [rangeHandle]));
        }

        // 光の向きを示す線。Angle は「光が来る向き」なので、進む向きは符号を反転させる。
        float rayLength = range > 1f ? range : 600f;
        if (type == LightSourceType.Spot)
        {
            var axis = -LightMath.DirFromAngle(angle);
            float half = Math.Clamp(spotAngle, 1f, 180f) * 0.5f;
            controllers.Add(BuildRay(lightPos, LightMath.Rotate(axis, -half) * rayLength));
            controllers.Add(BuildRay(lightPos, LightMath.Rotate(axis, half) * rayLength));
        }
        else if (type == LightSourceType.Directional)
        {
            controllers.Add(BuildRay(lightPos, -LightMath.DirFromAngle(angle) * 600f));
        }

        // 光源位置のドラッグ点は最後（手前）に置く
        var lightPoint = new ControllerPoint(
            lightPos,
            arg =>
            {
                item.OffsetX.AddToEachValues(arg.Delta.X);
                item.OffsetY.AddToEachValues(arg.Delta.Y);
            })
        {
            Shape = VideoControllerPointShape.Circle,
        };
        controllers.Add(new VideoEffectController(item, [lightPoint]));

        return [.. controllers];
    }

    /// <summary>到達距離を示す円。線分の集合として描くので、終点に始点を足して閉じる。</summary>
    VideoEffectController BuildRing(float centerX, float centerY, float radius)
    {
        const int Segments = 48;
        var points = new List<ControllerPoint>(Segments + 1);
        for (int i = 0; i <= Segments; i++)
        {
            float a = i / (float)Segments * MathF.Tau;
            points.Add(new ControllerPoint(
                new Vector3(centerX + MathF.Cos(a) * radius, centerY + MathF.Sin(a) * radius, 0f))
            {
                Shape = VideoControllerPointShape.None,
            });
        }
        return new VideoEffectController(item, [.. points])
        {
            Connection = VideoControllerPointConnection.Line,
        };
    }

    /// <summary>光源位置から delta だけ伸びる表示専用の線。</summary>
    VideoEffectController BuildRay(Vector3 from, Vector2 delta)
        => new(item, [
            new ControllerPoint(from) { Shape = VideoControllerPointShape.None },
            new ControllerPoint(new Vector3(from.X + delta.X, from.Y + delta.Y, 0f))
            {
                Shape = VideoControllerPointShape.None,
            },
        ])
        {
            Connection = VideoControllerPointConnection.Line,
        };

    public void Dispose()
    {
    }
}
