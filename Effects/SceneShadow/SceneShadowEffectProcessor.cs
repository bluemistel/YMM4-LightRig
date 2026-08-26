using System.Numerics;
using Vortice.Direct2D1;
using D2DEffects = Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;
using LightRig.Shared;

namespace LightRig.Effects.SceneShadow;

/// <summary>
/// シーン連動 落とし影。影を別レイヤーとして生成し、ぼかしてから元画像の「背面」へ合成する。
///   Input ─[SceneShadow(影マスク)]─[GaussianBlur]─[Composite in0]
///   Input ─────────────────────────────────────────[Composite in1]─ Output
/// Composite は SourceOver（in1 が in0 の上）なので、立ち絵が影の手前に来る。
///
/// 影の向き・長さは <see cref="LightSignalStore"/> の光源方向と高さから算出し、
/// 「長さ」「傾き」「接地位置」で手動補正できる。光源が無ければ角度オフセットを絶対角として単体動作する。
/// </summary>
internal sealed class SceneShadowEffectProcessor : VideoEffectProcessorBase
{
    private readonly SceneShadowEffect _item;
    private SceneShadowCustomEffect? _shadow;
    private D2DEffects.GaussianBlur? _blur;
    private D2DEffects.Composite? _composite;

    private bool _isFirst = true;
    private float _lastLean, _lastLengthRatio, _lastGround, _lastOpacity, _lastTipBlur, _lastBlur;
    private float _lastR = -1, _lastG = -1, _lastB = -1;

    public SceneShadowEffectProcessor(IGraphicsDevicesAndContext devices, SceneShadowEffect item)
        : base(devices)
    {
        _item = item;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        var dc = devices.DeviceContext;

        _shadow = new SceneShadowCustomEffect(devices);
        if (!_shadow.IsEnabled)
        {
            _shadow.Dispose();
            _shadow = null;
            return null;
        }
        disposer.Collect(_shadow);

        _blur = new D2DEffects.GaussianBlur(dc);
        disposer.Collect(_blur);
        using (var shadowOut = _shadow.Output)
            _blur.SetInput(0, shadowOut, true);

        // in0 = 影（下）, in1 = 元画像（上）
        _composite = new D2DEffects.Composite(dc) { InputCount = 2, Mode = CompositeMode.SourceOver };
        disposer.Collect(_composite);
        using (var blurOut = _blur.Output)
            _composite.SetInput(0, blurOut, true);

        var output = _composite.Output;
        disposer.Collect(output);
        return output;
    }

    protected override void setInput(ID2D1Image? input)
    {
        _shadow?.SetInput(0, input, true);
        _composite?.SetInput(1, input, true);
    }

    protected override void ClearEffectChain()
    {
        _shadow?.SetInput(0, null, true);
        _composite?.SetInput(0, null, true);
        _composite?.SetInput(1, null, true);
    }

    public override DrawDescription Update(EffectDescription effectDescription)
    {
        if (IsPassThroughEffect || _shadow is null || _blur is null || _composite is null)
            return effectDescription.DrawDescription;

        var drawDesc = effectDescription.DrawDescription;
        var frame = effectDescription.ItemPosition.Frame;
        var length = effectDescription.ItemDuration.Frame;
        var fps = effectDescription.FPS;

        var angleOffset = (float)_item.AngleOffset.GetValue(frame, length, fps);
        var lengthMul = (float)(_item.Length.GetValue(frame, length, fps) / 100.0);
        var lean = (float)(_item.Lean.GetValue(frame, length, fps) / 100.0);
        var ground = (float)_item.GroundOffset.GetValue(frame, length, fps);
        var opacity = (float)(_item.Opacity.GetValue(frame, length, fps) / 100.0);
        var blur = (float)_item.Blur.GetValue(frame, length, fps);
        var tipBlur = (float)_item.TipBlur.GetValue(frame, length, fps);
        var col = _item.ShadowColor;

        var itemPos = new Vector2(drawDesc.Draw.X, drawDesc.Draw.Y);
        Vector2 dir;
        float lightHeight = 200f; // 光源が無い時の既定の高さ

        if (_item.Channel != LightChannelOrOff.Off
            && LightSignalStore.TryResolve(effectDescription.SceneId, effectDescription.Usage, (LightChannel)_item.Channel,
                effectDescription.TimelinePosition.Frame, fps, itemPos, out var light))
        {
            dir = LightMath.Rotate(light.Dir, angleOffset);
            lightHeight = light.Height;
            // 光が届かない位置なら影も落ちない
            opacity *= light.Reach;
        }
        else
        {
            dir = LightMath.DirFromAngle(angleOffset);
        }

        // 影は光の反対側へ倒れる。光が真上（dir.x=0）なら傾き 0＝足元にまっすぐ落ちる。
        // lean は「出力1pxの高さあたりの横ずれ量」なので、影の横幅は 影の長さ×|lean| で頭打ちになる。
        float leanValue = Math.Clamp(-dir.X * lean, -3f, 3f);

        // 光源が高いほど影は短い。長さ倍率で手動補正する。
        // lengthRatio は「影の長さ ÷ 立ち絵の高さ」。1 なら立ち絵と同じ高さまで伸びる。
        float autoRatio = 1f / Math.Clamp(1f + MathF.Abs(lightHeight) / 200f, 1f, 20f);
        float lengthRatio = Math.Clamp(autoRatio * lengthMul, 0.02f, 3f);

        float r = col.R / 255f, g = col.G / 255f, b = col.B / 255f;

        if (_isFirst || leanValue != _lastLean) { _shadow.Lean = leanValue; _lastLean = leanValue; }
        if (_isFirst || lengthRatio != _lastLengthRatio) { _shadow.LengthRatio = lengthRatio; _lastLengthRatio = lengthRatio; }
        if (_isFirst || ground != _lastGround) { _shadow.GroundOffset = ground; _lastGround = ground; }
        if (_isFirst || opacity != _lastOpacity) { _shadow.Opacity = opacity; _lastOpacity = opacity; }
        if (_isFirst || tipBlur != _lastTipBlur) { _shadow.TipBlur = tipBlur; _lastTipBlur = tipBlur; }
        if (_isFirst || r != _lastR) { _shadow.ShadowR = r; _lastR = r; }
        if (_isFirst || g != _lastG) { _shadow.ShadowG = g; _lastG = g; }
        if (_isFirst || b != _lastB) { _shadow.ShadowB = b; _lastB = b; }
        if (_isFirst || blur != _lastBlur) { _blur.StandardDeviation = blur; _lastBlur = blur; }

        _isFirst = false;
        return drawDesc;
    }
}
