// シーン連動 落とし影の「影レイヤー」ピクセルシェーダー。
// 立ち絵のシルエット（アルファ）を接地線へ投影して地面に落ちる影を作る。出力は影だけ（元画像は含まない）。
// Processor 側で GaussianBlur を通し、元画像の背面へ合成する。
//
// 【投影モデル】接地線から shadowLength px ぶんの帯が影の領域。
//   接地線からの距離 hOut を t = hOut / shadowLength (0..1) に正規化し、
//   立ち絵の高さ h = t * inputHeight の位置をサンプリングする（＝全身が帯に収まる）。
//   横へは hOut * lean だけずらす（光の反対側へ倒れる）。
//
//   【帯を伸ばす向き（flip）】
//   flip=+1 … 接地線から画面の「上」へ＝奥へ伸びる。光源が被写体より手前にある構図。
//   flip=-1 … 接地線から画面の「下」へ＝手前へ伸びる。逆光（光源が被写体より奥）の構図。
//   光源の2D位置からは奥行きが分からないため自動判別できない。利用者に選ばせる。
//   hOut は「接地線からの距離」なのでどちらでも正の値になり、
//   足元が t=0・頭が t=1 という対応も lean の向きもそのまま成立する。
//
//   影の広がりが shadowLength と lean だけで決まるので、
//   C# 側で必要な矩形を正確に計算できる（切れない・飛ばない）。
//   ＝「地面の無限平面へ投影」する方式は、頭部の影が画像高さ×傾きぶん遠くへ飛んで
//   帯が画面外まで伸びるため 2D の立ち絵には向かない。
//
// 【タイル分割対策】D2D は画像を複数タイルに分けて描画するため、uv.zw から画像サイズを
//   逆算してはいけない（タイルのサイズになる）。絶対位置は posScene（SCENE_POSITION）と
//   CPU から渡した入力矩形で求め、サンプリングは現在画素からの相対オフセットに直して uv.zw を掛ける。
//
// 出力はプリマルチプライドアルファ。

Texture2D    InputTexture : register(t0);
SamplerState InputSampler : register(s0);

cbuffer Constants : register(b0)
{
    float inputLeft;    // 入力画像のシーン座標での矩形
    float inputTop;
    float inputWidth;
    float inputHeight;
    float lean;         // 出力1pxの高さあたりの横ずれ量（光の反対側へ）
    float lengthRatio;  // 影の長さ ÷ 立ち絵の高さ
    float groundOffset; // 接地線の位置（アイテム下端からの px。+で下）
    float opacity;      // 影の濃さ (0..1)
    float shadowR;      // 影色
    float shadowG;
    float shadowB;
    float tipBlur;      // 影の先端でのぼかし半径 (px)。接地部は 0
    float flip;         // 帯を伸ばす向き。+1=奥（画面上）へ, -1=手前（画面下）へ
    float _pad0; float _pad1; float _pad2; // 16 float（64byte）に揃える
};

// 先端ぼかしのサンプル数。黄金角スパイラルで円板状に散らす。
// 8方向リングだと「不透明度の違う影が8つ並んだ」多重像に見えてしまうため、
// 面で均一に散らして本当のぼかしにする（PerspectiveShadow と同じ考え方）。
#define TIP_TAPS 16
#define GOLDEN_ANGLE 2.39996323f

// 指定したシーン座標のアルファを読む。入力画像の外なら 0 を返す。
//
// 【重要】範囲外を素通しでサンプリングしてはいけない。D2D はテクスチャ外を
// 端の画素で引き伸ばす（クランプ）ため、画像の縁の色が帯状に伸びて
// 「光源位置に関係なく決まった場所に出る影の断片」になる。
// srcUv の計算は現在画素からの相対オフセット（タイル安全）で行う。
float sampleAlphaAt(float2 scenePos, float2 curScene, float2 curUv, float2 texel)
{
    if (scenePos.x < inputLeft || scenePos.x >= inputLeft + inputWidth ||
        scenePos.y < inputTop  || scenePos.y >= inputTop + inputHeight)
        return 0.0f;

    float2 uv = curUv + (scenePos - curScene) * texel;
    return InputTexture.Sample(InputSampler, uv).a;
}

float4 main(float4 pos : SV_POSITION,
            float4 posScene : SCENE_POSITION,
            float4 uv : TEXCOORD0) : SV_TARGET
{
    // 接地線（画像下端＋オフセット）をシーン座標で求める
    float groundY = inputTop + inputHeight + groundOffset;
    float shadowLength = max(inputHeight * lengthRatio, 1.0f);

    // 接地線からの距離。flip で「上へ伸びる／下へ伸びる」を切り替える。
    // どちらの向きでも hOut は正になるので、この先の式は共通で済む。
    float hOut = (groundY - posScene.y) * flip;
    if (hOut < 0.0f || hOut > shadowLength)
        return float4(0, 0, 0, 0);              // 影の帯の外

    float t = saturate(hOut / shadowLength);    // 影の中での位置 (0=足元, 1=先端)
    float h = t * inputHeight;                  // 参照する立ち絵の高さ

    // 【重要】参照元の高さは「画像の下端」から測る（立ち絵が立っている位置）。
    // 接地線 groundY から測ると、接地位置をずらしたぶん参照窓ごと画像の外へずれてしまい、
    // はみ出した側の影の内容が失われる（＝接地位置を動かすと影の上/下が欠ける）。
    // 接地位置は出力側の帯だけを平行移動させる役割に限定する。
    float imageBottom = inputTop + inputHeight;
    float2 srcScene = float2(posScene.x - hOut * lean, imageBottom - h);

    // 接地部はシャープ、先端ほどソフト
    float radius = tipBlur * t;

    float mask;
    if (radius > 0.01f)
    {
        float acc = 0.0f;
        [unroll]
        for (int i = 0; i < TIP_TAPS; i++)
        {
            float fi = (float)i + 0.5f;
            float r = sqrt(fi / TIP_TAPS) * radius;   // 円板上で均一分布になる半径
            float ang = fi * GOLDEN_ANGLE;
            float2 o = float2(cos(ang), sin(ang)) * r;
            acc += sampleAlphaAt(srcScene + o, posScene.xy, uv.xy, uv.zw);
        }
        mask = acc / TIP_TAPS;
    }
    else
    {
        mask = sampleAlphaAt(srcScene, posScene.xy, uv.xy, uv.zw);
    }

    float a = saturate(mask * opacity);
    float3 col = float3(shadowR, shadowG, shadowB);
    return float4(col * a, a); // プリマルチプライド
}
