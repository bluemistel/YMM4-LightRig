using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using LightRig.Effects;

namespace LightRig.Effects.SceneShadow;

/// <summary>
/// 落とし影の「影レイヤー」用 Direct2D カスタムシェーダーエフェクト（影だけを出力）。
/// 投影で大きく外側へ広がるため、矩形を横は shear×画像高さ、上下は帯の伸びる向きに応じて拡張する。
/// </summary>
public sealed class SceneShadowCustomEffect : D2D1CustomShaderEffectBase
{
    private enum PropertyIndex
    {
        InputLeft = 0,
        InputTop,
        InputWidth,
        InputHeight,
        Lean,
        LengthRatio,
        GroundOffset,
        Opacity,
        ShadowR,
        ShadowG,
        ShadowB,
        TipBlur,
        Flip,
    }

    public float Lean        { set => SetValue((int)PropertyIndex.Lean, value); }
    public float LengthRatio { set => SetValue((int)PropertyIndex.LengthRatio, value); }
    public float GroundOffset { set => SetValue((int)PropertyIndex.GroundOffset, value); }
    public float Opacity      { set => SetValue((int)PropertyIndex.Opacity, value); }
    public float ShadowR      { set => SetValue((int)PropertyIndex.ShadowR, value); }
    public float ShadowG      { set => SetValue((int)PropertyIndex.ShadowG, value); }
    public float ShadowB      { set => SetValue((int)PropertyIndex.ShadowB, value); }
    public float TipBlur      { set => SetValue((int)PropertyIndex.TipBlur, value); }
    /// <summary>帯を伸ばす向き。+1=奥（画面上）へ, -1=手前（画面下）へ。</summary>
    public float Flip         { set => SetValue((int)PropertyIndex.Flip, value); }

    public SceneShadowCustomEffect(IGraphicsDevicesAndContext devices)
        : base(Create<EffectImpl>(devices)) { }

