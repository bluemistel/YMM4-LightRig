// 受光（光源連動）ピクセルシェーダー。
// アルファ（シルエット）から擬似法線を作り、光の当たる側へ光源色の「光レイヤー」を生成する。
// 出力は光レイヤーのみ（下地は含まない）。後段でぼかし → 加算/スクリーン等で重ねる。
//
// 【なぜ乗算をやめたか】
// 前身のリライティングは relit = rgb * tone という乗算だったため、
// 光色が最大 1 である以上 結果が必ず元画素以下になり「色が付いて暗くなる」だけだった。
// 光を"足す"表現は、リムライト・逆光と同じく別レイヤー化して加算系で重ねるのが正しい
// （CLAUDE.md「ぼかし量の設計方針(A)」）。
//
// 出力はプリマルチプライドアルファ。オフセットサンプリングのため矩形を拡張すること。

Texture2D    InputTexture : register(t0);
SamplerState InputSampler : register(s0);

cbuffer Constants : register(b0)
{
    float lightDirX;   // 光源へ向かうスクリーン方向 X（正規化, Y下系）
    float lightDirY;   // 光源へ向かうスクリーン方向 Y
    float lightZ;      // 光の正面成分（大きいほど回り込む）
    float formScale;   // アルファ勾配のリング半径 = フォルムの大きさ (px)

    float wrap;        // 回り込み（テルミネータを柔らかく）
    float diffuse;     // 拡散の広がり
    float highlight;   // ハイライトの強さ
    float shininess;   // ハイライトの締まり

    float lightR;      // 光色（C# 側で受光量を掛け済み）
    float lightG;
    float lightB;
    float blur;        // 陰影スカラーのぼかし量 (px)。絵柄はぼかさない
};

static const float2 kOffsets[8] = {
    float2( 1.0f,  0.0f), float2(-1.0f,  0.0f),
    float2( 0.0f,  1.0f), float2( 0.0f, -1.0f),
    float2( 0.707f,  0.707f), float2(-0.707f,  0.707f),
    float2( 0.707f, -0.707f), float2(-0.707f, -0.707f),
};

// 指定位置での陰影スカラーを返す: x=lambert（拡散）, y=ndl（ハイライト用の生の内積）。
// アルファのリング勾配から擬似法線を作る（内部は +Z, 輪郭は外側へ傾く）。
// 輝度勾配だと服の柄や髪をシワとして拾うので、必ずアルファから作ること。
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
    float a = InputTexture.Sample(InputSampler, uv.xy).a;
    if (a <= 1e-5f)
        return float4(0.0f, 0.0f, 0.0f, 0.0f);

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

    // 光の当たり具合＝拡散 + ハイライト。シルエットの外へは出さないので a を掛ける。
    float amount = saturate(lit.x * diffuse);
    float spec = pow(saturate(lit.y), max(shininess, 1.0f)) * highlight;
    float mask = saturate(amount + spec) * a;
    if (mask <= 0.0f)
        return float4(0.0f, 0.0f, 0.0f, 0.0f);

    // 受光量で 1 を超えた色はここで丸める。プリマルチプライドは rgb <= a が前提で、
    // 超えると半透明の髪の縁などで合成が破綻するため。
    float3 col = min(float3(lightR, lightG, lightB), float3(1.0f, 1.0f, 1.0f));

    return float4(col * mask, mask); // プリマルチプライド
}
