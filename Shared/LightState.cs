using System.ComponentModel.DataAnnotations;
using System.Numerics;

namespace LightRig.Shared;

/// <summary>
/// 光源の種類。既定（0）は Point で、到達距離 0（＝無限）と組み合わせると
/// 種類を導入する前とまったく同じ挙動になる（既存プロジェクトを壊さないための約束）。
/// </summary>
public enum LightSourceType
{
    [Display(Name = "点光源", Description = "位置から放射状に照らす。到達距離で減衰する（電球・松明・街灯）")]
    Point = 0,

    [Display(Name = "平行光(太陽)", Description = "位置に関係なく角度が一定。距離減衰しない（太陽・月）")]
    Directional = 1,

    [Display(Name = "スポット", Description = "位置から指定方向へ円錐状に照らす。円錐の外は当たらない")]
    Spot = 2,
}

/// <summary>
/// シーン光源ターゲットが毎フレーム発信し、消費エフェクト（リムライト等）が参照する光源状態。
/// 不変（readonly struct）とし、<see cref="LightSignalStore"/> に値として格納する。
///
/// 【ゆらぎ（flicker）について】
/// フレームごとの乱数値そのものは載せず、係数（量・速度・シード）だけを載せる。
/// 実際の明滅は消費側シェーダー／プロセッサが frame/fps を時刻として決定的に再計算する。
/// こうすることで、YMM4 が一時停止時に別 Usage で再描画してフォールバック値を拾った場合でも、
/// またプレビューとエクスポートの間でも、同じ frame では必ず同じ明るさになる（非決定性を排除）。
/// </summary>
public readonly struct LightState : IEquatable<LightState>
{
    /// <summary>
    /// 値等価。<see cref="FrameSignalStore{T}"/> が「同じフレームなのに値が変わった
    /// ＝光源が編集された」を判定して古い履歴を捨てるために使う。
    /// 既定の <c>ValueType.Equals</c> はリフレクションで遅いので明示する。
    /// </summary>
    public bool Equals(LightState o)
        => Type == o.Type && Position == o.Position && Angle == o.Angle
        && Range == o.Range && FalloffStart == o.FalloffStart
        && SpotAngle == o.SpotAngle && SpotSoftness == o.SpotSoftness
        && Height == o.Height && Color == o.Color && Intensity == o.Intensity
        && FlickerAmount == o.FlickerAmount && FlickerSpeed == o.FlickerSpeed
        && FlickerSeed == o.FlickerSeed && AmbientColor == o.AmbientColor;

    public override bool Equals(object? obj) => obj is LightState o && Equals(o);

    public override int GetHashCode()
        => HashCode.Combine(Position, Angle, Range, Height, Color, Intensity, (int)Type);

    /// <summary>
    /// 光源の種類。<see cref="LightSourceType.Directional"/> のときは <see cref="Position"/> は使われず、
    /// <see cref="Angle"/> がそのまま光の向きになる（アイテムの位置によらず一定）。
    /// </summary>
    public LightSourceType Type { get; init; }

    /// <summary>光源のワールド座標（画面座標系, px）。ターゲットアイテムの描画位置＋オフセット。</summary>
    public Vector2 Position { get; init; }

    /// <summary>
    /// 平行光の「光が来る向き」／スポットの照らす向き（度, 0=上から, 時計回り）。
    /// 点光源では未使用。
    /// </summary>
    public float Angle { get; init; }

    /// <summary>到達距離（px）。この距離で光が 0 になる。<b>0 以下は無限＝減衰なし</b>（既定）。</summary>
    public float Range { get; init; }

    /// <summary>
    /// 減衰の始まる位置（到達距離に対する割合 0..1）。
    /// ここまでは等倍で、そこから <see cref="Range"/> にかけて滑らかに 0 まで落ちる。
    /// 0=到達距離いっぱいをかけて緩やかに落ちる / 1に近い=縁で急に切れる。
    /// </summary>
    public float FalloffStart { get; init; }

    /// <summary>スポットの円錐の全角（度）。</summary>
    public float SpotAngle { get; init; }

    /// <summary>スポットの縁のぼけ具合（0=くっきり, 1=最大）。</summary>
    public float SpotSoftness { get; init; }

    /// <summary>擬似的な高さ（Z）。法線ベースの陰影計算で光の回り込み・仰角に使う。</summary>
    public float Height { get; init; }

    /// <summary>光色（リニアではなく sRGB の 0..1、A は将来用）。</summary>
    public Vector4 Color { get; init; }

    /// <summary>光の強度（1.0 = 等倍）。負値は想定しない。</summary>
    public float Intensity { get; init; }

    /// <summary>明滅の振幅（0=なし, 例 0.2 で ±20%）。</summary>
    public float FlickerAmount { get; init; }

    /// <summary>明滅の速さ（Hz 目安）。</summary>
    public float FlickerSpeed { get; init; }

    /// <summary>明滅の位相シード。光源ごとに揺らぎ位相をずらすために使う。</summary>
    public float FlickerSeed { get; init; }

    /// <summary>
    /// 環境光の色（背景の平均色など。Phase 2 の環境光サンプラーが上書き発信する予約フィールド）。
    /// Phase 1 では未使用（既定 0）。
    /// </summary>
    public Vector4 AmbientColor { get; init; }
}
