// シーン連動リムライト ピクセルシェーダー。
// シーン光源から算出した「光源方向（スクリーン向き, 定数）」に面した輪郭だけを着色する。
// 光源方向はアイテム内で一定（複数光源は C# 側で合成済み）。
// 出力は色付きのリム（プリマルチプライド）。後段でぼかし→合成する。
// オフセットサンプリングするため C# 側で矩形を縁幅分拡張すること。
//
// 縁の作り方は2通り（mode）:
//   0=アルファ差分 … 従来方式。1タップの差分なので幅と硬さが分離できない
//   1=ぼかしシルエット … 入力のガウスぼかしを register(t1) で受け取り、
//     「ぼかしたアルファを光源方向へずらして読んだ不足分」を縁にする。
//     帯の柔らかさ（ガウスσ）と幅（縁幅）が独立して効くので調整しやすい。
//     参考: https://github.com/routersys/YMM4-AutoBlendLight

Texture2D    InputTexture : register(t0);
SamplerState InputSampler : register(s0);

// 入力をガウスぼかししたもの（シルエットの「にじみ」）。C# 側で GaussianBlur を繋いでいる。
// 8タップのリング平均と違い真のガウスなので、半径を大きくしてもエイリアシングしない。
Texture2D    DiffusedTexture : register(t1);
SamplerState DiffusedSampler : register(s1);

cbuffer Constants : register(b0)
{
    float lightDirX;  // 光源へ向かうスクリーン方向 X（正規化, Y下系）
    float lightDirY;  // 光源へ向かうスクリーン方向 Y
    float rimWidth;   // 縁の幅 (px)
    float softness;   // 縁の締まり（0=くっきり, 1=柔らかい）
    float colorR;
    float colorG;
    float colorB;
    float mode;       // 0=アルファ差分, 1=ぼかしシルエット
};

float4 main(float4 pos : SV_POSITION,
            float4 posScene : SCENE_POSITION,
            float4 uv : TEXCOORD0,
            float4 uvDiffused : TEXCOORD1) : SV_TARGET
{
    float2 dir = float2(lightDirX, lightDirY);
    float aHere = InputTexture.Sample(InputSampler, uv.xy).a;

    float rim;
    if (mode < 0.5f)
    {
        // アルファ差分（従来）: 光源側の縁で大きくなる（内側=不透明、光源方向の隣=透明）
        float2 offUv = uv.xy + dir * max(rimWidth, 0.0f) * uv.zw;
        float aOff = InputTexture.Sample(InputSampler, offUv).a;
        rim = saturate(aHere - aOff);
    }
    else
    {
        // ぼかしシルエット: ぼかしたアルファを光源方向へずらして読み、その不足分を縁とする。
        // シルエットの外へは出さないよう元のアルファを掛ける。
        float2 offUv = uvDiffused.xy + dir * max(rimWidth, 0.0f) * uvDiffused.zw;
        float aDiffused = DiffusedTexture.Sample(DiffusedSampler, offUv).a;
        rim = saturate(1.0f - aDiffused) * aHere;
    }

    rim = pow(rim, 1.0f + softness * 3.0f);

    float3 col = float3(colorR, colorG, colorB);
    return float4(col * rim, rim); // プリマルチプライド
}
