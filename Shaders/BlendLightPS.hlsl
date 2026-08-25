// 背景なじませ（AutoBlend）ピクセルシェーダー。
// 「環境光サンプラー」が発信した背景の色（3x3 グリッド or 代表色1つ）を、
// シーン光源の方向に沿ったグラデーション、または光源側の輪郭（縁取り）として
// 被写体へ乗せる光レイヤーを生成する。後段でぼかし → 合成モードで重ねる。
//
// 出力はプリマルチプライドの光レイヤー（下地は含まない）。
//
// 【タイル分割の注意】
// 絶対位置が必要なので 1/uv.zw から画像サイズを逆算してはいけない（タイルのサイズになる）。
// posScene（SCENE_POSITION）と、CPU から渡した入力矩形 inputLeft/Top/Width/Height を使う。
// オフセットサンプリング（縁取りモード）は現在画素からの相対オフセットで行う。

Texture2D    InputTexture : register(t0);
SamplerState InputSampler : register(s0);

cbuffer Constants : register(b0)
{
    float lightDirX;   // 光源へ向かうスクリーン方向 X（正規化, Y下系）
    float lightDirY;   // 光源へ向かうスクリーン方向 Y
    float spread;      // グラデーションの広がり 0..1（1=被写体全体へ回り込む）
    float mode;        // 0=グラデーション, 1=縁取り, 2=全体

    float rimWidth;    // 縁取りモードの縁幅 (px)
    float softness;    // 縁の締まり 0..1
    float saturation;  // 背景色の彩度倍率（0=モノクロ, 1=そのまま）
    float gain;        // 背景色の明るさ倍率

    float inputLeft;   // 入力画像のシーン矩形（MapInputRectsToOutputRect で受け取った画像全体）
    float inputTop;
    float inputWidth;
    float inputHeight;

    float uvOriginX;   // 被写体の中心が背景グリッドのどの UV に当たるか
    float uvOriginY;
    float uvScaleX;    // 被写体ローカル 1px あたりの背景グリッド UV の増分
    float uvScaleY;

    float useGrid;     // 0=代表色のみ, 1=3x3 グリッドを位置補間して使う
    float fallbackR;   // グリッドが無いときに使う色（代表色 or 固定色）
    float fallbackG;
    float fallbackB;

    // 背景の 3x3 セル平均色（row-major）。cbuffer 配列を避け、C# 側と1:1のスカラーで持つ。
    float c0r; float c0g; float c0b;
    float c1r; float c1g; float c1b;
    float c2r; float c2g; float c2b;
    float c3r; float c3g; float c3b;
    float c4r; float c4g; float c4b;
    float c5r; float c5g; float c5b;
    float c6r; float c6g; float c6b;
    float c7r; float c7g; float c7b;
    float c8r; float c8g; float c8b;

    float method;        // 0=光を重ねる（光レイヤーを出力）, 1=色調同化（最終色を出力）
    float toneStrength;  // 色味の同化量 0..1（背景の色味を乗算で移す）
    float lumaMatch;     // 明るさ合わせ 0..1（背景の輝度へ寄せる）
    float _pad0; float _pad1;
};

static const float3 LUMA = float3(0.299f, 0.587f, 0.114f);

/// 3x3 グリッドをバイリニア補間して背景色を得る。
/// セル中心を uv = 0, 0.5, 1 に置く（端をクランプするだけで矩形外も破綻しない）。
float3 sampleGrid(float2 uv)
{
    float3 cells[9];
    cells[0] = float3(c0r, c0g, c0b);
    cells[1] = float3(c1r, c1g, c1b);
    cells[2] = float3(c2r, c2g, c2b);
    cells[3] = float3(c3r, c3g, c3b);
    cells[4] = float3(c4r, c4g, c4b);
    cells[5] = float3(c5r, c5g, c5b);
    cells[6] = float3(c6r, c6g, c6b);
    cells[7] = float3(c7r, c7g, c7b);
    cells[8] = float3(c8r, c8g, c8b);

    float2 g = saturate(uv) * 2.0f;              // 0..2
    int2 i0 = clamp((int2)floor(g), 0, 2);
    int2 i1 = min(i0 + 1, 2);
    float2 f = saturate(g - (float2)i0);

    float3 c00 = cells[i0.y * 3 + i0.x];
    float3 c10 = cells[i0.y * 3 + i1.x];
    float3 c01 = cells[i1.y * 3 + i0.x];
    float3 c11 = cells[i1.y * 3 + i1.x];

    return lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
}

