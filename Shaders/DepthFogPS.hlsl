// 空気遠近（デプスフォグ）ピクセルシェーダー。
// カメラからの距離に応じてアイテム全体をフォグ色へ寄せ、遠くのレイヤーほど霞ませる。
//
// 距離→濃度の計算は C# 側（DrawDescription.Camera / Draw から算出）で済ませ、
// ここでは density 1つを受け取って色を混ぜるだけ。アイテム単位で濃度が決まるので
// 画面内で濃度が変わることはなく、per-pixel の距離推定は不要。
//
// 遠景ほどコントラストと彩度が落ちる実際の空気遠近に合わせ、
// フォグ色へ寄せると同時にわずかに彩度も落とす。
//
// 【模様について】YMM4 の「図形の模様」を入力として受け取る実装も試したが、
// 映像エフェクトのプロパティ欄では図形の種類コンボが描画されず実用にならなかったため単色に戻した。
// 詳細は CLAUDE.md を参照。
//
// 入力・出力ともプリマルチプライドアルファ。オフセットサンプリングしないので矩形拡張は不要。

Texture2D    InputTexture : register(t0);
SamplerState InputSampler : register(s0);

cbuffer Constants : register(b0)
{
    float fogR;      // フォグ色
    float fogG;
    float fogB;
    float density;   // 濃度 (0..1)
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

    float d = saturate(density);
    float3 rgb = src.rgb / a;

    // 彩度をわずかに落としてから（霞むと色味が失われる）フォグ色へ寄せる
    float lum = luminance(rgb);
    rgb = lerp(rgb, lum.xxx, d * 0.3f);
    rgb = lerp(rgb, float3(fogR, fogG, fogB), d);

    return float4(rgb * a, a); // 再プリマルチプライ
}
