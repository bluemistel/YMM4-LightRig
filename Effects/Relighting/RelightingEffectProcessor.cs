using System.Numerics;
using System.Windows.Media;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;
using LightRig.Shared;

namespace LightRig.Effects.Relighting;

/// <summary>
/// シーン連動リライティング。単一カスタムシェーダーでライティングし直した画像を出力する。
/// 光源方向・光色・強度（ゆらぎ込み）は <see cref="LightSignalStore"/> から取得し、無ければ手動角度・プリセット色で動作する。
/// プリセットは光色・影色・拡散・ハイライト・回り込みへ C# 側で解決してシェーダーへ渡す。
/// </summary>
internal sealed class RelightingEffectProcessor : VideoEffectProcessorBase
{
    private readonly RelightingEffect _item;
    private RelightingCustomEffect? _re;

    // 解決済みルック（プリセット or 手動）
    private readonly record struct Look(
        Vector3 LightColor, Vector3 ShadowColor,
        float Diffuse, float Highlight, float Shininess, float Wrap);

    private bool _isFirst = true;
    private ConstantValues _last;

    private readonly record struct ConstantValues(
        float DirX, float DirY, float LightZ, float FormScale, float Wrap,
        float Diffuse, float Highlight, float Shininess,
        float LR, float LG, float LB, float SR, float SG, float SB, float Intensity,
        float Blur);

    public RelightingEffectProcessor(IGraphicsDevicesAndContext devices, RelightingEffect item)
        : base(devices)
    {
        _item = item;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        _re = new RelightingCustomEffect(devices);
        if (!_re.IsEnabled)
        {
            _re.Dispose();
            _re = null;
            return null;
        }
        disposer.Collect(_re);

        var output = _re.Output;
        disposer.Collect(output);
        return output;
    }

    protected override void setInput(ID2D1Image? input)
        => _re?.SetInput(0, input, true);

    protected override void ClearEffectChain()
        => _re?.SetInput(0, null, true);

    public override DrawDescription Update(EffectDescription effectDescription)
    {
        if (IsPassThroughEffect || _re is null)
            return effectDescription.DrawDescription;

        var drawDesc = effectDescription.DrawDescription;
        var frame = effectDescription.ItemPosition.Frame;
        var length = effectDescription.ItemDuration.Frame;
        var fps = effectDescription.FPS;

        var angleOffset = (float)_item.AngleOffset.GetValue(frame, length, fps);
        var overall = (float)(_item.Intensity.GetValue(frame, length, fps) / 100.0);
        var formScale = (float)_item.FormScale.GetValue(frame, length, fps);
        var blur = (float)_item.Blur.GetValue(frame, length, fps);
        var colorMix = (float)(_item.ColorMix.GetValue(frame, length, fps) / 100.0);
        var ambientMix = (float)(_item.AmbientMix.GetValue(frame, length, fps) / 100.0);

        var look = ResolveLook(frame, length, fps);
        var shadowColor = look.ShadowColor;

        // 光源の解決
        var itemPos = new Vector2(drawDesc.Draw.X, drawDesc.Draw.Y);
        Vector2 dir;
        float lightZ = 1f;
        Vector3 sceneColor = look.LightColor;
        float sceneMul = 1f;

        if (_item.Channel != LightChannelOrOff.Off
            && LightSignalStore.TryGet(effectDescription.SceneId, effectDescription.Usage, (LightChannel)_item.Channel,
                effectDescription.TimelinePosition.Frame, out var light))
        {
            dir = LightMath.Rotate(LightMath.ScreenDir(light, itemPos), angleOffset);
            lightZ = Math.Clamp(light.Height / 400f, 0.05f, 4f);
            sceneColor = new Vector3(light.Color.X, light.Color.Y, light.Color.Z);
            sceneMul = light.Intensity
                * LightMath.Flicker(frame, fps, light.FlickerAmount, light.FlickerSpeed, light.FlickerSeed)
                * LightMath.Attenuation(light, itemPos);

            // 環境光サンプラーの背景色を影色へ混ぜて背景と馴染ませる（連動時のみ）
            if (ambientMix > 0f
                && AmbientSignalStore.TryGet(effectDescription.SceneId, effectDescription.Usage, (LightChannel)_item.Channel,
                    effectDescription.TimelinePosition.Frame, out var ambient))
            {
                shadowColor = Vector3.Lerp(shadowColor, new Vector3(ambient.X, ambient.Y, ambient.Z), ambientMix);
            }
        }
        else
        {
            dir = LightMath.DirFromAngle(angleOffset);
            colorMix = 0f; // 連動していないのでシーン光源色は使わない
        }

        // プリセット/手動の光色 ↔ シーン光源色 をミックスし、光源強度（ゆらぎ込み）を掛ける
        var effLight = Vector3.Lerp(look.LightColor, sceneColor, colorMix) * sceneMul;

        var cur = new ConstantValues(
            dir.X, dir.Y, lightZ, formScale, look.Wrap,
            look.Diffuse, look.Highlight, look.Shininess,
            effLight.X, effLight.Y, effLight.Z,
            shadowColor.X, shadowColor.Y, shadowColor.Z,
            overall, blur);

        if (_isFirst || cur != _last)
        {
            _re.LightDirX = cur.DirX;
            _re.LightDirY = cur.DirY;
            _re.LightZ = cur.LightZ;
            _re.FormScale = cur.FormScale;
            _re.Wrap = cur.Wrap;
            _re.Diffuse = cur.Diffuse;
            _re.Highlight = cur.Highlight;
            _re.Shininess = cur.Shininess;
            _re.LightR = cur.LR;
            _re.LightG = cur.LG;
            _re.LightB = cur.LB;
            _re.ShadowR = cur.SR;
            _re.ShadowG = cur.SG;
            _re.ShadowB = cur.SB;
            _re.Intensity = cur.Intensity;
            _re.Blur = cur.Blur;
            _last = cur;
            _isFirst = false;
        }

        return drawDesc;
    }