float4 main(float4 pos : SV_POSITION,
            float4 posScene : SCENE_POSITION,
            float4 uv : TEXCOORD0) : SV_TARGET
{
    float4 src = InputTexture.Sample(InputSampler, uv.xy);
    float aHere = src.a;

    float2 dir = float2(lightDirX, lightDirY);
    float2 center = float2(inputLeft + inputWidth * 0.5f, inputTop + inputHeight * 0.5f);
    float2 rel = posScene.xy - center;   // 被写体中心からのローカルオフセット (px)

    float mask;
    if (mode < 0.5f)
    {
        // グラデーション: 光源方向に沿った位置を 0..1 に正規化して、光源側ほど強くする。
        // 被写体の外接矩形で割ることで、素材の大きさによらず同じ見た目になる。
        float2 half2 = float2(max(inputWidth, 1.0f), max(inputHeight, 1.0f)) * 0.5f;
        float proj = dot(rel / half2, dir);      // 概ね -1..1
        float t = saturate(0.5f + 0.5f * proj);
        float s = max(spread, 1e-3f);
        mask = smoothstep(1.0f - s, 1.0f, t) * aHere;
    }
    else if (mode < 1.5f)
    {
        // 縁取り: 光源方向へずらした位置とのアルファ差分＝光源側の輪郭。
        // 相対オフセットに uv.zw を掛けるのでタイル分割に安全。
        float2 offUv = uv.xy + dir * max(rimWidth, 0.0f) * uv.zw;
        float aOff = InputTexture.Sample(InputSampler, offUv).a;
        float rim = saturate(aHere - aOff);
        mask = pow(rim, 1.0f + softness * 3.0f);
    }
    else
    {
        // 全体: 光源の向きを使わず、シルエット全体へ均一に背景色を乗せる。
        // 光源を置かずに「背景へ馴染ませる」だけを行いたいケース向け。
        mask = aHere;
    }

    // 色調同化は最終色を出力するので、マスクが 0 でも元画素を通す必要がある
    if (mask <= 0.0f && method < 0.5f)
        return float4(0.0f, 0.0f, 0.0f, 0.0f);

    float3 bg;
    if (useGrid > 0.5f)
    {
        float2 uvBg = float2(uvOriginX, uvOriginY) + rel * float2(uvScaleX, uvScaleY);
        bg = sampleGrid(uvBg);
    }
    else
    {
        bg = float3(fallbackR, fallbackG, fallbackB);
    }

    float lum = dot(bg, LUMA);
    bg = max(lerp(float3(lum, lum, lum), bg, saturation) * gain, 0.0f);

    // --- 方式1: 光レイヤーを出力し、後段のぼかし＋合成モードで重ねる ---
    if (method < 0.5f)
        return float4(bg * mask, mask); // プリマルチプライド

    // --- 方式2: 色調同化。背景の「色味」を乗算で移し、「明るさ」は別枠で寄せる ---
    // 乗算だけだと暗くなる一方なので手動で持ち上げる必要があった。
    // 背景色を輝度1に正規化して色味だけ取り出せば、色の調整が明るさに影響しない
    // （M9 の「色と明るさは必ず分離する」と同じ方針）。
    if (aHere <= 1e-4f)
        return float4(0.0f, 0.0f, 0.0f, 0.0f);

    float3 srcRgb = src.rgb / aHere;     // プリマルチプライドを解除

    float bgLum = max(dot(bg, LUMA), 1e-4f);
    float3 bgTone = bg / bgLum;          // 輝度をならした色味だけの成分

    float3 col = srcRgb * lerp(1.0f.xxx, bgTone, saturate(toneStrength) * mask);

    // 明るさを背景へ寄せる。比で合わせるので負にならず、暗部の階調も潰れにくい。
    // 極端な明暗差で破綻しないよう倍率はクランプする。
    float curLum = max(dot(col, LUMA), 1e-4f);
    float ratio = clamp(bgLum / curLum, 0.25f, 4.0f);
    col *= lerp(1.0f, ratio, saturate(lumaMatch) * mask);

    col = max(col, 0.0f);
    return float4(col * aHere, aHere); // プリマルチプライド
}
