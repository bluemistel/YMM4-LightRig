// シーン連動シェーディング ピクセルシェーダー。
// シーン光源と反対側を暗くして立体感・逆光の陰を作る。出力は暗くした画像そのもの。
// mode=0「エッジシェード」: 光源と反対側の輪郭付近だけを暗くする（リムの逆）。
//   ぼかし量は "影(shade)の成分" をマルチタップ平均して境界を柔らかくする。
// mode=1「擬似ノーマル」: アルファ（シルエット）の勾配から擬似法線を作り Lambert で陰影を付ける。
//   フォルム(formScale) = アルファ勾配を取るリング半径 = 拾う形の大きさ（スケール）。
//   輝度ではなくアルファを使うため、服の柄・髪などの内部ディテールを拾わずシワが出ない。
//
// 画像そのものはぼかさず、shade スカラーだけを扱って中心画素の色に掛ける（絵柄はシャープなまま）。
// 入力・出力ともプリマルチプライドアルファ。オフセットサンプリングのため矩形を拡張すること。

Texture2D    InputTexture : register(t0);
SamplerState InputSampler : register(s0);

cbuffer Constants : register(b0)
{
    float lightDirX;  // 光源へ向かうスクリーン方向 X（正規化, Y下系）
    float lightDirY;  // 光源へ向かうスクリーン方向 Y
    float lightZ;     // 擬似法線モードでの光の正面成分（大きいほど回り込む）
    float width;      // エッジシェードの幅 (px)
    float strength;   // 影の濃さ (0..1)
    float shadeR;     // 影色
    float shadeG;
    float shadeB;
    float mode;       // 0=エッジ, 1=擬似ノーマル
    float wrap;       // 擬似ノーマルの回り込み（テルミネータを柔らかく）
    float softness;   // エッジの締まり
    float blur;       // エッジ専用: 影のぼかし量 (px)
    float formScale;  // 擬似ノーマル専用: アルファ勾配のリング半径 = フォルムの大きさ (px)
    float _pad0;
    float _pad1;
    float _pad2;
};

// 8 方向オフセット（単位円）。エッジのぼかし平均・アルファ勾配リングの両方で使う。
static const float2 kOffsets[8] = {
    float2( 1.0f,  0.0f), float2(-1.0f,  0.0f),
    float2( 0.0f,  1.0f), float2( 0.0f, -1.0f),
    float2( 0.707f,  0.707f), float2(-0.707f,  0.707f),
    float2( 0.707f, -0.707f), float2(-0.707f, -0.707f),
};

// エッジシェード: 光源と反対側（-dir 側）の縁を検出（1=最も暗い）。
float computeShadeEdge(float2 p, float2 texel, float2 dir)
{
    float aHere = InputTexture.Sample(InputSampler, p).a;
    float2 awayUv = p - dir * max(width, 0.0f) * texel;
    float aAway = InputTexture.Sample(InputSampler, awayUv).a;
    float s = saturate(aHere - aAway);
    return pow(s, 1.0f + softness * 3.0f);
}

// 擬似ノーマル: 半径 r のリングでアルファ勾配を取り、シルエットから擬似法線を作って Lambert で陰影化。
// アルファは平坦な内部（=1）では勾配 0 となり、輪郭付近だけ法線が外側へ傾く。
// r（フォルム）を大きくするほど大きな形を拾い、滑らかで広い陰影になる（シワは出ない）。
float computeShadeNormal(float2 p, float2 texel, float2 dir)
{
    float r = max(formScale, 1.0f);
    float2 gradA = float2(0.0f, 0.0f);
    [unroll]
    for (int i = 0; i < 8; i++)
    {
        float av = InputTexture.Sample(InputSampler, p + kOffsets[i] * r * texel).a;
        gradA += kOffsets[i] * av; // アルファが高い（内側）方向を指す
    }
    gradA *= (1.0f / 8.0f);

    // 面の外向き法線 = アルファ減少方向 = -gradA。内部（gradA≈0）では +Z（正面）になる。
    float2 outward = -gradA;
    float3 N = normalize(float3(outward * 4.0f, 1.0f));
    float3 L = normalize(float3(dir, max(lightZ, 0.05f)));
    float lambert = saturate((dot(N, L) + wrap) / (1.0f + wrap));
    return 1.0f - lambert;
}

float4 main(float4 pos : SV_POSITION,
            float4 posScene : SCENE_POSITION,
            float4 uv : TEXCOORD0) : SV_TARGET
{
    float4 src = InputTexture.Sample(InputSampler, uv.xy);
    float a = src.a;
    if (a <= 1e-5f)
        return src;

    float3 rgb = src.rgb / a;
    float2 dir = float2(lightDirX, lightDirY);
    float2 texel = uv.zw;

    float shade;
    if (mode < 0.5f)
    {
        // エッジ: shade 成分だけをリング平均でぼかす（中心は重め）
        shade = computeShadeEdge(uv.xy, texel, dir) * 2.0f;
        float wsum = 2.0f;
        if (blur > 0.01f)
        {
            [unroll]
            for (int i = 0; i < 8; i++)
            {
                shade += computeShadeEdge(uv.xy + kOffsets[i] * blur * texel, texel, dir);
                wsum += 1.0f;
            }
        }
        shade /= wsum;
    }
    else
    {
        // 擬似ノーマル: フォルムがアルファ勾配のスケールになる（shade 平均はしない）
        shade = computeShadeNormal(uv.xy, texel, dir);
    }

    float3 shadeCol = float3(shadeR, shadeG, shadeB);
    float3 outRgb = lerp(rgb, rgb * shadeCol, saturate(shade * strength));
    return float4(outRgb * a, a); // 再プリマルチプライ
}