    /// <summary>プリセット（or 手動）を実効ルックへ解決する。</summary>
    private Look ResolveLook(long frame, long length, int fps)
    {
        // 手動パラメータ
        static Vector3 ToVec(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

        switch (_item.Preset)
        {
            case RelightingPreset.Sunset:
                return new Look(new(1.0f, 0.55f, 0.25f), new(0.28f, 0.30f, 0.45f), 1.0f, 0.35f, 8f, 0.40f);
            case RelightingPreset.Indoor:
                return new Look(new(1.0f, 0.88f, 0.72f), new(0.35f, 0.34f, 0.38f), 0.85f, 0.25f, 12f, 0.50f);
            case RelightingPreset.Moonlight:
                return new Look(new(0.60f, 0.72f, 1.0f), new(0.12f, 0.16f, 0.30f), 0.90f, 0.50f, 16f, 0.30f);
            case RelightingPreset.Fire:
                return new Look(new(1.0f, 0.50f, 0.20f), new(0.22f, 0.13f, 0.10f), 1.10f, 0.40f, 8f, 0.35f);
            case RelightingPreset.Daylight:
                return new Look(new(1.0f, 0.97f, 0.90f), new(0.45f, 0.50f, 0.62f), 0.95f, 0.30f, 12f, 0.45f);
            case RelightingPreset.Overcast:
                return new Look(new(0.82f, 0.85f, 0.90f), new(0.48f, 0.50f, 0.55f), 0.70f, 0.10f, 4f, 0.70f);
            case RelightingPreset.Fluorescent:
                return new Look(new(0.90f, 0.98f, 0.95f), new(0.38f, 0.40f, 0.42f), 0.90f, 0.35f, 20f, 0.55f);
            default: // Manual
                return new Look(
                    ToVec(_item.LightColor),
                    ToVec(_item.ShadowColor),
                    (float)(_item.Diffuse.GetValue(frame, length, fps) / 100.0),
                    (float)(_item.Highlight.GetValue(frame, length, fps) / 100.0),
                    (float)_item.Shininess.GetValue(frame, length, fps),
                    (float)(_item.Wrap.GetValue(frame, length, fps) / 100.0));
        }
    }
}
