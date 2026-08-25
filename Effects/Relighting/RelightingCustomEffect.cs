using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using LightRig.Effects;

namespace LightRig.Effects.Relighting;

/// <summary>リライティング用 Direct2D カスタムシェーダーエフェクト（擬似法線で光色ライティングし直す）。</summary>
public sealed class RelightingCustomEffect : D2D1CustomShaderEffectBase
{
    private enum PropertyIndex
    {
        LightDirX = 0,
        LightDirY,
        LightZ,
        FormScale,
        Wrap,
        Diffuse,
        Highlight,
        Shininess,
        LightR,
        LightG,
        LightB,
        ShadowR,
        ShadowG,
        ShadowB,
        Intensity,
        Blur,
    }

    public float LightDirX { set => SetValue((int)PropertyIndex.LightDirX, value); }
    public float LightDirY { set => SetValue((int)PropertyIndex.LightDirY, value); }
    public float LightZ    { set => SetValue((int)PropertyIndex.LightZ, value); }
    public float FormScale { set => SetValue((int)PropertyIndex.FormScale, value); }
    public float Wrap      { set => SetValue((int)PropertyIndex.Wrap, value); }
    public float Diffuse   { set => SetValue((int)PropertyIndex.Diffuse, value); }
    public float Highlight { set => SetValue((int)PropertyIndex.Highlight, value); }
    public float Shininess { set => SetValue((int)PropertyIndex.Shininess, value); }
    public float LightR    { set => SetValue((int)PropertyIndex.LightR, value); }
    public float LightG    { set => SetValue((int)PropertyIndex.LightG, value); }
    public float LightB    { set => SetValue((int)PropertyIndex.LightB, value); }
    public float ShadowR   { set => SetValue((int)PropertyIndex.ShadowR, value); }
    public float ShadowG   { set => SetValue((int)PropertyIndex.ShadowG, value); }
    public float ShadowB   { set => SetValue((int)PropertyIndex.ShadowB, value); }
    public float Intensity { set => SetValue((int)PropertyIndex.Intensity, value); }
    public float Blur      { set => SetValue((int)PropertyIndex.Blur, value); }

    public RelightingCustomEffect(IGraphicsDevicesAndContext devices)
        : base(Create<EffectImpl>(devices)) { }

    [CustomEffect(1)]
    private sealed class EffectImpl : D2D1CustomShaderEffectImplBase<EffectImpl>
    {
        private ConstantBuffer _cb;

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.LightDirX)]
        public float LightDirX { get => _cb.LightDirX; set { _cb.LightDirX = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.LightDirY)]
        public float LightDirY { get => _cb.LightDirY; set { _cb.LightDirY = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.LightZ)]
        public float LightZ { get => _cb.LightZ; set { _cb.LightZ = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.FormScale)]
        public float FormScale { get => _cb.FormScale; set { _cb.FormScale = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Wrap)]
        public float Wrap { get => _cb.Wrap; set { _cb.Wrap = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Diffuse)]
        public float Diffuse { get => _cb.Diffuse; set { _cb.Diffuse = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Highlight)]
        public float Highlight { get => _cb.Highlight; set { _cb.Highlight = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Shininess)]
        public float Shininess { get => _cb.Shininess; set { _cb.Shininess = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.LightR)]
        public float LightR { get => _cb.LightR; set { _cb.LightR = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.LightG)]
        public float LightG { get => _cb.LightG; set { _cb.LightG = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.LightB)]
        public float LightB { get => _cb.LightB; set { _cb.LightB = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ShadowR)]
        public float ShadowR { get => _cb.ShadowR; set { _cb.ShadowR = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ShadowG)]
        public float ShadowG { get => _cb.ShadowG; set { _cb.ShadowG = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ShadowB)]
        public float ShadowB { get => _cb.ShadowB; set { _cb.ShadowB = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Intensity)]
        public float Intensity { get => _cb.Intensity; set { _cb.Intensity = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Blur)]
        public float Blur { get => _cb.Blur; set { _cb.Blur = value; UpdateConstants(); } }

        public EffectImpl() : base(ShaderResourceLoader.Get("RelightingPS.cso")) { }

        protected override void UpdateConstants()
            => drawInformation?.SetPixelShaderConstantBuffer(_cb);

        // フォルム半径＋ぼかしの外周ぶんを見込んで拡張
        private int Range => (int)MathF.Ceiling(MathF.Abs(_cb.FormScale) + MathF.Abs(_cb.Blur)) + 2;

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

        // HLSL の cbuffer と型・順序・並びを厳密に一致させること（float16個=16byte境界OK）。
        [StructLayout(LayoutKind.Sequential)]
        private struct ConstantBuffer
        {
            public float LightDirX;
            public float LightDirY;
            public float LightZ;
            public float FormScale;
            public float Wrap;
            public float Diffuse;
            public float Highlight;
            public float Shininess;
            public float LightR;
            public float LightG;
            public float LightB;
            public float ShadowR;
            public float ShadowG;
            public float ShadowB;
            public float Intensity;
            public float Blur;
        }
    }
}
