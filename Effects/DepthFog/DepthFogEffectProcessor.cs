using System.Numerics;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;
using LightRig.Shared;

namespace LightRig.Effects.DepthFog;

/// <summary>
/// 空気遠近。カメラ距離から霞の濃度を算出し、単一シェーダーでフォグ色を混ぜる。
///
/// 距離計算は 2DCamera プラグインの DOF と同じ式（Z_DepthofField 由来）。
/// <see cref="DrawDescription"/> の Camera 行列を逆変換して視点と視軸を求め、
/// アイテム位置（Draw）との関係から距離を出す。DrawDescription だけで完結するので他プラグイン不要。
/// </summary>
internal sealed class DepthFogEffectProcessor : VideoEffectProcessorBase
{
    private readonly DepthFogEffect _item;
    private DepthFogCustomEffect? _fog;

    private bool _isFirst = true;
    private float _lastR = -1, _lastG = -1, _lastB = -1, _lastDensity = -1;

    public DepthFogEffectProcessor(IGraphicsDevicesAndContext devices, DepthFogEffect item)
        : base(devices)
    {
        _item = item;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        _fog = new DepthFogCustomEffect(devices);
        if (!_fog.IsEnabled)
        {
            _fog.Dispose();
            _fog = null;
            return null;
        }
        disposer.Collect(_fog);

        var output = _fog.Output;
        disposer.Collect(output);
        return output;
    }

    protected override void setInput(ID2D1Image? input)
        => _fog?.SetInput(0, input, true);

    protected override void ClearEffectChain()
        => _fog?.SetInput(0, null, true);

    public override DrawDescription Update(EffectDescription effectDescription)
    {
        if (IsPassThroughEffect || _fog is null)
            return effectDescription.DrawDescription;

        var drawDesc = effectDescription.DrawDescription;
        var frame = effectDescription.ItemPosition.Frame;
        var length = effectDescription.ItemDuration.Frame;
        var fps = effectDescription.FPS;

        // --- カメラとアイテムの距離（2DCamera の DOF と同じ算出） ---
        if (!Matrix4x4.Invert(drawDesc.Camera, out var invView))
            invView = Matrix4x4.Identity;

        // YMM4 標準のカメラ距離 1000 を基準にワールド上の視点位置を求める
        var eye = Vector3.Transform(new Vector3(0, 0, 1000), invView);
        var viewDir = Vector3.Normalize(new Vector3(-invView.M31, -invView.M32, -invView.M33));
        var rel = drawDesc.Draw - eye;
        float axial = Vector3.Dot(rel, viewDir);
        float raw = _item.Mode == FogDistanceMode.Planar ? MathF.Abs(axial) : rel.Length();

        // 標準カメラ位置（距離1000）を 0 とした「奥行き」に直す。
        // アイテムの Z 座標の指定と目盛りが一致するので、開始/終了距離を直感的に決められる
        // （既定位置のアイテム＝0、奥へ 1000 下げたアイテム＝1000）。
        const float d0 = 1000f;
        float distance = raw - d0;

        // --- 距離 → 濃度 ---
        var near = (float)_item.NearDistance.GetValue(frame, length, fps);
        var far = (float)_item.FarDistance.GetValue(frame, length, fps);
        var curve = (float)_item.Curve.GetValue(frame, length, fps);
        var maxDensity = (float)(_item.MaxDensity.GetValue(frame, length, fps) / 100.0);

        float t = far - near > 1e-3f
            ? Math.Clamp((distance - near) / (far - near), 0f, 1f)
            : (distance >= far ? 1f : 0f);
        float density = MathF.Pow(t, MathF.Max(curve, 0.01f)) * maxDensity;
        if (!float.IsFinite(density))
            density = 0f;

        // --- フォグ色（手動色 ↔ 環境光サンプラーの測定色） ---
        var c = _item.FogColor;
        var fog = new Vector3(c.R / 255f, c.G / 255f, c.B / 255f);

        var ambientMix = (float)(_item.AmbientMix.GetValue(frame, length, fps) / 100.0);
        if (_item.AmbientChannel != LightChannelOrOff.Off && ambientMix > 0f
            && AmbientSignalStore.TryGet(effectDescription.SceneId, effectDescription.Usage,
                                         (LightChannel)_item.AmbientChannel, out var ambient))
        {
            fog = Vector3.Lerp(fog, new Vector3(ambient.X, ambient.Y, ambient.Z), ambientMix);
        }

        if (_isFirst || fog.X != _lastR) { _fog.FogR = fog.X; _lastR = fog.X; }
        if (_isFirst || fog.Y != _lastG) { _fog.FogG = fog.Y; _lastG = fog.Y; }
        if (_isFirst || fog.Z != _lastB) { _fog.FogB = fog.Z; _lastB = fog.Z; }
        if (_isFirst || density != _lastDensity) { _fog.Density = density; _lastDensity = density; }

        _isFirst = false;
        return drawDesc;
    }
}
