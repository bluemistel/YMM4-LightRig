using System.Numerics;
using Vortice.Direct2D1;
using D2DEffects = Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;
using LightRig.Shared;

namespace LightRig.Effects.BlendLight;

/// <summary>
/// 背景なじませ。グラフ構成はリムライトと同型（光を"足す"ものは別レイヤー化して D2D の Blur を通す）:
///   Input ─┬──────────────────────────────[Composite/Blend in0]──[CrossFade in0]
///          └[BlendLight]───[GaussianBlur]─[Composite/Blend in1]        │
///   Input ──────────────────────────────────────────────[CrossFade in1] ─ Output
///
/// 光源方向は <see cref="LightSignalStore"/>、背景色は <see cref="AmbientSignalStore"/> から取得する。
/// 背景グリッドを使う場合は、被写体の Draw 位置・Zoom と背景のシーン矩形から
/// 「被写体ローカル座標 → 背景グリッド UV」のアフィン変換を CPU 側で組んでシェーダーへ渡す。
/// </summary>
internal sealed class BlendLightEffectProcessor : VideoEffectProcessorBase
{
    private readonly BlendLightEffect _item;
    private BlendLightCustomEffect? _light;
    private D2DEffects.GaussianBlur? _blur;
    private D2DEffects.Composite? _composite;
    private D2DEffects.Blend? _blend;
    private D2DEffects.CrossFade? _crossFade;

    private bool _isFirst = true;
    private float _lastDirX, _lastDirY, _lastSpread, _lastMode;
    private float _lastSaturation, _lastGain, _lastBlur, _lastWeight;
    private float _lastUvOriginX, _lastUvOriginY, _lastUvScaleX, _lastUvScaleY;
    private float _lastUseGrid = -1f;
    private float _lastMethod = -1f, _lastTone = -1f, _lastLumaMatch = -1f;
    private float _lastFallbackR = -1f, _lastFallbackG = -1f, _lastFallbackB = -1f;
    private readonly Vector3[] _lastCells = new Vector3[AmbientState.GridSize * AmbientState.GridSize];
    private bool _hasCells;
    private YukkuriMovieMaker.Project.Blend _lastBlendMode;

    public BlendLightEffectProcessor(IGraphicsDevicesAndContext devices, BlendLightEffect item)
        : base(devices)
    {
        _item = item;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        var dc = devices.DeviceContext;

        _light = new BlendLightCustomEffect(devices);
        if (!_light.IsEnabled)
        {
            _light.Dispose();
            _light = null;
            return null;
        }
        disposer.Collect(_light);

        _blur = new D2DEffects.GaussianBlur(dc);
        disposer.Collect(_blur);
        using (var lightOut = _light.Output)
            _blur.SetInput(0, lightOut, true);

        _composite = new D2DEffects.Composite(dc) { InputCount = 2 };
        disposer.Collect(_composite);
        using (var blurOut = _blur.Output)
            _composite.SetInput(1, blurOut, true);

        _blend = new D2DEffects.Blend(dc);
        disposer.Collect(_blend);
        using (var blurOut = _blur.Output)
            _blend.SetInput(1, blurOut, true);

        _crossFade = new D2DEffects.CrossFade(dc);
        disposer.Collect(_crossFade);

        var output = _crossFade.Output;
        disposer.Collect(output);
        return output;
    }

    protected override void setInput(ID2D1Image? input)
    {
        _light?.SetInput(0, input, true);
        _composite?.SetInput(0, input, true);
        _blend?.SetInput(0, input, true);
        _crossFade?.SetInput(1, input, true);
    }

    protected override void ClearEffectChain()
    {
        _light?.SetInput(0, null, true);
        _composite?.SetInput(0, null, true);
        _composite?.SetInput(1, null, true);
        _blend?.SetInput(0, null, true);
        _blend?.SetInput(1, null, true);
        _crossFade?.SetInput(0, null, true);
        _crossFade?.SetInput(1, null, true);
    }

