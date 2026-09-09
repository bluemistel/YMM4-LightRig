using System.Numerics;
using Vortice.Direct2D1;
using D2DEffects = Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;
using LightRig.Shared;

namespace LightRig.Effects.LightReceive;

/// <summary>
/// 受光。光を"足す"側なので、リムライトと同じく別レイヤー化して合成する:
///   Input ─┬─────────────────────────────[Composite/Blend in0]──[CrossFade in0]
///          └[LightReceive]─[GaussianBlur]─[Composite/Blend in1]        │
///   Input ────────────────────────────────────────────[CrossFade in1] ─ Output
///
/// 光源方向・色・強度は <see cref="LightSignalStore.TryResolve"/> から取得する
/// （同一チャンネルの複数光源は合成済み。ゆらぎと距離減衰も織り込み済み）。
/// </summary>
internal sealed class LightReceiveEffectProcessor : VideoEffectProcessorBase
{
    private readonly LightReceiveEffect _item;
    private LightReceiveCustomEffect? _light;
    private D2DEffects.GaussianBlur? _blur;
    private D2DEffects.Composite? _composite;
    private D2DEffects.Blend? _blend;
    private D2DEffects.CrossFade? _crossFade;

    private bool _isFirst = true;
    private float _lastDirX, _lastDirY, _lastZ, _lastForm, _lastWrap, _lastDiffuse;
    private float _lastHighlight, _lastShininess, _lastBlur, _lastWeight;
    private float _lastR = -1f, _lastG = -1f, _lastB = -1f;
    private YukkuriMovieMaker.Project.Blend _lastBlendMode;

    public LightReceiveEffectProcessor(IGraphicsDevicesAndContext devices, LightReceiveEffect item)
        : base(devices)
    {
        _item = item;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        var dc = devices.DeviceContext;

        _light = new LightReceiveCustomEffect(devices);
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
        var lightGain = (float)(_item.LightGain.GetValue(frame, length, fps) / 100.0);
        var formScale = (float)_item.FormScale.GetValue(frame, length, fps);
        var diffuse = (float)(_item.Diffuse.GetValue(frame, length, fps) / 100.0);
        var wrap = (float)(_item.Wrap.GetValue(frame, length, fps) / 100.0);
        var blur = (float)_item.Blur.GetValue(frame, length, fps);
        var highlight = (float)(_item.Highlight.GetValue(frame, length, fps) / 100.0);
        var shininess = (float)_item.Shininess.GetValue(frame, length, fps);
        var colorMix = (float)(_item.ColorMix.GetValue(frame, length, fps) / 100.0);
        var local = _item.LocalColor;

        // 場面切り替えの前後どちら側の描画か。同じ側の発信を優先して結び付ける
        // （前の場面にいる立ち絵が次の場面の光や環境光を拾わないようにする）。
        var isHeldRender = RenderSide.IsHeld(effectDescription);
        var itemPos = new Vector2(drawDesc.Draw.X, drawDesc.Draw.Y);

        // --- 光源の解決（連動 or 単体） ---
        Vector2 dir;
        float lightZ = 1f;
        float effR = local.R / 255f, effG = local.G / 255f, effB = local.B / 255f;
        float lightIntensity = 1f;

        if (_item.Channel != LightChannelOrOff.Off
            && LightSignalStore.TryResolve(effectDescription.SceneId, effectDescription.Usage, (LightChannel)_item.Channel,
                effectDescription.TimelinePosition.Frame, fps, isHeldRender, itemPos, out var light))
        {
            dir = LightMath.Rotate(light.Dir, angleOffset);
            lightZ = Math.Clamp(light.Height / 400f, 0.05f, 4f);
            effR = float.Lerp(local.R / 255f, light.Color.X, colorMix);
            effG = float.Lerp(local.G / 255f, light.Color.Y, colorMix);
            effB = float.Lerp(local.B / 255f, light.Color.Z, colorMix);
            lightIntensity = light.Intensity;
        }
        else
        {
            // 連動無し: 角度オフセットを絶対角として扱う
            dir = LightMath.DirFromAngle(angleOffset);
        }

        // 受光量は色へ掛ける（シェーダー側で 1 にクランプされる）。
        // 光の量そのものは CrossFade の重みで表す。
        effR *= lightGain;
        effG *= lightGain;
        effB *= lightGain;

        float weight = Math.Clamp(localIntensity * lightIntensity, 0f, 1f);

        if (_isFirst || dir.X != _lastDirX) { _light.LightDirX = dir.X; _lastDirX = dir.X; }
        if (_isFirst || dir.Y != _lastDirY) { _light.LightDirY = dir.Y; _lastDirY = dir.Y; }
        if (_isFirst || lightZ != _lastZ) { _light.LightZ = lightZ; _lastZ = lightZ; }
        if (_isFirst || formScale != _lastForm) { _light.FormScale = formScale; _lastForm = formScale; }
        if (_isFirst || wrap != _lastWrap) { _light.Wrap = wrap; _lastWrap = wrap; }
        if (_isFirst || diffuse != _lastDiffuse) { _light.Diffuse = diffuse; _lastDiffuse = diffuse; }
        if (_isFirst || highlight != _lastHighlight) { _light.Highlight = highlight; _lastHighlight = highlight; }
        if (_isFirst || shininess != _lastShininess) { _light.Shininess = shininess; _lastShininess = shininess; }
        if (_isFirst || effR != _lastR) { _light.ColorR = effR; _lastR = effR; }
        if (_isFirst || effG != _lastG) { _light.ColorG = effG; _lastG = effG; }
        if (_isFirst || effB != _lastB) { _light.ColorB = effB; _lastB = effB; }
        if (_isFirst || blur != _lastBlur) { _blur.StandardDeviation = blur; _lastBlur = blur; }

        var blendMode = _item.BlendMode;
        if (_isFirst || blendMode != _lastBlendMode)
        {
            if (blendMode.IsCompositionEffect())
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
        }
        if (_isFirst || weight != _lastWeight) { _crossFade.Weight = weight; _lastWeight = weight; }

        _isFirst = false;
        return drawDesc;
    }
}
