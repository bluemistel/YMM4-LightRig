using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using LightRig.Effects;

namespace LightRig.Effects.LightReceive;

/// <summary>
/// 受光の「光レイヤー」用 Direct2D カスタムシェーダーエフェクト。
/// 擬似法線から求めた光の当たり具合を、光源色の光レイヤーとして出力する。
/// </summary>
public sealed class LightReceiveCustomEffect : D2D1CustomShaderEffectBase
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
        ColorR,
        ColorG,
        ColorB,
        Blur,
    }

    public float LightDirX  { set => SetValue((int)PropertyIndex.LightDirX, value); }
    public float LightDirY  { set => SetValue((int)PropertyIndex.LightDirY, value); }
    public float LightZ     { set => SetValue((int)PropertyIndex.LightZ, value); }
    public float FormScale  { set => SetValue((int)PropertyIndex.FormScale, value); }
    public float Wrap       { set => SetValue((int)PropertyIndex.Wrap, value); }
    public float Diffuse    { set => SetValue((int)PropertyIndex.Diffuse, value); }
    public float Highlight  { set => SetValue((int)PropertyIndex.Highlight, value); }
    public float Shininess  { set => SetValue((int)PropertyIndex.Shininess, value); }
    public float ColorR     { set => SetValue((int)PropertyIndex.ColorR, value); }
    public float ColorG     { set => SetValue((int)PropertyIndex.ColorG, value); }
    public float ColorB     { set => SetValue((int)PropertyIndex.ColorB, value); }
    public float Blur       { set => SetValue((int)PropertyIndex.Blur, value); }

    public LightReceiveCustomEffect(IGraphicsDevicesAndContext devices)
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

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ColorR)]
        public float ColorR { get => _cb.ColorR; set { _cb.ColorR = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ColorG)]
        public float ColorG { get => _cb.ColorG; set { _cb.ColorG = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ColorB)]
        public float ColorB { get => _cb.ColorB; set { _cb.ColorB = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Blur)]
        public float Blur { get => _cb.Blur; set { _cb.Blur = value; UpdateConstants(); } }

        public EffectImpl() : base(ShaderResourceLoader.Get("LightReceivePS.cso")) { }

        protected override void UpdateConstants()
            => drawInformation?.SetPixelShaderConstantBuffer(_cb);

        /// <summary>擬似法線のリング半径（フォルム）とぼかし量のぶんだけ外側を読む。</summary>
        private int Range => Math.Clamp(
            (int)MathF.Ceiling(MathF.Abs(_cb.FormScale) + MathF.Abs(_cb.Blur)) + 1, 1, 8192);

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
            inputRects[0] = new RawRect(
                outputRect.Left - r, outputRect.Top - r, outputRect.Right + r, outputRect.Bottom + r);
        }

        // HLSL の cbuffer と型・順序・並びを厳密に一致させること（float 12個 = 48byte, 16byte境界OK）。
        [StructLayout(LayoutKind.Sequential)]
        private struct ConstantBuffer
        {
            public float LightDirX, LightDirY, LightZ, FormScale;
            public float Wrap, Diffuse, Highlight, Shininess;
            public float ColorR, ColorG, ColorB, Blur;
        }
    }
}
