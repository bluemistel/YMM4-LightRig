using System.Numerics;
using System.Windows.Media;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;
using LightRig.Shared;

namespace LightRig.Effects.SceneShading;

/// <summary>
/// シーン連動シェーディング。単一カスタムシェーダーで完結する（陰影を焼き込んだ画像をそのまま出力）。
/// 光源方向・高さは <see cref="LightSignalStore"/> から取得し、無ければ手動角度で動作する。
/// </summary>
internal sealed class SceneShadingEffectProcessor : VideoEffectProcessorBase
{
    private readonly SceneShadingEffect _item;
    private SceneShadingCustomEffect? _shade;

    private bool _isFirst = true;
    private float _lastDirX, _lastDirY, _lastZ, _lastWidth, _lastStrength, _lastMode, _lastWrap, _lastSoftness, _lastBlur, _lastFormScale;
    private float _lastR = -1, _lastG = -1, _lastB = -1;

    public SceneShadingEffectProcessor(IGraphicsDevicesAndContext devices, SceneShadingEffect item)
        : base(devices)
    {
        _item = item;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        _shade = new SceneShadingCustomEffect(devices);
        if (!_shade.IsEnabled)
        {
            _shade.Dispose();
            _shade = null;
            return null;
        }
        disposer.Collect(_shade);

        var output = _shade.Output;
        disposer.Collect(output);
        return output;
    }

    protected override void setInput(ID2D1Image? input)
        => _shade?.SetInput(0, input, true);

    protected override void ClearEffectChain()
        => _shade?.SetInput(0, null, true);

    public override DrawDescription Update(EffectDescription effectDescription)
    {
        if (IsPassThroughEffect || _shade is null)
            return effectDescription.DrawDescription;

        var drawDesc = effectDescription.DrawDescription;
        var frame = effectDescription.ItemPosition.Frame;
        var length = effectDescription.ItemDuration.Frame;
        var fps = effectDescription.FPS;

        var angleOffset = (float)_item.AngleOffset.GetValue(frame, length, fps);
        var strength = (float)(_item.Strength.GetValue(frame, length, fps) / 100.0);
        var width = (float)_item.Width.GetValue(frame, length, fps);
        var wrap = (float)(_item.Wrap.GetValue(frame, length, fps) / 100.0);
        var softness = (float)(_item.Softness.GetValue(frame, length, fps) / 100.0);
        var blur = (float)_item.Blur.GetValue(frame, length, fps);
        var formScale = (float)_item.FormScale.GetValue(frame, length, fps);
        var shadeCol = _item.ShadeColor;
        var colorMix = (float)(_item.ColorMix.GetValue(frame, length, fps) / 100.0);
        var colorTune = (float)(_item.ColorTune.GetValue(frame, length, fps) / 100.0);
        var mode = (float)(int)_item.Mode;

        var itemPos = new Vector2(drawDesc.Draw.X, drawDesc.Draw.Y);
        Vector2 dir;
        float lightZ = 1f;

        if (_item.Channel != LightChannelOrOff.Off
            && LightSignalStore.TryResolve(effectDescription.SceneId, effectDescription.Usage, (LightChannel)_item.Channel,
                effectDescription.TimelinePosition.Frame, fps, itemPos, out var light))
        {
            dir = LightMath.Rotate(light.Dir, angleOffset);
            lightZ = Math.Clamp(light.Height / 400f, 0.05f, 4f);
            // 光が届かない位置なら陰影も付かない（届かない＝そもそも光が当たっていない）
            strength *= light.Reach;
        }
        else
        {
            dir = LightMath.DirFromAngle(angleOffset);
        }

        var localShade = new Vector3(shadeCol.R / 255f, shadeCol.G / 255f, shadeCol.B / 255f);
        var shade = localShade;

        // --- 影色を背景から取る場合 ---
        // 影色は「乗算する色」なので、背景色をそのまま入れてはいけない。
        // 明るい背景では白に近い色＝影が消え、暗い背景では黒に近い色＝潰れる。
        // 【色味だけを背景から取り、暗さ（明るさ）は固定色側を維持する】
        // ＝ M9 の「色と明るさは必ず分離する」と同じ方針。
        // 光源が無くても（単体動作でも）環境光サンプラーさえあれば効く。
        if (_item.ColorSource == ShadeColorSource.Ambient
            && _item.Channel != LightChannelOrOff.Off
            && AmbientSignalStore.TryGet(effectDescription.SceneId, effectDescription.Usage,
                (LightChannel)_item.Channel, effectDescription.TimelinePosition.Frame, itemPos, out var ambient))
        {
            // 彩度が強すぎる背景で影が極端な色にならないよう整形してから色味を取り出す
            var tuned = ColorGrading.TuneLightColor(new Vector3(ambient.X, ambient.Y, ambient.Z), colorTune);
            // 固定色の「最大成分」＝影の暗さ。色味だけ差し替える
            float level = MathF.Max(localShade.X, MathF.Max(localShade.Y, localShade.Z));
            var ambientShade = ColorGrading.NormalizeTone(tuned) * level;
            shade = Vector3.Lerp(localShade, ambientShade, Math.Clamp(colorMix, 0f, 1f));
        }

        float r = shade.X, g = shade.Y, b = shade.Z;

        if (_isFirst || dir.X != _lastDirX) { _shade.LightDirX = dir.X; _lastDirX = dir.X; }
        if (_isFirst || dir.Y != _lastDirY) { _shade.LightDirY = dir.Y; _lastDirY = dir.Y; }
        if (_isFirst || lightZ != _lastZ) { _shade.LightZ = lightZ; _lastZ = lightZ; }
        if (_isFirst || width != _lastWidth) { _shade.Width = width; _lastWidth = width; }
        if (_isFirst || strength != _lastStrength) { _shade.Strength = strength; _lastStrength = strength; }
        if (_isFirst || r != _lastR) { _shade.ShadeR = r; _lastR = r; }
        if (_isFirst || g != _lastG) { _shade.ShadeG = g; _lastG = g; }
        if (_isFirst || b != _lastB) { _shade.ShadeB = b; _lastB = b; }
        if (_isFirst || mode != _lastMode) { _shade.Mode = mode; _lastMode = mode; }
        if (_isFirst || wrap != _lastWrap) { _shade.Wrap = wrap; _lastWrap = wrap; }
        if (_isFirst || softness != _lastSoftness) { _shade.Softness = softness; _lastSoftness = softness; }
        if (_isFirst || blur != _lastBlur) { _shade.Blur = blur; _lastBlur = blur; }
        if (_isFirst || formScale != _lastFormScale) { _shade.FormScale = formScale; _lastFormScale = formScale; }

        _isFirst = false;
        return drawDesc;
    }
}
