// シーン連動リライティング ピクセルシェーダー。
// アルファ（シルエット）から擬似法線を作り、光色でライティングし直す（2トーン + ハイライト）。
// 影側は影色、光側は光色で元画像を着色し、最も光に面した所へハイライトを足す。
// 法線はアルファ勾配ベースなので内部ディテール（服の柄・髪）を拾わずシワが出ない。
//
// 入力・出力ともプリマルチプライドアルファ。オフセットサンプリングのため矩形を拡張すること。
//
// 【この処理は乗算なので、光色が 1 以下だと明るくならない】
// relit = rgb * tone なので、tone（=光色）が最大 1 のままでは元画素以下にしかならず
// 「色が付くだけ」になる。C# 側の受光量で光色を 1 超へ持ち上げて初めて照らされた見た目になる。

Texture2D    InputTexture : register(t0);
SamplerState InputSampler : register(s0);

cbuffer Constants : register(b0)
{
    float lightDirX;   // 光源へ向かうスクリーン方向 X（正規化, Y下系）
    float lightDirY;   // 光源へ向かうスクリーン方向 Y
    float lightZ;      // 光の正面成分（大きいほど回り込む）
    float formScale;   // アルファ勾配のリング半径 = フォルムの大きさ (px)
    float wrap;        // 回り込み（テルミネータを柔らかく）
    float diffuse;     // 拡散の強さ
    float highlight;   // ハイライトの強さ
    float shininess;   // ハイライトの締まり
    float lightR;      // 光色
    float lightG;
    float lightB;
    float shadowR;     // 影色
    float shadowG;
    float shadowB;
    float intensity;   // 元画像 ↔ リライト結果 のミックス (0..1)
    float blur;        // lambert（陰影スカラー）のぼかし量 (px)。絵柄はぼかさない
};

static const float2 kOffsets[8] = {
    float2( 1.0f,  0.0f), float2(-1.0f,  0.0f),
    float2( 0.0f,  1.0f), float2( 0.0f, -1.0f),
    float2( 0.707f,  0.707f), float2(-0.707f,  0.707f),
    float2( 0.707f, -0.707f), float2(-0.707f, -0.707f),
};

// 指定位置での陰影スカラーを返す: x=lambert（拡散）, y=ndl（ハイライト用の生の内積）。
// アルファのリング勾配から擬似法線を作る（内部は +Z, 輪郭は外側へ傾く）。
float2 computeLighting(float2 p, float2 texel, float2 dir)
{
    float r = max(formScale, 1.0f);
    float2 gradA = float2(0.0f, 0.0f);
    [unroll]
    for (int i = 0; i < 8; i++)
    {
        float av = InputTexture.Sample(InputSampler, p + kOffsets[i] * r * texel).a;
        gradA += kOffsets[i] * av;
    }
    gradA *= (1.0f / 8.0f);

    float3 N = normalize(float3(-gradA * 4.0f, 1.0f));
    float3 L = normalize(float3(dir, max(lightZ, 0.05f)));
    float ndl = dot(N, L);
    float lambert = saturate((ndl + wrap) / (1.0f + wrap));
    return float2(lambert, ndl);
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

    // フォルムを大きくすると髪などの細部でリング勾配がエイリアシングし、
    // 光と影が縞状に織り重なって見えることがある。陰影スカラーだけをぼかして馴染ませる
    // （絵柄そのものはぼかさない）。中心は重め。
    float2 lit = computeLighting(uv.xy, texel, dir) * 2.0f;
    float wsum = 2.0f;
    if (blur > 0.01f)
    {
        [unroll]
        for (int i = 0; i < 8; i++)
        {
            lit += computeLighting(uv.xy + kOffsets[i] * blur * texel, texel, dir);
            wsum += 1.0f;
        }
    }
    lit /= wsum;

    float lambert = lit.x;
    float ndl = lit.y;

    float3 lightCol = float3(lightR, lightG, lightB);
    float3 shadowCol = float3(shadowR, shadowG, shadowB);

    // 2トーン: 影色 → 光色 を lambert で補間して元画像に掛ける
    float3 tone = lerp(shadowCol, lightCol, saturate(lambert * diffuse));
    float3 relit = rgb * tone;

    // ハイライト（最も光に面した法線で強く出す）
    float h = pow(saturate(ndl), max(shininess, 1.0f)) * highlight;
    relit += lightCol * h;

    float3 outRgb = lerp(rgb, relit, saturate(intensity));

    // 受光量を上げると tone が 1 を超えて relit も 1 を超えうる。
    // そのまま a を掛けると rgb > a となり、プリマルチプライドとして不正な値になる
    // （半透明の髪の縁などで合成が破綻する）。表示できる範囲へ丸めてから premultiply する。
    outRgb = saturate(outRgb);
    return float4(outRgb * a, a); // 再プリマルチプライ
}
