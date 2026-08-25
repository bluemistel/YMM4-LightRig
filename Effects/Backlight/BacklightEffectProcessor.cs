using System.Numerics;
using System.Windows.Media;
using Vortice.Direct2D1;
using D2DEffects = Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;
using YukkuriMovieMaker.Project;
using LightRig.Effects.SceneRimLight;
using LightRig.Shared;

namespace LightRig.Effects.Backlight;

/// <summary>
/// 逆光。リムライトと同型の多段グラフで構成する:
///   Input ─[Backlight(減光+脱色)]─┬──────────────────────[Composite/Blend in0]──[CrossFade in0]
///                                 └──────────────────────[CrossFade in1]              │
///   Input ─[SceneRimLight]─[GaussianBlur]────────────────[Composite/Blend in1]        └─ Output
///
/// リムを別レイヤーにして GaussianBlur を通すことで、境界を本当に柔らかくできる
/// （1シェーダーに焼き込むと「上から塗った」硬い見た目になる）。
/// CrossFade の weight でリムの量を、合成モードで重ね方を選べる。
/// 光源方向・光色・強度（ゆらぎ込み）は <see cref="LightSignalStore"/> から取得する。
/// </summary>
internal sealed class BacklightEffectProcessor : VideoEffectProcessorBase
{
    private readonly BacklightEffect _item;
    private BacklightCustomEffect? _body;
    private SceneRimLightCustomEffect? _rim;
    private D2DEffects.GaussianBlur? _blur;
    private D2DEffects.Composite? _composite;
    private D2DEffects.Blend? _blend;
    private D2DEffects.CrossFade? _crossFade;

    private bool _isFirst = true;
    private float _lastDirX, _lastDirY, _lastRimWidth, _lastSoftness, _lastBlur, _lastWeight, _lastDim, _lastDesat;
    private float _lastR = -1, _lastG = -1, _lastB = -1;
    private YukkuriMovieMaker.Project.Blend _lastBlendMode;

    public BacklightEffectProcessor(IGraphicsDevicesAndContext devices, BacklightEffect item)
        : base(devices)
    {
        _item = item;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        var dc = devices.DeviceContext;

        _body = new BacklightCustomEffect(devices);
        _rim = new SceneRimLightCustomEffect(devices);
        if (!_body.IsEnabled || !_rim.IsEnabled)
        {
            _body.Dispose(); _body = null;
            _rim.Dispose(); _rim = null;
            return null;
        }
        disposer.Collect(_body);
        disposer.Collect(_rim);

        _blur = new D2DEffects.GaussianBlur(dc);
        disposer.Collect(_blur);
        using (var rimOut = _rim.Output)
            _blur.SetInput(0, rimOut, true);

        // 合成の下地は「減光済みの本体」。リムは上に足す。
        _composite = new D2DEffects.Composite(dc) { InputCount = 2 };
        disposer.Collect(_composite);
        using (var bodyOut = _body.Output)
            _composite.SetInput(0, bodyOut, true);
        using (var blurOut = _blur.Output)
            _composite.SetInput(1, blurOut, true);

        _blend = new D2DEffects.Blend(dc);
        disposer.Collect(_blend);
        using (var bodyOut = _body.Output)
            _blend.SetInput(0, bodyOut, true);
        using (var blurOut = _blur.Output)
            _blend.SetInput(1, blurOut, true);

        // weight=0 でリム無し（＝減光のみ）になるよう、in1 には本体を繋ぐ
        _crossFade = new D2DEffects.CrossFade(dc);
        disposer.Collect(_crossFade);
        using (var bodyOut = _body.Output)
            _crossFade.SetInput(1, bodyOut, true);

        var output = _crossFade.Output;
        disposer.Collect(output);
        return output;
    }

    protected override void setInput(ID2D1Image? input)
    {
        _body?.SetInput(0, input, true);
        _rim?.SetInput(0, input, true);
    }

