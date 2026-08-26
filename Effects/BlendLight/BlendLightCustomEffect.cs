using System.Numerics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using LightRig.Effects;

namespace LightRig.Effects.BlendLight;

/// <summary>
/// 背景なじませの「光レイヤー」用 Direct2D カスタムシェーダーエフェクト。
/// 背景の色（3x3 グリッド or 単色）を、光源方向のグラデーション／光源側の輪郭として出力する。
///
/// 背景グリッドは cbuffer にスカラーで持つ（2枚目の入力テクスチャにすると
/// 入力矩形のマッピングがタイル分割で破綻しやすいため）。
/// C# の ConstantBuffer と HLSL の cbuffer は型・順序・並びを厳密に一致させること。
/// </summary>
public sealed class BlendLightCustomEffect : D2D1CustomShaderEffectBase
{
    /// <summary>グリッド先頭（C0R）のプロパティ番号。以降 R,G,B の順に連番。</summary>
    private const int CellBase = 20;

    private enum PropertyIndex
    {
        LightDirX = 0,
        LightDirY,
        Spread,
        Mode,
        RimWidth,
        Softness,
        Saturation,
        Gain,
        InputLeft,
        InputTop,
        InputWidth,
        InputHeight,
        UvOriginX,
        UvOriginY,
        UvScaleX,
        UvScaleY,
        UseGrid,
        FallbackR,
        FallbackG,
        FallbackB,
        // グリッド（CellBase..CellBase+26）の後ろに続く
        Method = CellBase + 27,
        ToneStrength,
        LumaMatch,
    }

    public float LightDirX  { set => SetValue((int)PropertyIndex.LightDirX, value); }
    public float LightDirY  { set => SetValue((int)PropertyIndex.LightDirY, value); }
    public float Spread     { set => SetValue((int)PropertyIndex.Spread, value); }
    public float Mode       { set => SetValue((int)PropertyIndex.Mode, value); }
    public float RimWidth   { set => SetValue((int)PropertyIndex.RimWidth, value); }
    public float Softness   { set => SetValue((int)PropertyIndex.Softness, value); }
    public float Saturation { set => SetValue((int)PropertyIndex.Saturation, value); }
    public float Gain       { set => SetValue((int)PropertyIndex.Gain, value); }
    public float UvOriginX  { set => SetValue((int)PropertyIndex.UvOriginX, value); }
    public float UvOriginY  { set => SetValue((int)PropertyIndex.UvOriginY, value); }
    public float UvScaleX   { set => SetValue((int)PropertyIndex.UvScaleX, value); }
    public float UvScaleY   { set => SetValue((int)PropertyIndex.UvScaleY, value); }
    public float UseGrid    { set => SetValue((int)PropertyIndex.UseGrid, value); }
    public float FallbackR  { set => SetValue((int)PropertyIndex.FallbackR, value); }
    public float FallbackG  { set => SetValue((int)PropertyIndex.FallbackG, value); }
    public float FallbackB  { set => SetValue((int)PropertyIndex.FallbackB, value); }
    public float Method       { set => SetValue((int)PropertyIndex.Method, value); }
    public float ToneStrength { set => SetValue((int)PropertyIndex.ToneStrength, value); }
    public float LumaMatch    { set => SetValue((int)PropertyIndex.LumaMatch, value); }

    /// <summary>背景グリッドの i 番目（row-major）のセル色を設定する。</summary>
    public void SetCell(int i, Vector3 color)
    {
        int b = CellBase + i * 3;
        SetValue(b + 0, color.X);
        SetValue(b + 1, color.Y);
        SetValue(b + 2, color.Z);
    }

    public BlendLightCustomEffect(IGraphicsDevicesAndContext devices)
        : base(Create<EffectImpl>(devices)) { }

