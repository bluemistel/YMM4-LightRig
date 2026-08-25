// シーン連動リムライト ピクセルシェーダー。
// シーン光源から算出した「光源方向（スクリーン向き, 定数）」に面した輪郭だけを着色する。
// 遠方のシーン光源を方向性ライトとして扱うため、光源方向はアイテム内で一定。
// 「現在アルファ − 光源方向へずらした位置のアルファ」で光の当たる縁を求める。
// 出力は色付きのリム（プリマルチプライド）。後段でぼかし→合成する。
// オフセットサンプリングするため C# 側で矩形を縁幅分拡張すること。

Texture2D    InputTexture : register(t0);
SamplerState InputSampler : register(s0);

cbuffer Constants : register(b0)
{
    float lightDirX;  // 光源へ向かうスクリーン方向 X（正規化, Y下系）
    float lightDirY;  // 光源へ向かうスクリーン方向 Y
    float rimWidth;   // 縁の幅 (px)
    float softness;   // 縁の締まり（0=くっきり, 1=柔らかい）
    float colorR;
    float colorG;
    float colorB;
    float _pad;
};

float4 main(float4 pos : SV_POSITION,
            float4 posScene : SCENE_POSITION,
            float4 uv : TEXCOORD0) : SV_TARGET
{
    float2 dir = float2(lightDirX, lightDirY);

    float aHere = InputTexture.Sample(InputSampler, uv.xy).a;
    float2 offUv = uv.xy + dir * max(rimWidth, 0.0f) * uv.zw;
    float aOff = InputTexture.Sample(InputSampler, offUv).a;

    // 光源側の縁で大きくなる（内側=不透明、光源方向の隣=透明）
    float rim = saturate(aHere - aOff);
    rim = pow(rim, 1.0f + softness * 3.0f);

    float3 col = float3(colorR, colorG, colorB);
    return float4(col * rim, rim); // プリマルチプライド
}
