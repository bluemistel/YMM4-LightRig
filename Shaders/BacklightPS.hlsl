// 逆光（Backlight）の「本体」ピクセルシェーダー。
// 被写体を減光・脱色してシルエット寄りにするだけの単純な per-pixel 処理。
//
// リムは別レイヤー（SceneRimLightPS）で生成し、GaussianBlur を通してから合成モードで重ねる。
// 1シェーダーに焼き込むとリムをぼかせず「上から塗った」硬い見た目になるため、必ず分離すること。
//
// 入力・出力ともプリマルチプライドアルファ。オフセットサンプリングしないので矩形拡張は不要。

Texture2D    InputTexture : register(t0);
SamplerState InputSampler : register(s0);

cbuffer Constants : register(b0)
{
    float dim;    // 全体減光 (0..1)
    float desat;  // 彩度低下 (0..1)
    float _pad0;
    float _pad1;
};

float luminance(float3 c) { return dot(c, float3(0.299f, 0.587f, 0.114f)); }

float4 main(float4 pos : SV_POSITION,
            float4 posScene : SCENE_POSITION,
            float4 uv : TEXCOORD0) : SV_TARGET
{
    float4 src = InputTexture.Sample(InputSampler, uv.xy);
    float a = src.a;
    if (a <= 1e-5f)
        return src;

    float3 rgb = src.rgb / a;

    float lum = luminance(rgb);
    float3 body = lerp(rgb, lum.xxx, saturate(desat));
    body *= (1.0f - saturate(dim));

    return float4(body * a, a); // 再プリマルチプライ
}
