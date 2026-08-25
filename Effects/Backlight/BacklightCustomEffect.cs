using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using LightRig.Effects;

namespace LightRig.Effects.Backlight;

/// <summary>
/// 逆光の「本体」用 Direct2D カスタムシェーダーエフェクト（減光＋脱色のみ）。
/// リムは <see cref="SceneRimLight.SceneRimLightCustomEffect"/> を別レイヤーとして使い、
/// ぼかしてから合成モードで重ねる（Processor 側でグラフを構築）。
/// per-pixel 処理なので入出力の矩形は同じ（Map* のオーバーライド不要）。
/// </summary>
public sealed class BacklightCustomEffect : D2D1CustomShaderEffectBase
{
    private enum PropertyIndex
    {
        Dim = 0,
        Desat,
    }

    public float Dim   { set => SetValue((int)PropertyIndex.Dim, value); }
    public float Desat { set => SetValue((int)PropertyIndex.Desat, value); }

    public BacklightCustomEffect(IGraphicsDevicesAndContext devices)
        : base(Create<EffectImpl>(devices)) { }

    [CustomEffect(1)]
    private sealed class EffectImpl : D2D1CustomShaderEffectImplBase<EffectImpl>
    {
        private ConstantBuffer _cb;

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Dim)]
        public float Dim { get => _cb.Dim; set { _cb.Dim = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Desat)]
        public float Desat { get => _cb.Desat; set { _cb.Desat = value; UpdateConstants(); } }

        public EffectImpl() : base(ShaderResourceLoader.Get("BacklightPS.cso")) { }

        protected override void UpdateConstants()
            => drawInformation?.SetPixelShaderConstantBuffer(_cb);

        // HLSL の cbuffer と型・順序・並びを厳密に一致させること（float4個=16byte境界OK）。
        [StructLayout(LayoutKind.Sequential)]
        private struct ConstantBuffer
        {
            public float Dim;
            public float Desat;
            public float Pad0;
            public float Pad1;
        }
    }
}