    [CustomEffect(1)]
    private sealed class EffectImpl : D2D1CustomShaderEffectImplBase<EffectImpl>
    {
        private ConstantBuffer _cb;

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.LightDirX)]
        public float LightDirX { get => _cb.LightDirX; set { _cb.LightDirX = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.LightDirY)]
        public float LightDirY { get => _cb.LightDirY; set { _cb.LightDirY = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Spread)]
        public float Spread { get => _cb.Spread; set { _cb.Spread = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Mode)]
        public float Mode { get => _cb.Mode; set { _cb.Mode = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.RimWidth)]
        public float RimWidth { get => _cb.RimWidth; set { _cb.RimWidth = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Softness)]
        public float Softness { get => _cb.Softness; set { _cb.Softness = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Saturation)]
        public float Saturation { get => _cb.Saturation; set { _cb.Saturation = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Gain)]
        public float Gain { get => _cb.Gain; set { _cb.Gain = value; UpdateConstants(); } }

        // 入力矩形は MapInputRectsToOutputRect で受け取る（D2D はここに画像全体の矩形を渡す）。
        // タイルごとに呼ばれる MapOutputRectToInputRects で更新してはいけない。
        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.InputLeft)]
        public float InputLeft { get => _cb.InputLeft; set { _cb.InputLeft = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.InputTop)]
        public float InputTop { get => _cb.InputTop; set { _cb.InputTop = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.InputWidth)]
        public float InputWidth { get => _cb.InputWidth; set { _cb.InputWidth = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.InputHeight)]
        public float InputHeight { get => _cb.InputHeight; set { _cb.InputHeight = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.UvOriginX)]
        public float UvOriginX { get => _cb.UvOriginX; set { _cb.UvOriginX = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.UvOriginY)]
        public float UvOriginY { get => _cb.UvOriginY; set { _cb.UvOriginY = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.UvScaleX)]
        public float UvScaleX { get => _cb.UvScaleX; set { _cb.UvScaleX = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.UvScaleY)]
        public float UvScaleY { get => _cb.UvScaleY; set { _cb.UvScaleY = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.UseGrid)]
        public float UseGrid { get => _cb.UseGrid; set { _cb.UseGrid = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.FallbackR)]
        public float FallbackR { get => _cb.FallbackR; set { _cb.FallbackR = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.FallbackG)]
        public float FallbackG { get => _cb.FallbackG; set { _cb.FallbackG = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.FallbackB)]
        public float FallbackB { get => _cb.FallbackB; set { _cb.FallbackB = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, CellBase + 0)]
        public float C0R { get => _cb.C0R; set { _cb.C0R = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 1)]
        public float C0G { get => _cb.C0G; set { _cb.C0G = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 2)]
        public float C0B { get => _cb.C0B; set { _cb.C0B = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, CellBase + 3)]
        public float C1R { get => _cb.C1R; set { _cb.C1R = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 4)]
        public float C1G { get => _cb.C1G; set { _cb.C1G = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 5)]
        public float C1B { get => _cb.C1B; set { _cb.C1B = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, CellBase + 6)]
        public float C2R { get => _cb.C2R; set { _cb.C2R = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 7)]
        public float C2G { get => _cb.C2G; set { _cb.C2G = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 8)]
        public float C2B { get => _cb.C2B; set { _cb.C2B = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, CellBase + 9)]
        public float C3R { get => _cb.C3R; set { _cb.C3R = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 10)]
        public float C3G { get => _cb.C3G; set { _cb.C3G = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 11)]
        public float C3B { get => _cb.C3B; set { _cb.C3B = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, CellBase + 12)]
        public float C4R { get => _cb.C4R; set { _cb.C4R = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 13)]
        public float C4G { get => _cb.C4G; set { _cb.C4G = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 14)]
        public float C4B { get => _cb.C4B; set { _cb.C4B = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, CellBase + 15)]
        public float C5R { get => _cb.C5R; set { _cb.C5R = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 16)]
        public float C5G { get => _cb.C5G; set { _cb.C5G = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 17)]
        public float C5B { get => _cb.C5B; set { _cb.C5B = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, CellBase + 18)]
        public float C6R { get => _cb.C6R; set { _cb.C6R = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 19)]
        public float C6G { get => _cb.C6G; set { _cb.C6G = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 20)]
        public float C6B { get => _cb.C6B; set { _cb.C6B = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, CellBase + 21)]
        public float C7R { get => _cb.C7R; set { _cb.C7R = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 22)]
        public float C7G { get => _cb.C7G; set { _cb.C7G = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 23)]
        public float C7B { get => _cb.C7B; set { _cb.C7B = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, CellBase + 24)]
        public float C8R { get => _cb.C8R; set { _cb.C8R = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 25)]
        public float C8G { get => _cb.C8G; set { _cb.C8G = value; UpdateConstants(); } }
        [CustomEffectProperty(PropertyType.Float, CellBase + 26)]
        public float C8B { get => _cb.C8B; set { _cb.C8B = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Method)]
        public float Method { get => _cb.Method; set { _cb.Method = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ToneStrength)]
        public float ToneStrength { get => _cb.ToneStrength; set { _cb.ToneStrength = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.LumaMatch)]
        public float LumaMatch { get => _cb.LumaMatch; set { _cb.LumaMatch = value; UpdateConstants(); } }

        public EffectImpl() : base(ShaderResourceLoader.Get("BlendLightPS.cso")) { }

        protected override void UpdateConstants()
            => drawInformation?.SetPixelShaderConstantBuffer(_cb);

        /// <summary>
        /// 縁取りモードだけが縁幅ぶんオフセットサンプリングする。
        /// グラデーションモードは自分の画素しか読まないので最小限で足りる。
        /// </summary>
        private int Range => _cb.Mode >= 0.5f
            ? Math.Clamp((int)MathF.Ceiling(MathF.Abs(_cb.RimWidth)) + 1, 1, 8192)
            : 1;

        public override void MapInputRectsToOutputRect(
            RawRect[] inputRects, RawRect[] inputOpaqueSubRects,
            out RawRect outputRect, out RawRect outputOpaqueSubRect)
        {
            // ここで渡される矩形が画像全体。グラデーションと背景対応付けの基準になるのでシェーダーへ渡す。
            var i = inputRects[0];
            _cb.InputLeft = i.Left;
            _cb.InputTop = i.Top;
            _cb.InputWidth = MathF.Max(1f, i.Right - i.Left);
            _cb.InputHeight = MathF.Max(1f, i.Bottom - i.Top);
            UpdateConstants();

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

        // HLSL の cbuffer と型・順序・並びを厳密に一致させること（float 52個 = 208byte, 16byte境界OK）。
        [StructLayout(LayoutKind.Sequential)]
        private struct ConstantBuffer
        {
            public float LightDirX, LightDirY, Spread, Mode;
            public float RimWidth, Softness, Saturation, Gain;
            public float InputLeft, InputTop, InputWidth, InputHeight;
            public float UvOriginX, UvOriginY, UvScaleX, UvScaleY;
            public float UseGrid, FallbackR, FallbackG, FallbackB;
            public float C0R, C0G, C0B;
            public float C1R, C1G, C1B;
            public float C2R, C2G, C2B;
            public float C3R, C3G, C3B;
            public float C4R, C4G, C4B;
            public float C5R, C5G, C5B;
            public float C6R, C6G, C6B;
            public float C7R, C7G, C7B;
            public float C8R, C8G, C8B;
            public float Method, ToneStrength, LumaMatch;
            public float Pad0, Pad1;
        }
    }
}
