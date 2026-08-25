using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using LightRig.Effects;

namespace LightRig.Effects.DepthFog;

/// <summary>
/// 空気遠近用 Direct2D カスタムシェーダーエフェクト（フォグ色を density で混ぜるだけ）。
/// per-pixel 処理なので入出力の矩形は同じ（Map* のオーバーライド不要）。
/// </summary>
public sealed class DepthFogCustomEffect : D2D1CustomShaderEffectBase
{
    private enum PropertyIndex
    {
        FogR = 0,
        FogG,
        FogB,
        Density,
    }

    public float FogR    { set => SetValue((int)PropertyIndex.FogR, value); }
    public float FogG    { set => SetValue((int)PropertyIndex.FogG, value); }
    public float FogB    { set => SetValue((int)PropertyIndex.FogB, value); }
    public float Density { set => SetValue((int)PropertyIndex.Density, value); }

    public DepthFogCustomEffect(IGraphicsDevicesAndContext devices)
        : base(Create<EffectImpl>(devices)) { }

    [CustomEffect(1)]
    private sealed class EffectImpl : D2D1CustomShaderEffectImplBase<EffectImpl>
    {
        private ConstantBuffer _cb;

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.FogR)]
        public float FogR { get => _cb.FogR; set { _cb.FogR = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.FogG)]
        public float FogG { get => _cb.FogG; set { _cb.FogG = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.FogB)]
        public float FogB { get => _cb.FogB; set { _cb.FogB = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Density)]
        public float Density { get => _cb.Density; set { _cb.Density = value; UpdateConstants(); } }

        public EffectImpl() : base(ShaderResourceLoader.Get("DepthFogPS.cso")) { }

        protected override void UpdateConstants()
            => drawInformation?.SetPixelShaderConstantBuffer(_cb);

        // HLSL の cbuffer と型・順序・並びを厳密に一致させること（float4個=16byte境界OK）。
        [StructLayout(LayoutKind.Sequential)]
        private struct ConstantBuffer
        {
            public float FogR;
            public float FogG;
            public float FogB;
            public float Density;
        }
    }
}
