using System.Numerics;
using System.Windows.Media;
using Vortice.Direct2D1;
using D2DEffects = Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;
using YukkuriMovieMaker.Project;
using LightRig.Shared;

namespace LightRig.Effects.SceneRimLight;

/// <summary>
/// シーン連動リムライト。グラフ構成は EmoiEffect の RimLight と同型:
///   Input ─┬─────────────────────────────[Composite/Blend in0]──[CrossFade in0]
///          └[SceneRimLight]─[GaussianBlur]─[Composite/Blend in1]        │
///   Input ─────────────────────────────────────────────[CrossFade in1] ─ Output
/// 光源方向・色は <see cref="LightSignalStore"/> から取得し、無ければ手動角度・固定色で動作する。
/// </summary>
internal sealed class SceneRimLightEffectProcessor : VideoEffectProcessorBase
{
    private readonly SceneRimLightEffect _item;
    private SceneRimLightCustomEffect? _rim;
    private D2DEffects.GaussianBlur? _blur;
    private D2DEffects.GaussianBlur? _silhouette; // 入力1へ渡すシルエットのぼかし
    private D2DEffects.Composite? _composite;
    private D2DEffects.Blend? _blend;
    private D2DEffects.CrossFade? _crossFade;

    private bool _isFirst = true;
    private float _lastDirX, _lastDirY, _lastRimWidth, _lastSoftness, _lastBlur, _lastWeight;
    private float _lastSilhouette = -1f, _lastEdgeMode = -1f;
    private float _lastR = -1, _lastG = -1, _lastB = -1;
    private YukkuriMovieMaker.Project.Blend _lastBlendMode;

    public SceneRimLightEffectProcessor(IGraphicsDevicesAndContext devices, SceneRimLightEffect item)
        : base(devices)
    {
        _item = item;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        var dc = devices.DeviceContext;

        _rim = new SceneRimLightCustomEffect(devices);
        if (!_rim.IsEnabled)
        {
            _rim.Dispose();
            _rim = null;
            return null;
        }
        disposer.Collect(_rim);

        // 入力1: 元画像をガウスぼかししたシルエット。
        // 未接続の入力があると描画できないので、方式に関わらず必ず繋いでおく。
        _silhouette = new D2DEffects.GaussianBlur(dc);
        disposer.Collect(_silhouette);
        using (var silhouetteOut = _silhouette.Output)
            _rim.SetInput(1, silhouetteOut, true);

        _blur = new D2DEffects.GaussianBlur(dc);
        disposer.Collect(_blur);
        using (var rimOut = _rim.Output)
            _blur.SetInput(0, rimOut, true);

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
        _rim?.SetInput(0, input, true);
        _silhouette?.SetInput(0, input, true);
        _composite?.SetInput(0, input, true);
        _blend?.SetInput(0, input, true);
        _crossFade?.SetInput(1, input, true);
    }

    protected override void ClearEffectChain()
    {
        _rim?.SetInput(0, null, true);
        _silhouette?.SetInput(0, null, true);
        _composite?.SetInput(0, null, true);
        _composite?.SetInput(1, null, true);
        _blend?.SetInput(0, null, true);
        _blend?.SetInput(1, null, true);
        _crossFade?.SetInput(0, null, true);
        _crossFade?.SetInput(1, null, true);
    }

