using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using LightRig.Effects;

namespace LightRig.Effects.SceneShading;

/// <summary>シーン連動シェーディング用 Direct2D カスタムシェーダーエフェクト（陰影付き画像を出力）。</summary>
public sealed class SceneShadingCustomEffect : D2D1CustomShaderEffectBase
{
    private enum PropertyIndex
    {
        LightDirX = 0,
        LightDirY,
        LightZ,
        Width,
        Strength,
        ShadeR,
        ShadeG,
        ShadeB,
        Mode,
        Wrap,
        Softness,
        Blur,
        FormScale,
    }

    public float LightDirX { set => SetValue((int)PropertyIndex.LightDirX, value); }
    public float LightDirY { set => SetValue((int)PropertyIndex.LightDirY, value); }
    public float LightZ    { set => SetValue((int)PropertyIndex.LightZ, value); }
    public float Width     { set => SetValue((int)PropertyIndex.Width, value); }
    public float Strength  { set => SetValue((int)PropertyIndex.Strength, value); }
    public float ShadeR    { set => SetValue((int)PropertyIndex.ShadeR, value); }
    public float ShadeG    { set => SetValue((int)PropertyIndex.ShadeG, value); }
    public float ShadeB    { set => SetValue((int)PropertyIndex.ShadeB, value); }
    public float Mode      { set => SetValue((int)PropertyIndex.Mode, value); }
    public float Wrap      { set => SetValue((int)PropertyIndex.Wrap, value); }
    public float Softness  { set => SetValue((int)PropertyIndex.Softness, value); }
    public float Blur      { set => SetValue((int)PropertyIndex.Blur, value); }
    public float FormScale { set => SetValue((int)PropertyIndex.FormScale, value); }

    public SceneShadingCustomEffect(IGraphicsDevicesAndContext devices)
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

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Width)]
        public float Width { get => _cb.Width; set { _cb.Width = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Strength)]
        public float Strength { get => _cb.Strength; set { _cb.Strength = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ShadeR)]
        public float ShadeR { get => _cb.ShadeR; set { _cb.ShadeR = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ShadeG)]
        public float ShadeG { get => _cb.ShadeG; set { _cb.ShadeG = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ShadeB)]
        public float ShadeB { get => _cb.ShadeB; set { _cb.ShadeB = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Mode)]
        public float Mode { get => _cb.Mode; set { _cb.Mode = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Wrap)]
        public float Wrap { get => _cb.Wrap; set { _cb.Wrap = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Softness)]
        public float Softness { get => _cb.Softness; set { _cb.Softness = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Blur)]
        public float Blur { get => _cb.Blur; set { _cb.Blur = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.FormScale)]
        public float FormScale { get => _cb.FormScale; set { _cb.FormScale = value; UpdateConstants(); } }

        public EffectImpl() : base(ShaderResourceLoader.Get("SceneShadingPS.cso")) { }

        protected override void UpdateConstants()
            => drawInformation?.SetPixelShaderConstantBuffer(_cb);

        // エッジ=幅+ぼかし、擬似ノーマル=フォルム半径。両モードの大きい方で矩形を拡張する。
        private int Range =>
            (int)MathF.Ceiling(MathF.Max(
                MathF.Abs(_cb.Width) + MathF.Abs(_cb.Blur),
                MathF.Abs(_cb.FormScale))) + 2;

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
            public float Width;
            public float Strength;
            public float ShadeR;
            public float ShadeG;
            public float ShadeB;
            public float Mode;
            public float Wrap;
            public float Softness;
            public float Blur;
            public float FormScale;
            public float Pad0;
            public float Pad1;
            public float Pad2;
        }
    }
}