    protected override void ClearEffectChain()
    {
        _body?.SetInput(0, null, true);
        _rim?.SetInput(0, null, true);
        _composite?.SetInput(0, null, true);
        _composite?.SetInput(1, null, true);
        _blend?.SetInput(0, null, true);
        _blend?.SetInput(1, null, true);
        _crossFade?.SetInput(0, null, true);
        _crossFade?.SetInput(1, null, true);
    }

    public override DrawDescription Update(EffectDescription effectDescription)
    {
        if (IsPassThroughEffect || _body is null || _rim is null || _blur is null
            || _composite is null || _blend is null || _crossFade is null)
            return effectDescription.DrawDescription;

        var drawDesc = effectDescription.DrawDescription;
        var frame = effectDescription.ItemPosition.Frame;
        var length = effectDescription.ItemDuration.Frame;
        var fps = effectDescription.FPS;

        var angleOffset = (float)_item.AngleOffset.GetValue(frame, length, fps);
        var amount = (float)(_item.Amount.GetValue(frame, length, fps) / 100.0);
        var rimStrength = (float)(_item.RimStrength.GetValue(frame, length, fps) / 100.0);
        var rimWidth = (float)_item.RimWidth.GetValue(frame, length, fps);
        var softness = (float)(_item.Softness.GetValue(frame, length, fps) / 100.0);
        var blur = (float)_item.Blur.GetValue(frame, length, fps);
        var colorMix = (float)(_item.ColorMix.GetValue(frame, length, fps) / 100.0);
        var local = _item.LocalColor;

        var itemPos = new Vector2(drawDesc.Draw.X, drawDesc.Draw.Y);
        Vector2 dir;
        float effR = local.R / 255f, effG = local.G / 255f, effB = local.B / 255f;
        float lightIntensity = 1f;

        if (_item.Channel != LightChannelOrOff.Off
            && LightSignalStore.TryGet(effectDescription.SceneId, effectDescription.Usage, (LightChannel)_item.Channel, out var light))
        {
            dir = LightMath.Rotate(LightMath.ScreenDir(light, itemPos), angleOffset);
            effR = float.Lerp(local.R / 255f, light.Color.X, colorMix);
            effG = float.Lerp(local.G / 255f, light.Color.Y, colorMix);
            effB = float.Lerp(local.B / 255f, light.Color.Z, colorMix);
            lightIntensity = light.Intensity
                * LightMath.Flicker(frame, fps, light.FlickerAmount, light.FlickerSpeed, light.FlickerSeed)
                * LightMath.Attenuation(light, itemPos);
        }
        else
        {
            dir = LightMath.DirFromAngle(angleOffset);
        }

        // リムの量: 100% までは CrossFade の weight、それ以上は色を明るくして表現する
        float strength = MathF.Max(0f, rimStrength * lightIntensity);
        float weight = Math.Clamp(strength, 0f, 1f);
        float gain = Math.Clamp(MathF.Max(strength, 1f), 1f, 4f);
        effR *= gain; effG *= gain; effB *= gain;

        float dim = amount * 0.85f;
        float desat = amount * 0.7f;

        if (_isFirst || dim != _lastDim) { _body.Dim = dim; _lastDim = dim; }
        if (_isFirst || desat != _lastDesat) { _body.Desat = desat; _lastDesat = desat; }

        if (_isFirst || dir.X != _lastDirX) { _rim.LightDirX = dir.X; _lastDirX = dir.X; }
        if (_isFirst || dir.Y != _lastDirY) { _rim.LightDirY = dir.Y; _lastDirY = dir.Y; }
        if (_isFirst || rimWidth != _lastRimWidth) { _rim.RimWidth = rimWidth; _lastRimWidth = rimWidth; }
        if (_isFirst || softness != _lastSoftness) { _rim.Softness = softness; _lastSoftness = softness; }
        if (_isFirst || effR != _lastR) { _rim.ColorR = effR; _lastR = effR; }
        if (_isFirst || effG != _lastG) { _rim.ColorG = effG; _lastG = effG; }
        if (_isFirst || effB != _lastB) { _rim.ColorB = effB; _lastB = effB; }
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