    [CustomEffect(1)]
    private sealed class EffectImpl : D2D1CustomShaderEffectImplBase<EffectImpl>
    {
        private ConstantBuffer _cb;

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Lean)]
        public float Lean { get => _cb.Lean; set { _cb.Lean = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.LengthRatio)]
        public float LengthRatio { get => _cb.LengthRatio; set { _cb.LengthRatio = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.GroundOffset)]
        public float GroundOffset { get => _cb.GroundOffset; set { _cb.GroundOffset = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Opacity)]
        public float Opacity { get => _cb.Opacity; set { _cb.Opacity = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ShadowR)]
        public float ShadowR { get => _cb.ShadowR; set { _cb.ShadowR = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ShadowG)]
        public float ShadowG { get => _cb.ShadowG; set { _cb.ShadowG = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.ShadowB)]
        public float ShadowB { get => _cb.ShadowB; set { _cb.ShadowB = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.TipBlur)]
        public float TipBlur { get => _cb.TipBlur; set { _cb.TipBlur = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.Flip)]
        public float Flip { get => _cb.Flip; set { _cb.Flip = value; UpdateConstants(); } }

        // 入力矩形は MapInputRectsToOutputRect で受け取る（D2D はここに画像全体の矩形を渡す）。
        // タイルごとに呼ばれる MapOutputRectToInputRects で更新してはいけない（接地線がタイル単位になる）。
        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.InputLeft)]
        public float InputLeft { get => _cb.InputLeft; set { _cb.InputLeft = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.InputTop)]
        public float InputTop { get => _cb.InputTop; set { _cb.InputTop = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.InputWidth)]
        public float InputWidth { get => _cb.InputWidth; set { _cb.InputWidth = value; UpdateConstants(); } }

        [CustomEffectProperty(PropertyType.Float, (int)PropertyIndex.InputHeight)]
        public float InputHeight { get => _cb.InputHeight; set { _cb.InputHeight = value; UpdateConstants(); } }

        // Flip は 0 だとシェーダー側で hOut が常に 0 になり帯が画面全体へ広がるため、
        // Processor が値を入れる前でも破綻しないよう既定を +1（＝奥へ）にしておく。
        public EffectImpl() : base(ShaderResourceLoader.Get("SceneShadowPS.cso"))
            => _cb.Flip = 1f;

        protected override void UpdateConstants()
            => drawInformation?.SetPixelShaderConstantBuffer(_cb);

        /// <summary>
        /// 影が届く範囲だけ矩形を広げる。影の帯は接地線から（Flip の向きへ）shadowLength、
        /// 横へ最大 shadowLength×|lean| しか伸びないので、必要量を正確に出せる。
        /// 極端な値で矩形が爆発しないよう上限を設ける。
        /// </summary>
        private RawRect Expand(RawRect r)
        {
            int imageHeight = Math.Max(r.Bottom - r.Top, 1);
            float shadowLength = MathF.Max(imageHeight * _cb.LengthRatio, 1f);

            // 横は影の傾きぶん＋先端ぼかし
            int ex = (int)MathF.Ceiling(shadowLength * MathF.Abs(_cb.Lean) + MathF.Abs(_cb.TipBlur)) + 2;
            ex = Math.Clamp(ex, 0, 8192);

            // 影の帯が矩形上端から見てどこを占めるかを、伸ばす向きも含めて求める。
            // 接地位置は帯ごと平行移動させるので、符号込みで計算しないと
            // ずらしたときに拡張が足りず影が見切れる。
            float groundYRel = imageHeight + _cb.GroundOffset;
            float bandTop, bandBottom;
            if (_cb.Flip >= 0f)
            {
                // 奥へ（画面上へ）伸びる
                bandTop = groundYRel - shadowLength;
                bandBottom = groundYRel;
            }
            else
            {
                // 手前へ（画面下へ）伸びる。拡張する側が上下逆になる。
                bandTop = groundYRel;
                bandBottom = groundYRel + shadowLength;
            }

            int above = (int)MathF.Ceiling(MathF.Max(-bandTop, 0f)) + 2;
            above = Math.Clamp(above, 0, 8192);

            int below = (int)MathF.Ceiling(MathF.Max(bandBottom - imageHeight, 0f)) + 2;
            below = Math.Clamp(below, 0, 8192);

            return new RawRect(r.Left - ex, r.Top - above, r.Right + ex, r.Bottom + below);
        }

        public override void MapInputRectsToOutputRect(
            RawRect[] inputRects, RawRect[] inputOpaqueSubRects,
            out RawRect outputRect, out RawRect outputOpaqueSubRect)
        {
            // ここで渡される矩形が画像全体。接地線の基準になるのでシェーダーへ渡す。
            var i = inputRects[0];
            _cb.InputLeft = i.Left;
            _cb.InputTop = i.Top;
            _cb.InputWidth = MathF.Max(1f, i.Right - i.Left);
            _cb.InputHeight = MathF.Max(1f, i.Bottom - i.Top);
            UpdateConstants();

            outputRect = Expand(i);
            outputOpaqueSubRect = default;
        }

        /// <summary>
        /// 【重要】影は出力画素より上（影の長さが立ち絵より短いときは最大で画像高さぶん）を参照する。
        /// 出力矩形から必要な入力範囲を機械的に広げても足りず、過小申告するとタイルに渡される入力が
        /// 不足して、離れた場所に影の断片が出たりブロック状に割れたりする。
        /// MapInputRectsToOutputRect で控えておいた画像全体を常に要求するのが確実。
        /// </summary>
        public override void MapOutputRectToInputRects(RawRect outputRect, RawRect[] inputRects)
        {
            if (_cb.InputWidth > 1f && _cb.InputHeight > 1f)
            {
                inputRects[0] = new RawRect(
                    (int)MathF.Floor(_cb.InputLeft),
                    (int)MathF.Floor(_cb.InputTop),
                    (int)MathF.Ceiling(_cb.InputLeft + _cb.InputWidth),
                    (int)MathF.Ceiling(_cb.InputTop + _cb.InputHeight));
                return;
            }
            inputRects[0] = Expand(outputRect);
        }

        // HLSL の cbuffer と型・順序・並びを厳密に一致させること。
        // float 16 個 = 64byte で 16byte 境界に揃える（_pad は HLSL 側にも同じ数だけ置くこと）。
        [StructLayout(LayoutKind.Sequential)]
        private struct ConstantBuffer
        {
            public float InputLeft;
            public float InputTop;
            public float InputWidth;
            public float InputHeight;
            public float Lean;
            public float LengthRatio;
            public float GroundOffset;
            public float Opacity;
            public float ShadowR;
            public float ShadowG;
            public float ShadowB;
            public float TipBlur;
            public float Flip;
            public float Pad0;
            public float Pad1;
            public float Pad2;
        }
    }
}
