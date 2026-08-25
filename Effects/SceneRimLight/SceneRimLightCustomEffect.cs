using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using LightRig.Effects;

namespace LightRig.Effects.SceneRimLight;

/// <summary>シーン連動リムライト用 Direct2D カスタムシェーダーエフェクト（色付きリムを生成）。</summary>
public sealed class SceneRimLightCustomEffect : D2D1CustomShaderEffectBase
{
    private enum PropertyIndex
    {
        LightDirX = 0,
        LightDirY,
        RimWidth,
        Softness,
        ColorR,
        ColorG,
        ColorB,
    }

    public float LightDirX { set => SetValue((int)PropertyIndex.LightDirX, value); }
    public float LightDirY { set => SetValue((int)PropertyIndex.LightDirY, value); }
    public float RimWidth  { set => SetValue((int)PropertyIndex.RimWidth, value); }
    public float Softness  { set => SetValue((int)PropertyIndex.Softness, value); }
    public float ColorR    { set => SetValue((int)PropertyIndex.ColorR, value); }
    public float ColorG    { set => SetValue((int)PropertyIndex.ColorG, value); }
    public float ColorB    { set => SetValue((int)PropertyIndex.ColorB, value); }

    public SceneRimLightCustomEffect(IGraphicsDevicesAndContext devices)
        : base(Create<EffectImpl>(devices)) { }

    [CustomEffect(1)]
    private sealed class EffectImpl : D2D1CustomShaderEffectImplBase<EffectImpl>
    {
        private ConstantBuffer _cb;

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.LightDirX)]
        public float LightDirX { get => _cb.LightDirX; set { _cb.LightDirX = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.LightDirY)]
        public float LightDirY { get => _cb.LightDirY; set { _cb.LightDirY = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.RimWidth)]
        public float RimWidth { get => _cb.RimWidth; set { _cb.RimWidth = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Softness)]
        public float Softness { get => _cb.Softness; set { _cb.Softness = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ColorR)]
        public float ColorR { get => _cb.ColorR; set { _cb.ColorR = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ColorG)]
        public float ColorG { get => _cb.ColorG; set { _cb.ColorG = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ColorB)]
        public float ColorB { get => _cb.ColorB; set { _cb.ColorB = value; UpdateConstants(); } }

        public EffectImpl() : base(ShaderResourceLoader.Get("SceneRimLightPS.cso")) { }

        protected override void UpdateConstants()
            => drawInformation?.SetPixelShaderConstantBuffer(_cb);

        private int Range => (int)MathF.Ceiling(MathF.Abs(_cb.RimWidth)) + 1;

        public override void MapInputRectsToOutputRect(
            RawRect[] inputRects, RawRect[] inputOpaqueSubRects,
            out RawRect outputRect, out RawRect outputOpaqueSubRect)
        {
            var i = inputRects[0];
            int r = Range;
            outputRect = new RawRect(i.Left - r, i.Top - r, i.Right + r, i.Bottom + r);
            outputOpaqueSubRect = default;
        }

        public override void MapOutputRectToInputRects(RawRect outputRect, RawRect[] inputRects)
        {
            int r = Range;
            inputRects[0] = new RawRect(outputRect.Left - r, outputRect.Top - r, outputRect.Right + r, outputRect.Bottom + r);
        }

        // HLSL の cbuffer と型・順序・並びを厳密に一致させること（float8個=16byte境界OK）。
        [StructLayout(LayoutKind.Sequential)]
        private struct ConstantBuffer
        {
            public float LightDirX;
            public float LightDirY;
            public float RimWidth;
            public float Softness;
            public float ColorR;
            public float ColorG;
            public float ColorB;
            public float Pad;
        }
    }
}