    public override DrawDescription Update(EffectDescription effectDescription)
    {
        if (IsPassThroughEffect || _light is null || _blur is null
            || _composite is null || _blend is null || _crossFade is null)
            return effectDescription.DrawDescription;

        var drawDesc = effectDescription.DrawDescription;
        var frame = effectDescription.ItemPosition.Frame;
        var length = effectDescription.ItemDuration.Frame;
        var fps = effectDescription.FPS;

        var angleOffset = (float)_item.AngleOffset.GetValue(frame, length, fps);
        var localIntensity = (float)(_item.Intensity.GetValue(frame, length, fps) / 100.0);
        var spread = (float)(_item.Spread.GetValue(frame, length, fps) / 100.0);
        var blur = (float)_item.Blur.GetValue(frame, length, fps);
        var saturation = (float)(_item.Saturation.GetValue(frame, length, fps) / 100.0);
        var gain = (float)(_item.Gain.GetValue(frame, length, fps) / 100.0);
        var rangeScale = (float)(_item.RangeScale.GetValue(frame, length, fps) / 100.0);
        var offsetX = (float)_item.OffsetX.GetValue(frame, length, fps);
        var offsetY = (float)_item.OffsetY.GetValue(frame, length, fps);
        var mode = (float)(int)_item.Mode; // 0=グラデーション, 2=全体
        var colorTune = (float)(_item.ColorTune.GetValue(frame, length, fps) / 100.0);
        var method = (float)(int)_item.Method; // 0=光を重ねる, 1=色調同化
        var tone = (float)(_item.ToneStrength.GetValue(frame, length, fps) / 100.0);
        var lumaMatch = (float)(_item.LumaMatch.GetValue(frame, length, fps) / 100.0);
        var local = _item.LocalColor;

        var itemPos = new Vector2(drawDesc.Draw.X, drawDesc.Draw.Y);

        // --- 光源方向の解決（連動 or 単体） ---
        // モード＝全体は「光源を置かずに背景へ馴染ませる」ためのモードなので、
        // 向きも強さも減衰も一切参照しない（光源があってもなじませ量が変わらない）。
        Vector2 dir;
        float lightIntensity = 1f;
        if (_item.Mode == BlendLightMode.Uniform)
        {
            dir = LightMath.DirFromAngle(0f);
        }
        else if (_item.Channel != LightChannelOrOff.Off
            && LightSignalStore.TryResolve(effectDescription.SceneId, effectDescription.Usage, (LightChannel)_item.Channel,
                effectDescription.TimelinePosition.Frame, fps, itemPos, out var light))
        {
            dir = LightMath.Rotate(light.Dir, angleOffset);
            lightIntensity = light.Intensity;
        }
        else
        {
            // 連動無し: 角度オフセットを絶対角として扱う
            dir = LightMath.DirFromAngle(angleOffset);
        }

        // --- 背景色の解決（グリッド or 代表色 or 固定色） ---
        float useGrid = 0f;
        float fallbackR = local.R / 255f, fallbackG = local.G / 255f, fallbackB = local.B / 255f;
        float uvOriginX = 0f, uvOriginY = 0f, uvScaleX = 0f, uvScaleY = 0f;

        if (_item.Channel != LightChannelOrOff.Off
            && AmbientSignalStore.TryGetState(effectDescription.SceneId, effectDescription.Usage, (LightChannel)_item.Channel,
                effectDescription.TimelinePosition.Frame, itemPos, out var ambient))
        {
            // 拾った背景色を「光源らしい色」へ寄せる（0%なら素通し）
            var tunedFallback = ColorGrading.TuneLightColor(
                new Vector3(ambient.Color.X, ambient.Color.Y, ambient.Color.Z), colorTune);
            fallbackR = tunedFallback.X;
            fallbackG = tunedFallback.Y;
            fallbackB = tunedFallback.Z;

            if (_item.ColorSource == BlendLightColorSource.Grid && ambient.HasGrid)
            {
                // 被写体ローカル px → 背景グリッド UV のアフィン変換。
                //   uv = (被写体中心の背景UV) + (中心からのローカルオフセット) × (Zoom / 背景サイズ)
                // 位置オフセットはシーン座標での参照位置のずらし、範囲倍率は参照する広さの調整。
                var zoom = drawDesc.Zoom * MathF.Max(rangeScale, 1e-3f);
                uvScaleX = zoom.X / ambient.RectSize.X;
                uvScaleY = zoom.Y / ambient.RectSize.Y;
                var sample = itemPos + new Vector2(offsetX, offsetY) - ambient.RectMin;
                uvOriginX = sample.X / ambient.RectSize.X;
                uvOriginY = sample.Y / ambient.RectSize.Y;
                useGrid = 1f;

                var grid = ambient.Grid!;
                for (int i = 0; i < grid.Length; i++)
                {
                    var cell = ColorGrading.TuneLightColor(grid[i], colorTune);
                    if (!_hasCells || _lastCells[i] != cell)
                    {
                        _light.SetCell(i, cell);
                        _lastCells[i] = cell;
                    }
                }
                _hasCells = true;
            }
        }

        float weight = Math.Clamp(localIntensity * lightIntensity, 0f, 1f);

        if (_isFirst || dir.X != _lastDirX) { _light.LightDirX = dir.X; _lastDirX = dir.X; }
        if (_isFirst || dir.Y != _lastDirY) { _light.LightDirY = dir.Y; _lastDirY = dir.Y; }
        if (_isFirst || spread != _lastSpread) { _light.Spread = spread; _lastSpread = spread; }
        if (_isFirst || mode != _lastMode) { _light.Mode = mode; _lastMode = mode; }
        if (_isFirst || saturation != _lastSaturation) { _light.Saturation = saturation; _lastSaturation = saturation; }
        if (_isFirst || gain != _lastGain) { _light.Gain = gain; _lastGain = gain; }
        if (_isFirst || useGrid != _lastUseGrid) { _light.UseGrid = useGrid; _lastUseGrid = useGrid; }
        if (_isFirst || uvOriginX != _lastUvOriginX) { _light.UvOriginX = uvOriginX; _lastUvOriginX = uvOriginX; }
        if (_isFirst || uvOriginY != _lastUvOriginY) { _light.UvOriginY = uvOriginY; _lastUvOriginY = uvOriginY; }
        if (_isFirst || uvScaleX != _lastUvScaleX) { _light.UvScaleX = uvScaleX; _lastUvScaleX = uvScaleX; }
        if (_isFirst || uvScaleY != _lastUvScaleY) { _light.UvScaleY = uvScaleY; _lastUvScaleY = uvScaleY; }
        if (_isFirst || fallbackR != _lastFallbackR) { _light.FallbackR = fallbackR; _lastFallbackR = fallbackR; }
        if (_isFirst || fallbackG != _lastFallbackG) { _light.FallbackG = fallbackG; _lastFallbackG = fallbackG; }
        if (_isFirst || fallbackB != _lastFallbackB) { _light.FallbackB = fallbackB; _lastFallbackB = fallbackB; }
        if (_isFirst || tone != _lastTone) { _light.ToneStrength = tone; _lastTone = tone; }
        if (_isFirst || lumaMatch != _lastLumaMatch) { _light.LumaMatch = lumaMatch; _lastLumaMatch = lumaMatch; }
        if (_isFirst || blur != _lastBlur) { _blur.StandardDeviation = blur; _lastBlur = blur; }

        // 色調同化はシェーダーが最終色を出すので、ぼかし・合成モードの段を通さず直結する
        var blendMode = _item.BlendMode;
        if (_isFirst || method != _lastMethod || blendMode != _lastBlendMode)
        {
            _light.Method = method;

            if (method >= 0.5f)
            {
                using var toned = _light.Output;
                _crossFade.SetInput(0, toned, true);
            }
            else if (blendMode.IsCompositionEffect())
            {
                _composite.Mode = blendMode.ToD2DCompositionMode();
                using var composited = _composite.Output;
                _crossFade.SetInput(0, composited, true);
            }
            else
            {
                _blend.Mode = blendMode.ToD2DBlendMode();
                using var blended = _blend.Output;
                _crossFade.SetInput(0, blended, true);
            }
            _lastBlendMode = blendMode;
            _lastMethod = method;
        }
        if (_isFirst || weight != _lastWeight) { _crossFade.Weight = weight; _lastWeight = weight; }

        _isFirst = false;
        return drawDesc;
    }
}