    public override DrawDescription Update(EffectDescription effectDescription)
    {
        if (IsPassThroughEffect || _rim is null || _blur is null || _silhouette is null
            || _composite is null || _blend is null || _crossFade is null)
            return effectDescription.DrawDescription;

        var drawDesc = effectDescription.DrawDescription;
        var frame = effectDescription.ItemPosition.Frame;
        var length = effectDescription.ItemDuration.Frame;
        var fps = effectDescription.FPS;

        var angleOffset = (float)_item.AngleOffset.GetValue(frame, length, fps);
        var rimWidth = (float)_item.RimWidth.GetValue(frame, length, fps);
        var blur = (float)_item.Blur.GetValue(frame, length, fps);
        var softness = (float)(_item.Softness.GetValue(frame, length, fps) / 100.0);
        var localIntensity = (float)(_item.Intensity.GetValue(frame, length, fps) / 100.0);
        var colorMix = (float)(_item.ColorMix.GetValue(frame, length, fps) / 100.0);
        var colorTune = (float)(_item.ColorTune.GetValue(frame, length, fps) / 100.0);
        var silhouetteBlur = (float)_item.SilhouetteBlur.GetValue(frame, length, fps);
        var edgeMode = (float)(int)_item.EdgeMode;
        var local = _item.LocalColor;

        // --- 光源の解決（連動 or 単体） ---
        // 場面切り替えの前後どちら側の描画か。同じ側の発信を優先して結び付ける
        // （前の場面にいる立ち絵が次の場面の光や環境光を拾わないようにする）。
        var isHeldRender = RenderSide.IsHeld(effectDescription);
        var itemPos = new Vector2(drawDesc.Draw.X, drawDesc.Draw.Y);
        Vector2 dir;
        float effR = local.R / 255f, effG = local.G / 255f, effB = local.B / 255f;
        float lightIntensity = 1f;

        if (_item.Channel != LightChannelOrOff.Off
            && LightSignalStore.TryResolve(effectDescription.SceneId, effectDescription.Usage, (LightChannel)_item.Channel,
                effectDescription.TimelinePosition.Frame, fps, isHeldRender, itemPos, out var light))
        {
            dir = LightMath.Rotate(light.Dir, angleOffset);

            // 色源の解決。背景色を選んだ場合は環境光サンプラーから取り、
            // そのままだと暗い背景で暗い縁光になるので「光源らしい色」へ整形する。
            var source = new Vector3(light.Color.X, light.Color.Y, light.Color.Z);
            if (_item.ColorSource == RimColorSource.Ambient
                && AmbientSignalStore.TryGet(effectDescription.SceneId, effectDescription.Usage, (LightChannel)_item.Channel,
                    effectDescription.TimelinePosition.Frame, isHeldRender, itemPos, out var ambient))
            {
                source = ColorGrading.TuneLightColor(
                    new Vector3(ambient.X, ambient.Y, ambient.Z), colorTune);
            }

            // 色源の色と固定色をミックス
            effR = float.Lerp(local.R / 255f, source.X, colorMix);
            effG = float.Lerp(local.G / 255f, source.Y, colorMix);
            effB = float.Lerp(local.B / 255f, source.Z, colorMix);
            // 光源強度 × ゆらぎ（frame から決定的に再計算）
            lightIntensity = light.Intensity;
        }
        else
        {
            // 連動無し: 角度オフセットを絶対角として扱う
            dir = LightMath.DirFromAngle(angleOffset);
        }

        float weight = Math.Clamp(localIntensity * lightIntensity, 0f, 1f);

        if (_isFirst || dir.X != _lastDirX) { _rim.LightDirX = dir.X; _lastDirX = dir.X; }
        if (_isFirst || dir.Y != _lastDirY) { _rim.LightDirY = dir.Y; _lastDirY = dir.Y; }
        if (_isFirst || rimWidth != _lastRimWidth) { _rim.RimWidth = rimWidth; _lastRimWidth = rimWidth; }
        if (_isFirst || softness != _lastSoftness) { _rim.Softness = softness; _lastSoftness = softness; }
        if (_isFirst || effR != _lastR) { _rim.ColorR = effR; _lastR = effR; }
        if (_isFirst || effG != _lastG) { _rim.ColorG = effG; _lastG = effG; }
        if (_isFirst || effB != _lastB) { _rim.ColorB = effB; _lastB = effB; }
        if (_isFirst || blur != _lastBlur) { _blur.StandardDeviation = blur; _lastBlur = blur; }
        if (_isFirst || edgeMode != _lastEdgeMode) { _rim.Mode = edgeMode; _lastEdgeMode = edgeMode; }
        // アルファ差分方式では入力1を使わないので、無駄なぼかしを避けて 0 にする
        var silhouetteSigma = edgeMode >= 0.5f ? silhouetteBlur : 0f;
        if (_isFirst || silhouetteSigma != _lastSilhouette)
        {
            _silhouette.StandardDeviation = silhouetteSigma;
            _lastSilhouette = silhouetteSigma;
        }

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
