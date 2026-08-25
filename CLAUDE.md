# LightRig（シーンライティング）YMM4 プラグイン — 開発ガイド

このリポジトリは YMM4（YukkuriMovieMaker4）向けの映像エフェクトプラグイン **LightRig** を開発する。
2D の立ち絵＋背景動画において、シーンに1つ置いた「シーン光源」を各立ち絵のライティング
エフェクトが**同じチャンネルで自動参照**し、リムライト・シェーディング・逆光・リライティングの
統一と調整コスト削減を実現する。

対象: .NET 10 / Windows / x64。YMM4 プラグイン。開発 YMM4 は `C:\tools\YukkuriMovieMaker_Dev\`。

---

## ビルド

```
dotnet build -c Release -p:Platform=x64
```

配布パッケージ（.ymme）まで作る場合:

```
dotnet build -c Release -p:Platform=x64 -p:PackYmme=true
```

- **fxc.exe が必須**（Windows SDK 付属）。`Directory.Build.props` が Windows Kits から自動探索する。
- ビルド成功で DLL は自動的に `C:\tools\YukkuriMovieMaker_Dev\user\plugin\LightRig\` へコピーされる（`CopyToYmm4PluginDir`）。
- `.ymme` は `publish\LightRig.v.<Version>.ymme` に出力される。
- 便利なトグル: `-p:SkipHlsl=true`（シェーダーコンパイル省略）、`-p:SkipPluginCopy=true`（コピー省略）。
- **x64 のみサポート**（Release/x64 前提）。
- **YMM4 起動中は plugin フォルダへのコピーが DLL ロックで失敗する**（MSB3021/MSB3027, "used by another process"）。
  コンパイル自体は成功しているので、動作確認だけしたいときは `-p:SkipPluginCopy=true` を付ける。
  差し替えて実機反映する場合は YMM4 を閉じてからビルドする。
- **ビルド前に起動確認する**のが定石:
  `tasklist | grep -i yukkuri` → 起動中なら `-p:SkipPluginCopy=true` を付ける／閉じていれば通常ビルド。

---

## 手本ファイル（別リポジトリ・必読）

新しいエフェクトを実装する際は、まず以下の実物を読むこと。パターンはすべてここから流用している。

| 目的 | 手本ファイル |
|---|---|
| csproj / ビルド基盤 | `C:\YMM4_PluginDevelop\EmoiEffect\EmoiEffect.csproj`, `Directory.Build.props` |
| **エフェクト3クラスパターン** | `C:\YMM4_PluginDevelop\EmoiEffect\Effects\RimLight\RimLightEffect.cs`（UI/モデル）, `RimLightCustomEffect.cs`（D2Dカスタムシェーダー）, `RimLightEffectProcessor.cs`（D2Dグラフ） |
| HLSL シェーダー | `C:\YMM4_PluginDevelop\EmoiEffect\Shaders\RimLightPS.hlsl` |
| cso ローダー | `C:\YMM4_PluginDevelop\EmoiEffect\Effects\ShaderResourceLoader.cs`（本repoに移植済み: `Effects\ShaderResourceLoader.cs`） |
| 単一シェーダー用 Processor 基底 | `C:\YMM4_PluginDevelop\EmoiEffect\Effects\ShaderVideoEffectProcessorBase.cs`（M2で必要なら移植） |
| **共有ストア＋ターゲット** | `C:\YMM4_PluginDevelop\2DCamera\TwoDCameraPlugin\AfSignalStore.cs`（Usageフォールバックのコメント必読）, `AfChannel.cs`, `AfTargetEffect.cs`, `AfTargetProcessor.cs` |
| 消費側の TryGet 使い方 | `C:\YMM4_PluginDevelop\2DCamera\TwoDCameraPlugin\TwoDCameraProcessor.cs`（`ResolveFocus`, 130行付近） |

---

## アーキテクチャ

### 共有ライト基盤 `Shared\`（実装済み・M1）
- `LightChannel.cs` — 発信側 `LightChannel`(Ch1..8) / 受信側 `LightChannelOrOff`(Off含む)。数値を一致させ `(LightChannel)値` でキャストする。
- `LightState.cs` — 光源状態の readonly struct（Position, Height, Color, Intensity, Flicker*, AmbientColor）。
- `LightSignalStore.cs` — `Publish` / `TryGet`。キー `(SceneId, Channel)` → Usage 別 `(Seq, LightState)`。
  同一 Usage 優先 → 最新 Seq フォールバック。

### エフェクト構成（1 DLL に独立収録、必要なものだけアイテムに貼る）
| エフェクト | フォルダ | 状態 | 役割 |
|---|---|---|---|
| シーン光源ターゲット | `Effects\LightTarget\` | ✅ M1 | パススルー。光源状態を Publish。種類(点/平行光/スポット)と到達距離・減衰(M10)。環境光サンプラーへ色/明るさを追従可(M9)。プレビューに操作点・到達範囲を表示 |
| リムライト(光源連動) | `Effects\SceneRimLight\` | ✅ M2 | 光源方向の輪郭を光らせる |
| シェーディング(光源連動) | `Effects\SceneShading\` | ✅ M2 | 光源の反対側を暗くする（エッジ=アルファ差分 / 擬似ノーマル=アルファ勾配。ぼかし量対応） |
| 逆光 | `Effects\Backlight\` | ✅ M3 | 減光+脱色の本体シェーダー＋リムを別レイヤー化しBlur/合成モードで重ねる多段グラフ |
| リライティング(光源連動) | `Effects\Relighting\` | ✅ M4 | アルファ擬似法線で2トーン+ハイライト。プリセット7種。環境光ミックス・ぼかし量対応 |
| 環境光サンプラー | `Effects\AmbientSampler\` | ✅ M5 | 背景を8x8へ縮小しGPU読み戻し→輝度しきい値以上の画素の平均色をPublish(Nフレーム間引き) |
| 落とし影(光源連動) | `Effects\SceneShadow\` | ✅ M6 | シルエットを接地線からの「帯」へ投影。別レイヤー→Blur→背面合成。長さ/傾き/接地位置を手動補正可 |
| 空気遠近(デプスフォグ) | `Effects\DepthFog\` | ✅ M7 | カメラ距離に応じて単色のフォグ色へ寄せる。環境光サンプラー連動可 |
| 背景なじませ(環境光連動) | `Effects\BlendLight\` | ✅ M8 | 背景の色をグラデーション／縁取り／全体として被写体へ乗せる。背景を3x3に区切った色グリッドを位置補間。**光源は任意**（全体モードは光源を一切参照しない） |

---

## 新エフェクト追加チェックリスト

映像を変える連動エフェクト（リムライト等）は **3クラス＋HLSL** で作る。手本は RimLight 一式。

1. **`XxxEffect.cs`**（`VideoEffectBase`）
   - `[VideoEffect("表示名", ["LightRig"], [検索タグ...], IsAviUtlSupported = false)]` と `[PluginDetails(AuthorName = "bluemistel")]`。
   - キーフレーム可のパラメータは `Animation` プロパティ＋`[AnimationSlider("F0","%",min,max)]`。
   - 非アニメーションは `Set(ref ...)` バッキング＋`[ColorPicker]`/`[EnumComboBox]`/`[TextBoxSlider]`。
   - **消費エフェクトには必ず `LightChannelOrOff Channel`（既定 Ch1）とローカル倍率パラメータを持たせる。**
   - `CreateVideoEffect` で Processor を返す。`CreateExoVideoFilters` は `[]`。`GetAnimatables()` に Animation を列挙。
2. **`XxxCustomEffect.cs`**（`D2D1CustomShaderEffectBase`, namespace `YukkuriMovieMaker.Player.Video`）
   - ネスト `[CustomEffect(1)] EffectImpl : D2D1CustomShaderEffectImplBase<EffectImpl>`。
   - `[StructLayout(Sequential)] ConstantBuffer` は **HLSL の cbuffer と型・順序・16バイト境界を一致**させる（float4 の途中に float を割り込ませない。パディングに注意）。
   - `[CustomEffectProperty(PropertyType.Float, index)]` は連番。setter で `UpdateConstants()`。
   - オフセットサンプリングする場合は `MapInputRectsToOutputRect` / `MapOutputRectToInputRects` で**矩形をリム幅ぶん拡張**する（拡張しないと縁が切れる）。
   - ctor は `base(ShaderResourceLoader.Get("XxxPS.cso"))`。
3. **`XxxEffectProcessor.cs`**（`VideoEffectProcessorBase`）
   - `CreateEffect` で D2D グラフを構築（custom → GaussianBlur → Composite/Blend → CrossFade 等）。`disposer.Collect`。
   - `Update(EffectDescription)` で `_item.Xxx.GetValue(frame, length, fps)` を取得し、**dirty フラグで変化時のみ**定数を更新。
   - `frame = desc.ItemPosition.Frame`, `length = desc.ItemDuration.Frame`, `fps = desc.FPS`。
   - **光源の参照**: `LightSignalStore.TryGet(desc.SceneId, desc.Usage, (LightChannel)item.Channel, out var light)`。
     `Channel == Off` は連動しない（ローカル値のみ）。信号無しは直前値ホールド or ローカル値へフォールバック。
4. **`Shaders\XxxPS.hlsl`** — `Shaders\` に置けば csproj が自動で fxc → cso → 埋め込み。
   - エントリ `main`、`cbuffer Constants : register(b0)`、`Texture2D InputTexture : register(t0)` / `SamplerState : register(s0)`。
   - 出力は**プリマルチプライドアルファ**（`float4(col * a, a)`）。

パススルー系（光源ターゲットや環境光サンプラー）は HLSL 不要で、`IVideoEffectProcessor` を直接実装し `Update` で Publish するだけ（手本: `AfTargetProcessor.cs` / 本repo `LightTargetProcessor.cs`）。

---

## 落とし穴

- **ConstantBuffer と cbuffer のレイアウトずれ**が最頻出バグ。順序・float 数を厳密に一致させる。ずれると値が別プロパティに化ける。
- **cso 埋め込み名**は `LightRig.Shaders.<...>.<Name>.cso`。`ShaderResourceLoader.Get("<Name>PS.cso")` はサフィックス一致で拾う。HLSL のファイル名と `Get` の引数を合わせる。
- **矩形拡張忘れ**でリム・影の縁が切れる。`MapInputRectsToOutputRect` で必ず広げる。
- **【タイル分割】`uv.zw`（テクセルサイズ）から画像サイズを逆算してはいけない。**
  D2D は画像を複数タイルに分けて描画するため `1/uv.zw` は「タイルのサイズ」になり、
  絵が格子状に分割された見た目になる（落とし影で実際に発生。接地線がタイルごとにできた）。
  - **絶対位置が要る処理**（接地線・光源位置・グラデーションの基準など）は
    `posScene`（`SCENE_POSITION` セマンティクス）と、CPU から渡した入力矩形
    （`inputLeft/Top/Width/Height`）で求める。手本: `EmoiEffect\Shaders\RimLightPS.hlsl`。
  - 入力矩形は **`MapInputRectsToOutputRect` で受け取る**（D2D はここに画像全体の矩形を渡す）。
    タイルごとに呼ばれる `MapOutputRectToInputRects` で更新すると同じバグに戻る。
  - **サンプリングは「現在画素からの相対オフセット」に直してから `uv.zw` を掛ける**
    （`srcUv = uv.xy + (srcScene - posScene.xy) * uv.zw`）。相対オフセットはタイル安全。
  - **`MapOutputRectToInputRects` で必要な入力範囲を過小申告してもタイル分割で割れる。**
    落とし影は出力画素より上（最大で画像高さぶん＝遠く離れた位置）を参照するため、
    タイルに渡される入力が足りず境界で切れた。**遠くを参照する処理は
    `MapInputRectsToOutputRect` で控えた画像全体を常に要求する**のが確実
    （手本の PerspectiveShadow も「光源が出力矩形内なら入力矩形全体を含める」保険を持つ）。

### YMM4「図形の模様」の再利用は**映像エフェクトでは不可**（試して撤退・2026-07）
模様（ノイズ/市松/グラデーション/画像等）を自前実装せず YMM4 の図形プラグインで賄えないか試したが、
**映像エフェクトのプロパティ欄に図形の種類コンボが描画されず実用にならなかった**ため単色に戻した。
呼び出し API 自体は下記のとおり公開されており動く（コンパイル・実行とも通る）。UI だけが出ない。

- `IShapeParameter.CreateShapeSource(devices)` → `IShapeSource`、`.Output` が `ID2D1Image`（`YukkuriMovieMaker.Plugin.Shape`）
- `shapeSource.Update(new TimelineItemSourceDescription(timeline, itemFrame, itemLength, layer))`
  `timeline` は `new TimelineSourceDescription(Size, ItemDuration, ItemPosition, FPS, Usage, SceneId, [])` で組める
  （`EffectDescription` に `TimelineSourceDescription` は生えていないので自前で組み立てる）
- `ShapeParameterBase` は**抽象**で、具体クラス（`CircleShapeParameter` 等）は SDK ではなく
  本体 `YukkuriMovieMaker.dll` 側。この仕組みは図形アイテム用で、エフェクトから使う想定になっていない。
- 2入力シェーダーが要る場合の書き方（この撤退とは別に有用）: `[CustomEffect(2)]`、
  HLSL に `Texture2D ... : register(t1)` と第4引数 `float4 uvPattern : TEXCOORD1`。
  **未接続の入力を残さないこと**（描画できない）。
- `VideoEffectProcessorBase.Dispose()` と `MapInvalidRect` は **virtual ではない**ので上書きできない。
  後片付けが要るものは生成時に `disposer.Collect(...)` して基底の破棄に任せる。
- **プリマルチプライドアルファ**前提。非プリマルで出すと合成が破綻する。
- Release / x64 のみ。AnyCPU デバッグでは YMM4 実機確認できない。

### 座標系の規約（実機で確認済み・2026-07）
- **`drawDesc.Draw` は Y+ が下方向**で、シェーダーの UV（`uv.zw`）と同じ向き。**Y 反転は不要**。
  当初「ワールドY上向き」と想定して反転していたが実機で逆と判明し、`LightMath.ScreenDirTowardLight` から反転を除去した。
- 向きの規約は `Shared\LightMath.cs` の1箇所に閉じているので、変わった場合はそこだけ直せば全エフェクトに反映される。

### プレビュー操作点（ControllerPoint）の正しい実装方法【重要】
プレビュー上のドラッグ可能な操作点は **`DrawDescription.Controllers`**（型 `ImmutableList<VideoEffectController>`）
に設定する。**`Update()` の戻り値で渡す**のがポイント。実装例: `Effects\LightTarget\LightTargetProcessor.cs`。

```csharp
return drawDesc with { Controllers = cachedControllers };
```

- **座標系**: `ControllerPoint` の `Position` は**アイテム中心からのオフセット**（Vector3）。
  `Animation` のオフセットパラメータと同じ座標系なので、ドラッグ差分をそのまま加算できる。
- **ドラッグ**: ctor 第2引数のコールバックで `arg.Delta.X/Y` を受け取り、
  **`item.OffsetX.AddToEachValues(arg.Delta.X)`** でパラメータへ書き戻す（`Animation` の全キーフレームに加算）。
- **コールバックを渡さない点はドラッグ不可**になるので、ガイド線の端点などの表示専用に使える。
- `VideoEffectController(item, [points])` の `Connection = VideoControllerPointConnection.Line` で点間に線を引ける。
- `ControllerPoint` の `Shape`(`VideoControllerPointShape.Circle` 等)、`IsSelected`、`OnDragStart`
  (`arg.ModifierKeys` でCtrl判定可)も設定できる。
- 操作点は**パラメータ変化時のみ再構築してキャッシュ**する（毎フレーム生成は無駄）。

**探す場所を間違えないこと**（実際に3回外した）: `Controllers` は
`IVideoEffectProcessor`（インターフェース）にも `VideoEffectBase`（エフェクト側）にも
`VideoEffectProcessorBase` にも**無い**。あくまで `DrawDescription` のプロパティ。
手本: `C:\YMM4_PluginDevelop\...`（外部）→ https://github.com/routersys/YMM4-PuppetDeformation
の `PuppetDeformation\PuppetDeformationEffectProcessor.cs`（`BuildControllers`）。

### 共有ストアのタイミング（重要）
- 同一フレーム内の「発信側 → 消費側」評価順は**保証されない**。同一フレームの鮮度を仮定せず直近既知値で許容する。
- YMM4 は一時停止時に別 Usage で再描画するため、`TryGet` の **Usage フォールバックは必須**（`LightSignalStore` に実装済み）。
- 信号が無いチャンネルのデフォルト挙動を必ず決める（連動オフ扱い＝ローカル値、が既定方針）。
- **ゆらぎ（flicker）はストアに乱数値を載せない。** 係数（量/速度/シード）だけを載せ、消費側が `frame/fps` を
  時刻として決定的に再計算する。これによりプレビュー/エクスポート/一時停止再描画で同一 frame は同一の明るさになる。

---

## 検証手順（各マイルストーン末に実施）

1. `dotnet build -c Release -p:Platform=x64` が通り、DLL が plugin フォルダにコピーされる。
2. YMM4 起動 → エフェクト一覧に「シーン光源ターゲット」「リムライト(光源連動)」等が出る。
3. 図形/空アイテムに **シーン光源ターゲット(Ch1)** を挿す → 立ち絵に **リムライト(Ch1)**。
4. ターゲットの位置（またはオフセットX）をキーフレームで動かす → リムの向きが追従する。
5. 光色・強度の変更が消費側に反映される。**Ch2** に変えると分離する（Ch1消費は反応しない）。
6. ターゲットの無いチャンネルを消費側に指定 → 連動オフ（ローカル値のみ）で破綻しない。
7. 一時停止・シークでちらつかない（Usage フォールバック）。
8. 動画エクスポート結果がプレビューと一致。ゆらぎが frame ベースで決定的。

得られた知見（定数バッファのずれ、YMM4 API の差異等）はこの CLAUDE.md に追記していくこと。

---

## 現在の実装状況

- ✅ **M0**: 雛形（`LightRig.csproj`, `Directory.Build.props`, `LightRigPlugin.cs`）＋本ドキュメント。
- ✅ **M1**: `Shared\{LightChannel,LightState,LightSignalStore}.cs`、`Effects\LightTarget\{LightTargetEffect,LightTargetProcessor}.cs`、`Effects\ShaderResourceLoader.cs`（移植済み）。
- ✅ **M2**: `Shared\LightMath.cs`（座標/ゆらぎヘルパー）、`Effects\SceneRimLight\`（3クラス＋`Shaders\SceneRimLightPS.hlsl`）、`Effects\SceneShading\`（3クラス＋`Shaders\SceneShadingPS.hlsl`）。
- ✅ **M3**: `Effects\Backlight\`（3クラス＋`Shaders\BacklightPS.hlsl`）。**Phase 1 完了。**
- ✅ **M4**: `Effects\Relighting\`（3クラス＋`Shaders\RelightingPS.hlsl`）。アルファ擬似法線＋プリセット＋環境光ミックス。
- ✅ **M5**: `Shared\AmbientSignalStore.cs`、`Effects\AmbientSampler\{Effect,Processor}.cs`（GPU読み戻し）。**Phase 2 完了。**
- ✅ **M6**: `Effects\SceneShadow\`（3クラス＋`Shaders\SceneShadowPS.hlsl`）。落とし影。
- ✅ **M7**: `Effects\DepthFog\`（3クラス＋`Shaders\DepthFogPS.hlsl`）。空気遠近。
- ✅ **M8**: `Effects\BlendLight\`（3クラス＋`Shaders\BlendLightPS.hlsl`）＋ `AmbientSignalStore` のグリッド拡張。背景なじませ。
- ✅ **M9**: `LightTarget` に環境光サンプラーへの色・明るさ追従を追加。光源1つでシーン全体が背景の明るさへ揃う。
- ✅ **M10**: 光源の種類（点光源/平行光/スポット）と距離減衰。`LightMath.ScreenDir` / `Attenuation` に集約し、消費側6箇所は1行ずつ。プレビューに到達範囲の円・円錐を追加。

### カメラ距離の算出（M7・他プラグイン不要）
`DrawDescription` だけでカメラ距離が求まる。2DCamera プラグインの DOF と同じ式（Z_DepthofField 由来）:
```csharp
Matrix4x4.Invert(drawDesc.Camera, out var invView);
var eye = Vector3.Transform(new Vector3(0, 0, 1000), invView); // YMM4 標準カメラ距離 1000
var viewDir = Vector3.Normalize(new Vector3(-invView.M31, -invView.M32, -invView.M33));
var rel = drawDesc.Draw - eye;
float axial = Vector3.Dot(rel, viewDir);        // 平面距離
float radial = rel.Length();                    // 放射距離
```
2DCamera の FOV は `Draw` を視軸に直交する方向へずらすが**視軸方向の成分は保存される**ため、
「平面」モードなら 2DCamera と併用しても距離がずれない。

**UI に出す距離は `raw - 1000`（標準カメラ位置を 0 とした奥行き）にする。**
YMM4 の既定位置のアイテムは生の距離が 1000 になるため、そのまま見せると
「開始距離 1200」のような直感に反する値になる。1000 を引くとアイテムの Z 座標と目盛りが一致し、
実運用の調整範囲（おおむね -1000〜1000）とスライダーの範囲が揃う。

### 落とし影の投影モデル（M6）【「地面へ無限投影」は失敗。帯モデルにすること】
接地線から上へ `shadowLength = 画像高さ × lengthRatio` の**帯**を影の領域とし、
出力の地面からの高さ `hOut` を `t = hOut/shadowLength` に正規化して、
立ち絵の高さ `h = t × 画像高さ` をサンプリングする（全身が帯に収まる）。
横へは `hOut × lean` ずらす。
- `lean = -dir.x × 傾き` … 光が右なら影は左へ倒れる。真上の光（dir.x=0）は足元にまっすぐ落ちる。
- `lengthRatio = 長さ ÷ (1 + 光源の高さ/200)` … 光が高いほど短い影。**`LightState.Height` の2つ目の用途**。
- **影の広がりが `shadowLength` と `lean` だけで決まる**ので、必要な矩形を正確に計算できる。

**当初の「地面を無限平面とみなして h*shear だけ横へ落とす」方式は破綻した**（実機で確認）。
頭部の影が `画像高さ × 傾き`（数百px）も離れた位置へ飛び、細長い帯が画面外まで伸びて
「本体から離れた場所に別の影が出る」「範囲が足りず全体が出ない」状態になる。
2D の立ち絵では**影の伸びる量に上限がある帯モデル**が正しい。

- 2D の絵では厳密な投影が不自然になるので、長さ・傾き・接地位置の手動倍率を必ず残すこと。
- 影は**アイテム自身の描画範囲にしか描けない**（背景アイテムの上には落ちない）。
- 矩形は 横 `shadowLength×|lean|＋先端ぼかし`、下 `接地オフセット`、
  上 `shadowLength −(画像高さ＋接地オフセット)`（先端が上端を超える分だけ）拡張する。上限クランプ必須。
- **`MapOutputRectToInputRects` は画像全体を返すこと**（`Expand(outputRect)` ではダメ）。
  影の長さが立ち絵より短いと、出力画素は自分より最大で画像高さぶん**上**を参照するため、
  出力矩形を機械的に広げても入力が足りず、離れた場所に影の断片が出る。
  帯モデルへ作り直した際にこれを巻き戻してしまい再発させた。
- **投影のサンプリングは入力画像の範囲外なら明示的に 0 を返す。**
  D2D はテクスチャ外を端の画素で引き伸ばす（クランプ）ため、素通しでサンプリングすると
  画像の縁の色が帯状に伸び、**光源位置に関係なく決まった場所に出る影の断片**になる。
  シーン座標で `inputLeft/Top/Width/Height` と比較してから読むこと（先端ぼかしの各タップも同様）。
- **接地位置は符号込みで矩形計算に入れる。** 帯は
  `[groundYRel - shadowLength, groundYRel]`（`groundYRel = 画像高さ + 接地位置`）を占め、
  接地位置は帯ごと平行移動させる。`max(接地位置, 0)` で計算すると
  上へずらしたときに上方向の拡張が足りず影が見切れる。
- **参照元の高さは「画像の下端」から測る（接地線 groundY から測らない）。**
  立ち絵が立っているのは画像の下端。接地線から測ると接地位置をずらしたぶん
  参照窓ごと画像の外へずれ、範囲外が透明になって**はみ出した側の影の内容が欠ける**
  （接地位置を+にすると片側、−にすると逆側が欠ける）。
  接地位置は**出力側の帯を平行移動させる役割だけ**に限定する。

### 多点サンプリングのぼかしは「リング」ではなく「円板」で散らす
8方向リング（`kOffsets[8]`）でマスクを平均すると、**不透明度の違うコピーが8つ並んだ多重像**に見える。
ぼかしに見せたいときは**黄金角スパイラルで円板状に均一分布**させる（落とし影の先端ぼかしで採用）:
```hlsl
float fi = (float)i + 0.5f;
float r = sqrt(fi / TAPS) * radius;   // 円板上で均一分布
float ang = fi * 2.39996323f;         // 黄金角
float2 o = float2(cos(ang), sin(ang)) * r;
```
16タップ程度で十分滑らかになる。手本: PerspectiveShadow（16〜128サンプルのスパイラル）。
※ シェーディング/リライティングの「陰影スカラー平均」は用途が別（低周波化が目的）なのでリングのままでよい。

### M5 環境光サンプラーの実装メモ（重要）
- **GPU→CPU 読み戻し方式**。`AmbientSamplerProcessor` は本体の DeviceContext とは別に
  `device.CreateDeviceContext` で専用コンテキストを作り、入力を 8x8 の Target ビットマップへ縮小描画 →
  CpuRead ビットマップへ `CopyFromBitmap` → `Map(MapOptions.Read)` で画素を取得する。
- **【縮小の補間モードは `Anisotropic`。`Linear` にしてはいけない（2026-08・実機で事故）】**
  1920x1080 → 8x8 は約240:1 の縮小で、バイリニアは出力1画素あたり 2x2 テクセルしか読まないため
  **実質的な点サンプリング**になる。暗い部屋に強い光源がある絵では、隅のセルがたまたま明るい画素を拾って
  「背景は隅ほど暗いのに、なじませの色が暗くならない」という見え方をした。
  `Anisotropic` はミップマップを使うので縮小率が大きくても面積平均に近い色になる。
- **単純平均ではなく輝度しきい値で選別**する。建物・木・道路などの暗色に引きずられると
  環境光の印象と合わないため、輝度（0.299R+0.587G+0.114B）がしきい値以上の画素だけを平均し、
  空・光源・明部の色を拾う。該当画素が無い場合は全画素平均へフォールバックする。
  透明画素（α≈0）は背景色として数えない。
- 負荷対策で **SampleInterval フレームおき**にのみ測定し、毎フレームは最後の色を Publish（Usage 別に新鮮さを保つ）。
- **失敗時は `_disabled` で以降のサンプリングを止め、パススルーに縮退**（try/catch で例外連発を防ぐ）。
  既存プラグインに読み戻しの前例が無いため、実機で「重くないか」「色が妥当か」「一時停止で落ちないか」を要確認。
- 環境光は `AmbientSignalStore`（位置を持つ `LightSignalStore` とは別系統）で受け渡す。
  同一チャンネルで光源ターゲットと環境光サンプラーを併用しても互いを上書きしない。
- 消費側は **リライティングの「環境光ミックス」**（影色へ背景色を混ぜる）と **背景なじませ**（M8）。リム/逆光にも同様に足せる。
- **M8 で発信内容を `Vector4`（代表色）から `AmbientState`（代表色＋3x3色グリッド＋背景のシーン矩形）へ拡張した。**
  従来の `TryGet(... out Vector4)` は `TryGetState` のラッパーとして残してあるので、リライティング側は変更不要。
  グリッドは同じ 8x8 読み戻しから作るので追加コストは無い。**グリッドには輝度しきい値を掛けない**
  （「その場所の背景色」が欲しいため。しきい値は代表色専用）。不透明画素が無いセルは代表色で埋める。

### M8 背景なじませの実装メモ（AutoBlendLight 相当）

AviUtl2 の `aviutl2_script_AutoBlendLight` に相当する「背景の色に被写体を馴染ませる」機能。

**AviUtl との構造的な差（最重要）**: AviUtl のスクリプトは `copybuffer("obj","frm")` で
**自分より下に描画済みのフレームバッファを読める**ので、サンプリング位置の背景色をその場で取れる。
**YMM4 の映像エフェクトは自アイテムの画像しか入力に持たない**（落とし影が背景の上に落ちないのと同じ制約）。
そのため背景の色は必ず「背景アイテム側の環境光サンプラー → チャンネルストア → 消費側」を経由する。
AutoBlendLight の「背景なじませ（背景をぼかして重ねる）」項目だけは**原理的に移植不可**。

**位置対応付け（背景グリッド UV）**は CPU 側でアフィン変換を組んでシェーダーへ渡す:
```
uv = uvOrigin + (posScene.xy - 入力矩形の中心) * uvScale
uvOrigin = (立ち絵のDraw + 位置オフセット − 背景のRectMin) / 背景のRectSize
uvScale  = 立ち絵のZoom * 範囲倍率 / 背景のRectSize
```
- 背景のシーン矩形は `GetImageLocalBounds`（ローカルサイズ）× `drawDesc.Zoom` を `drawDesc.Draw` 中心に置いた近似。
  **回転は考慮していない**ので、ずれる場合のために「位置オフセットX/Y」「範囲倍率」を必ず残すこと。
- 矩形は**毎フレーム作り直す**（サンプリングは数フレーム間引いていても、背景が動けば対応付けは追従させたい）。
- `drawDesc.Zoom` は **`Vector2`**（float ではない）。

**グリッドは cbuffer にスカラーで持つ**（2枚目の入力テクスチャにしない）。
2入力にすると input1 の UV がタイルごとに正規化され、任意位置サンプリングが破綻するため。
- `float4 cell[16]` のような **cbuffer 配列は使わない**。`[CustomEffectProperty]` は
  既存 181 箇所すべて `PropertyType.Float` で、**`PropertyType.Vector4` は前例が無い**
  （コンパイルは通るが実機未検証）。3x3×3成分＝27個の float プロパティに素直に展開した。
  HLSL 側もスカラー 27 個で受け、シェーダー内で `float3 cells[9]` に詰めてバイリニア補間する。
- cbuffer は 20 + 27 + pad 1 = **48 float（192byte）**。C# の ConstantBuffer と個数を必ず一致させること。

**モードは2つ**（AutoBlendLight の「ライティング」に対応）:
- グラデーション … 光源方向に沿った位置を `smoothstep(1-広がり, 1, t)` で 0..1 にして面で染める
- 縁取り … リムライトと同じアルファ差分で光源側の輪郭だけを染める

光を"足す"側なので**設計方針(A)どおり別レイヤー化 → GaussianBlur → 合成モード → CrossFade**。
既定は合成モード=スクリーン（加算だと白飛びしやすく「馴染ませ」にならない）。

### プロパティの条件付き表示は `ShowPropertyEditorWhen` でできる（2026-08・調査済み）
`YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhenAttribute(string propertyName, object value)`
が **public で提供されている**。種類によって使わないパラメータを隠せる。

```csharp
[ShowPropertyEditorWhen(nameof(SourceType), LightSourceType.Spot)]
```

**制約**（実機で確認する前にコンパイルで判明した）:
- **`AllowMultiple = false`**。1プロパティに条件は1つだけ。
- **等値比較のみ**。「〜以外」「A または B」は書けない。
- したがって「平行光**以外**で表示」のような条件は表現できない。
  **列挙を切り分けて「ちょうど1つの値に属する」形にすると隠せる**
  （例: 到達距離は `SourceType` ではなく `FalloffMode == Range` を条件にした）。
- 複数の値で使うプロパティ（例: 光の角度は平行光とスポットの両方で使う）は常時表示のままにして、
  Description に「点光源では使いません」と書く。

### 「〜連動」という名前は依存先を正確に書く（2026-08・実機の混乱）
背景なじませを「背景なじませ(光源連動)」としていたため、
**実際に必要なのは環境光サンプラーなのに「光源を置かないと効かない」と誤解される**事故が起きた。
「(環境光連動)」へ改名し、あわせて**光源を一切参照しない「全体」モード**を追加した。
名前に入れる連動先は「無いと動かないもの」にすること。任意の連動先を名前に入れてはいけない。

### M9 環境光の自動追従は「光源ターゲット側」に置く【重要な設計判断】

背景の明るさ・色温度へシーン全体を追従させたいとき、**消費エフェクト7個それぞれに
「背景から色を取る」を実装してはいけない**。同じロジックが散り、UI も増える。
**`LightTargetProcessor` が `AmbientSignalStore` を読んで `LightState.Color` / `Intensity` を
上書きしてから Publish する**のが正解。消費側は一切変更なしで一斉に揃う（チャンネル基盤の本来の使い方）。

- 光源ターゲットは LightSignalStore へ**書き**、AmbientSignalStore から**読む**。
  ストアが別系統なので同一チャンネルでも循環しない。
- **色と明るさは必ず分離する。** 代表色をそのまま光色へ混ぜると、暗い背景で光色まで暗くなり
  「明るさの追従」と二重に効く。色は `NormalizeTone`（最大成分を1に正規化）で**色味だけ**取り出す。
- 明るさは `背景輝度 ÷ 基準の明るさ` を倍率にし、**上限クランプ必須**（極端な背景で光が暴走する）。
- 追従率 0%（既定）で従来どおりの手動動作。既存プロジェクトを壊さない。

**プリセットの「自動選択」はやってはいけない。** リライティングのプリセットは離散なので、
背景が少し変わるだけでルックが不連続に飛ぶ。加えて環境光は「Nフレームおきに測った直近既知値」で
厳密なフレーム決定性が無いため、**プレビューと書き出しで別のプリセットが選ばれる事故**が起きる。
連続量（色・強度・ミックス率）へ流し込む形だけにすること。

### 背景グリッドを「上段=空 / 下段=地面」と決め打ちしてはいけない（2026-08・不採用）
落とし影の影色を下段セルから、空気遠近のフォグ色を上段セルから取る案を検討したが**却下**。
背景アイテムのどこが画面のどこに来るかはカメラと配置で変わるため前提が崩れる。
具体例: 左下から右上へ伸びる落とし影では、床の色が一様でも参照セルが動いてしまい色が変わって見える。
位置に依存する色が要るなら、行を決め打ちにせず**対象自身のシーン座標でグリッドを引く**
（背景なじませと同じアフィン変換を使う）こと。ただし 3x3 では壁と床の境目をまたぐと不正確。

### M10 光源の種類と距離減衰

**それまで光源は1種類（点光源をアイテム内では平行光とみなす）しかなく、距離減衰も無かった。**
画面の隅に置いた松明が反対側の立ち絵も等しく照らしていた。M10 でここを埋めた。

- **規約は `LightMath` に集約する。** 消費側は
  `LightMath.ScreenDir(light, itemPos)`（種類に応じて方向を返す）と
  `LightMath.Attenuation(light, itemPos)`（0..1 の減衰係数）を呼ぶだけ。
  **`ScreenDirTowardLight` を消費側から直接呼ばないこと**（平行光が効かなくなる）。
- **後方互換**: `LightSourceType.Point = 0` かつ `Range = 0`（＝無限、減衰なし）が既定。
  `default(LightState)` が従来と同じ挙動になるよう数値を選んである。
- **【連続スライダーに「0=無限」のようなマジック値を持たせてはいけない（2026-08・実機で事故）】**
  当初 UI の「到達距離」を 0=無限としていたため、**0 の隣の 1px が「ほぼ全滅」**という崖ができ、
  スライダーを 0 から動かした瞬間に光源連動エフェクトが全部消えて「壊れた」ように見えた。
  有効/無効は必ず**別の列挙（`LightFalloffMode`）で明示**し、距離側は常に実距離だけを表す。
  `LightState.Range` の内部表現は 0=無限のままで良い（発信時に変換する）。
- **減衰の適用先はエフェクトごとに違う**。「光の強さ」に相当する量へ掛けること:
  リム/逆光/リライティング/背景なじませ = `lightIntensity`、
  シェーディング = `strength`（濃さ）、落とし影 = `opacity`（濃さ）。
- 減衰式は「**減衰の始まり**（到達距離に対する割合）までは等倍 → 到達距離にかけて smoothstep で 0」。
  物理の逆二乗則ではなく**「どこまで届くか」を作画的に指定できる形**にしている（到達距離ちょうどで 0）。
- **【指数カーブ `t^n` は不採用・実機で調整不能と判明（2026-08）】**
  当初 `(1 - 距離/到達距離)^カーブ` にしていたが、値と見た目の対応が直感に反して詰められなかった。
  `n<1` は到達距離の手前までほぼ等倍を保って**縁で一気に落ち**、`n=1`（直線）は
  **光源の真上以外が常に減光される**ため「効きが弱い」と感じる。有効な調整域が両端に偏る。
  刻みを細かくしても直らない性質なので、**プラトー（等倍領域）＋ smoothstep** へ作り直した。
  パラメータが「境界の位置」を素直に動かすだけになり、スライダーの端から端まで意味を持つ。
  同種の"効き具合"パラメータを足すときは、指数ではなく境界位置で表現できないか先に考えること。
- **`Angle` は一貫して「光が来る向き」**（0=上から、時計回り）。
  スポットの円錐の軸は光が進む向きなので `-DirFromAngle(Angle)` と符号を反転させる。
  角度0のスポットは真下を照らす。
- 平行光は距離の概念を持たないので `Attenuation` は常に 1 を返し、`Position` も参照されない。

### プレビューの表示専用ガイド（M10）
- **`VideoControllerPointShape` には `None` がある**（他は `Circle` / `Square`）。
  ドラッグさせない表示専用の頂点はこれにする。`VideoControllerPointConnection` は `None` / `Line` のみ。
- **円は描けないので 48 角形の折れ線**で代用する（終点に始点を足して閉じる）。
- 減衰を入れるなら**到達範囲の可視化は必須**。届く距離が見えないと調整できない。
  到達距離はドラッグハンドル（`Square`）から `Range.AddToEachValues(arg.Delta.X)` で直接動かせる。
- 円は**2本描く**（外＝到達距離, 内＝減衰の始まり）。数値パラメータの意味を目で確認できるようにする。
- 操作点は**パラメータが変わった時だけ**作り直す。キャッシュキーに種類・到達距離・角度・スポット角を含めること
  （オフセットだけを見ていると種類を変えてもガイドが更新されない）。

### M2 で確立した設計（M3 以降も踏襲すること）
- **光源方向は CPU 側で算出**する。「立ち絵の Draw 座標 → 光源の Draw 座標」の差から
  **スクリーン向き（Y下）の単位方向**を得る。アイテム内は方向一定。`SCENE_POSITION`→ワールド逆変換は不要。
  **【M10 で入口が変わった】消費側が呼ぶのは `LightMath.ScreenDir(light, itemPos)`。**
  `ScreenDirTowardLight` はその内部実装（点光源・スポット用）なので直接呼ばないこと（平行光が効かなくなる）。
- **ワールドY↔スクリーンYの反転は `LightMath` に1箇所だけ**。実機でリムの上下が逆なら
  `ScreenDirTowardLight` の Y 符号だけ直せば全エフェクトが同時に直る。各エフェクトの「角度オフセット」でも微調整可。
- **連動と単体動作の両立**: `Channel==Off` または `TryGet` 失敗時は「角度オフセットを絶対角」として単体動作。
  消費エフェクトは必ずこのフォールバックを持たせる。
- **ゆらぎ**は `LightMath.Flicker(frame, fps, amount, speed, seed)` で決定的に再計算し、リムの強度（CrossFade Weight）へ乗算。
- リムは RimLight と同じ多段グラフ（custom→Blur→Composite/Blend→CrossFade）。
  シェーディング・逆光・リライティングは結果を焼き込んで出力する単一シェーダー。

### 「ぼかし量」の設計方針【重要・2種類を使い分ける】

**(A) 光を"足す"もの（リム・逆光）＝ 別レイヤー化して D2D の GaussianBlur を通す**
リムのような加算の光は、**必ず独立したレイヤーとして生成 → `GaussianBlur` → 合成モードで重ねる**。
グラフ: `custom(rim) → GaussianBlur → Composite/Blend(下地と) → CrossFade`。
手本: `SceneRimLightEffectProcessor.cs` / `BacklightEffectProcessor.cs`。
- **1シェーダーに焼き込むとぼかしが効かない**。シェーダー内の8方向リング平均では
  アルファ差分の硬いエッジを柔らかくできず、「上から白く塗った」筋状の見た目になる（実機で確認）。
- 合成モード（`Blend`）を選べるようにすること。塗りつぶし感の回避に効く。
  `blendMode.IsCompositionEffect()` で Composite/Blend を切り替える（`using YukkuriMovieMaker.Player;` が必要）。
- リムの量は `CrossFade.Weight`(0..1)。100%超はリム色に gain を掛けて表現する。

**(B) 陰影を"焼き込む"もの（シェーディング・リライティング）＝ シェーダー内でスカラーを平均**
出力画像全体をぼかすと**絵柄まで甘くなる**ので絶対にやらない。
**陰影スカラー（shade / lambert）だけを 8 方向リングでタップ平均**し、中心画素の色に掛ける。
- 実装は `kOffsets[8]` + `computeXxx()` を複数点で評価して平均（中心は重み2）。
- `blur<=0` なら 1 タップに落とすので、既定 0 なら追加コストは無い。
- 矩形拡張（`Range`）に必ず blur 分を足すこと（足さないと縁が切れる）。
- こちらが有効なのは「暗くする＝元画素に掛ける」処理だから。光を足す処理には (A) を使う。

**擬似ノーマル系（シェーディング mode=1 / リライティング）の注意**: 法線は
「アルファのリング勾配」から作る（輝度勾配は服の柄・髪を拾ってシワになるので使わない）。
リング半径は **フォルム(formScale)** で、ぼかし量とは別パラメータ。
フォルムを大きくすると細部でエイリアシングして光と影が縞状に織り重なるため、
その緩和が「ぼかし量」の役割（lambert を平均して馴染ませる）。

### M3 実装メモ（逆光 Backlight）
- 独立エフェクト `Effects\Backlight\` として作る（一覧での発見性のため）。
- **実装結果**: 本体シェーダー（`BacklightPS.hlsl`）は「全体を薄く減光＋彩度低下」だけを担当し、
  リムは `SceneRimLightCustomEffect` を**別レイヤーとして再利用**して
  GaussianBlur → 合成モード → CrossFade で重ねる多段グラフにした。
  当初検討した「1シェーダーに焼き込む」構成は、ぼかしが効かず輪郭が硬いままになるため却下している
  （「ぼかし量」の設計方針(A)を参照）。
- 光源連動は M2 と同じ `LightSignalStore.TryGet` + `LightMath` を使う。
