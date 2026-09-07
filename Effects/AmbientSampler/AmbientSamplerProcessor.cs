using System.Numerics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using LightRig.Shared;

namespace LightRig.Effects.AmbientSampler;

/// <summary>
/// 環境光サンプラーのプロセッサ。映像はパススルーし、数フレームおきに入力画像を NxN へ縮小して
/// GPU→CPU 読み戻しし、次の2つを発信する。
/// <list type="bullet">
/// <item>代表色 … 輝度しきい値以上の画素の平均色（従来どおり。リライティングの環境光ミックス等が使う）</item>
/// <item>色グリッド … 背景を 3x3 に区切ったセル平均色。「背景なじませ」が立ち絵の位置に応じた背景色を引く</item>
/// </list>
/// 読み戻しは専用の DeviceContext・オフスクリーンビットマップで行い、本体のレンダーは触らない。
/// 失敗時は _disabled で以降のサンプリングを止め、パススルーのみに縮退する。
/// </summary>
internal sealed class AmbientSamplerProcessor : IVideoEffectProcessor
{
    // サンプリング解像度（NxN）。しきい値で画素を選別するため、統計が安定する程度の解像度を取る。
    // グリッドの1辺 G は N を割り切る必要はない（セル境界は整数除算で切る）。
    const int N = 8;
    const int G = AmbientState.GridSize;

    private readonly IGraphicsDevicesAndContext _devices;
    private readonly AmbientSamplerEffect _item;

    private ID2D1Image? _input;

    private ID2D1DeviceContext? _dc;
    private ID2D1Bitmap1? _target;   // 描画先（NxN, Target）
    private ID2D1Bitmap1? _staging;  // 読み戻し用（NxN, CpuRead）

    // 読み戻しの失敗は「永久停止」にしない。一度の失敗で二度と測らなくなると、
    // 古い色を配り続けたまま復帰できず、原因も分からない状態になるため
    // （実際に「停止すると古い環境光のまま固まる」不具合の候補になった）。
    // 失敗するたびに間隔を空けて再挑戦する（例外の連発は避けつつ復帰はできる）。
    private int _failureCount;
    private long _retryAfterFrame = long.MinValue;

    private long _lastSampledFrame = long.MinValue;
    private Vector4 _lastColor = new(0.5f, 0.5f, 0.5f, 1f);
    private Vector3[]? _lastGrid;
    private float[]? _lastGridCoverage;
    private float _lastCoverage = 1f;
    private Vector2 _lastLocalSize;  // 入力画像のローカルサイズ（px）。矩形は毎フレーム drawDesc から作り直す
    private bool _hasColor;

    public AmbientSamplerProcessor(IGraphicsDevicesAndContext devices, AmbientSamplerEffect item)
    {
        _devices = devices;
        _item = item;
    }

    public ID2D1Image Output => _input!;

    public void SetInput(ID2D1Image? input) => _input = input;

    public void ClearInput() => _input = null;

    public DrawDescription Update(EffectDescription desc)
    {
        var drawDesc = desc.DrawDescription;
        if (_input is null)
            return drawDesc;

        long frame = desc.TimelinePosition.Frame;
        // 間隔はミリ秒指定。プロジェクトの FPS に合わせてフレーム数へ換算する
        // （フレーム単位で持つと 30fps と 60fps で追従速度が変わってしまう）。
        int fps = Math.Max(1, desc.FPS);
        int interval = Math.Max(1, (int)Math.Round(fps * _item.SampleIntervalMs / 1000.0));

        // 間引き（SampleInterval）は「連続再生で少しずつ前へ進む」ときだけの最適化。
        // 巻き戻しシークは内容が変わった可能性が高いので即座に測り直す。
        // これをしないと、真夜中→夕方へ1フレームずつ戻したとき
        // 差分が interval に達するまで（既定5フレーム）古い色が出続ける。
        //
        // 【差分の計算は _hasColor が true のときだけ行うこと】
        // _lastSampledFrame の初期値は long.MinValue なので、frame=0（タイムライン先頭へ
        // ショートカットで飛んだ場合など）だと frame - long.MinValue が long.MinValue に
        // オーバーフローし、Math.Abs が OverflowException を投げてプレビューが落ちる。
        var needSample = !_hasColor;
        if (!needSample)
        {
            long delta = frame - _lastSampledFrame; // 双方とも実在のフレームなので安全
            needSample = delta < 0 || delta >= interval;
        }

        var canTry = _failureCount == 0 || frame >= _retryAfterFrame;
        if (canTry && needSample)
        {
            if (TrySample(out var color, out var grid, out var gridCoverage, out var coverage, out var localSize))
            {
                _lastColor = color;
                _lastGrid = grid;
                _lastGridCoverage = gridCoverage;
                _lastCoverage = coverage;
                _lastLocalSize = localSize;
                _hasColor = true;
                _lastSampledFrame = frame;
                _failureCount = 0;
            }
            else
            {
                // 失敗回数に応じて再挑戦までの間隔を伸ばす（最大 600 フレーム）
                _failureCount++;
                _retryAfterFrame = frame + Math.Min(_failureCount, 10) * 60;
            }
        }

        // 毎フレーム（自 Usage 向けに）最後に測った色を発信する。
        // 矩形だけは毎フレーム作り直す。サンプリングを間引いていても、
        // 背景が動けば「どのセルがどこか」の対応付けは追従させたいため。
        if (_hasColor)
        {
            var min = SceneRect(drawDesc, out var size);
            // 発信元キーは Usage をまたいで同一の _item（プロセッサは Usage ごとに別インスタンス）
            // 有効範囲＝このサンプラーを載せたアイテムが存在するタイムライン区間。
            // 範囲外のフレームでは選ばれないので、背景アイテムが終わった後も
            // 古い背景色が配られ続けることがない。
            long itemStart = frame - desc.ItemPosition.Frame;
            AmbientSignalStore.Publish(
                desc.SceneId, desc.Usage, _item.Channel,
                frame, itemStart, itemStart + desc.ItemDuration.Frame + (long)_item.PublishExtension, _item, new AmbientState
            {
                // 「いつ測った値か」を刻む。消費側はこれで古い時刻の値を弾く。
                Frame = _lastSampledFrame,
                Color = _lastColor,
                Grid = _lastGrid,
                GridCoverage = _lastGridCoverage,
                Coverage = _lastCoverage,
                RectMin = min,
                RectSize = size,
            });
        }

        return drawDesc;
    }

    /// <summary>
    /// 背景アイテムがシーン上で占める矩形を、ローカルサイズ・Draw 位置・Zoom から求める。
    /// アイテム中心を Draw 位置とみなす近似で、回転は考慮しない
    /// （ずれる場合は消費側の「位置オフセット」「範囲倍率」で補正する）。
    /// </summary>
    private Vector2 SceneRect(DrawDescription drawDesc, out Vector2 size)
    {
        size = _lastLocalSize * drawDesc.Zoom;
        var center = new Vector2(drawDesc.Draw.X, drawDesc.Draw.Y);
        return center - size * 0.5f;
    }

    private bool TrySample(out Vector4 color, out Vector3[]? grid,
        out float[]? gridCoverage, out float coverage, out Vector2 localSize)
    {
        color = default;
        grid = null;
        gridCoverage = null;
        coverage = 0f;
        localSize = default;
        try
        {
            var mainDc = _devices.DeviceContext;
            EnsureResources(mainDc);
            if (_dc is null || _target is null || _staging is null || _input is null)
                return false;

            // 入力画像の範囲を取得し、NxN へ収める変換を作る
            var b = mainDc.GetImageLocalBounds(_input);
            float w = b.Right - b.Left;
            float h = b.Bottom - b.Top;
            if (!(w > 0f) || !(h > 0f) || !float.IsFinite(w) || !float.IsFinite(h))
                return false;
            localSize = new Vector2(w, h);

            var transform = Matrix3x2.CreateTranslation(-b.Left, -b.Top)
                          * Matrix3x2.CreateScale(N / w, N / h);

            _dc.Target = _target;
            _dc.BeginDraw();
            _dc.Transform = transform;
            _dc.Clear(new Color4(0f, 0f, 0f, 0f));
            // 【補間モード】1920x1080 → 8x8 のような極端な縮小では、バイリニア（Linear）は
            // 出力1画素あたり数テクセルしか読まないため「平均」ではなく飛び飛びの点サンプルになる。
            // 明るい光源の画素をたまたま拾うとセルが実際より大幅に明るくなり、
            // 「背景は隅ほど暗いのに、なじませの色が暗くならない」という見え方になる。
            // Anisotropic はミップマップを使うので、縮小率が大きくても面積平均に近い色が得られる。
            _dc.DrawImage(_input, InterpolationMode.Anisotropic, CompositeMode.SourceOver);
            _dc.EndDraw();
            _dc.Target = null;

            // CPU 読み戻し
            _staging.CopyFromBitmap(_target);
            var map = _staging.Map(MapOptions.Read);
            try
            {
                Analyze(map.Bits, map.Pitch, (float)(_item.LuminanceThreshold / 100.0),  // 0..1 の相対しきい値
                    out color, out grid, out gridCoverage, out coverage);
            }
            finally
            {
                _staging.Unmap();
            }
            return true;
        }
        catch
        {
            // リソースを作り直せば復帰することがあるので、破棄だけして次の機会に再挑戦する
            DisposeResources();
            return false;
        }
    }

    /// <summary>
    /// 縮小画像から代表色と色グリッドを求める。
    ///
    /// 代表色は「輝度が明部基準の threshold 以上の画素だけの平均」。建物・木・道路といった暗色に
    /// 引きずられず、空・光源・明部＝実際に光を投げている部分を環境光として取り出すため。
    ///
    /// 【しきい値は絶対値ではなく「画面内の最大輝度に対する相対値」にすること】
    /// 絶対値だと、0% は全画素平均・100% は該当画素ゼロで全画素平均へフォールバックとなり、
    /// <b>スライダーの両端が同じ結果</b>になる（実機で判明）。さらに夜景のように全体が暗い背景では
    /// どんな値でも該当画素が無く、常にフォールバックしていた。
    /// 最大輝度を基準にすれば 0→100% が単調に「全体の平均 → 最も明るい部分の色」へ変化し、
    /// 明るい背景でも暗い背景でも同じ感覚で効く。
    ///
    /// 一方グリッドは「その場所の背景色」が欲しいのでしきい値を掛けない。
    /// 不透明画素が1つも無いセルは代表色で埋め、対応付けがずれても破綻しないようにする。
    ///
    /// 【アルファの扱い（2026-09）】
    /// 背景を複数枚で組むとき<b>手前の画像はほぼ必ず透過画像</b>になるので、
    /// 半透明の画素を不透明と同じ重みで数えてはいけない。
    /// <b>色はアルファで重み付けして平均</b>し、<b>被覆率（平均アルファ）を別途記録</b>する。
    /// 8x8 への縮小では透明部と不透明部が混ざって薄いアルファの画素になるため、
    /// 重み付けしないと「ほとんど透明な縁の色」が実体と同じ影響力を持ってしまう。
    /// 被覆率は消費側が「その場所にこの画像が実在するか」を判断するのに使う。
    /// </summary>
    private unsafe void Analyze(nint bits, int pitch, float threshold,
        out Vector4 color, out Vector3[] grid, out float[] gridCoverage, out float coverage)
    {
        var p = (byte*)bits;

        // 合計はすべてアルファ重み付き。除数は画素数ではなくアルファの合計。
        double selR = 0, selG = 0, selB = 0, selW = 0;
        double allR = 0, allG = 0, allB = 0, allW = 0;
        double alphaSum = 0;
        int pixelCount = 0;

        var cellSum = new Vector3[G * G];
        var cellWeight = new float[G * G];
        var cellAlpha = new float[G * G];
        var cellPixels = new int[G * G];

        // 1パス目: グリッドと全画素平均を作りつつ、最大輝度を求める（相対しきい値の基準）
        float maxLum = 0f;
        for (int y = 0; y < N; y++)
        {
            byte* row = p + y * pitch;
            int gy = Math.Min(y * G / N, G - 1);
            for (int x = 0; x < N; x++)
            {
                byte* px = row + x * 4; // B8G8R8A8
                float pa = px[3] / 255f;

                int gx = Math.Min(x * G / N, G - 1);
                int gi = gy * G + gx;

                // 被覆率は透明画素も分母に数える（＝その場所に実体があるかを表す）
                cellAlpha[gi] += pa;
                cellPixels[gi]++;
                alphaSum += pa;
                pixelCount++;

                if (pa <= 1e-4f)
                    continue; // 完全な透明部分は色を持たない

                // プリマルチプライドを解除
                float b = px[0] / 255f / pa;
                float g = px[1] / 255f / pa;
                float r = px[2] / 255f / pa;

                // 半透明の画素は色への寄与も小さい。アルファで重み付けする。
                allR += r * pa; allG += g * pa; allB += b * pa; allW += pa;
                maxLum = MathF.Max(maxLum, 0.299f * r + 0.587f * g + 0.114f * b);

                cellSum[gi] += new Vector3(r, g, b) * pa;
                cellWeight[gi] += pa;
            }
        }

        // 2パス目: 最大輝度を基準にした相対しきい値で代表色を作る。
        // しきい値100%でも最大輝度の画素自身は必ず残るので、両端が同じ結果になることはない。
        float absThreshold = maxLum * Math.Clamp(threshold, 0f, 1f);
        for (int y = 0; y < N; y++)
        {
            byte* row = p + y * pitch;
            for (int x = 0; x < N; x++)
            {
                byte* px = row + x * 4;
                float pa = px[3] / 255f;
                if (pa <= 1e-4f)
                    continue;

                float b = px[0] / 255f / pa;
                float g = px[1] / 255f / pa;
                float r = px[2] / 255f / pa;

                if (0.299f * r + 0.587f * g + 0.114f * b >= absThreshold)
                {
                    selR += r * pa; selG += g * pa; selB += b * pa; selW += pa;
                }
            }
        }

        coverage = pixelCount > 0 ? (float)(alphaSum / pixelCount) : 0f;

        if (selW > 1e-4)
            color = new Vector4((float)(selR / selW), (float)(selG / selW), (float)(selB / selW), coverage);
        else if (allW > 1e-4)
            color = new Vector4((float)(allR / allW), (float)(allG / allW), (float)(allB / allW), coverage);
        else
            color = new Vector4(0f, 0f, 0f, 0f);

        var fallback = new Vector3(color.X, color.Y, color.Z);
        grid = new Vector3[G * G];
        gridCoverage = new float[G * G];
        for (int i = 0; i < grid.Length; i++)
        {
            grid[i] = cellWeight[i] > 1e-4f ? cellSum[i] / cellWeight[i] : fallback;
            gridCoverage[i] = cellPixels[i] > 0 ? cellAlpha[i] / cellPixels[i] : 0f;
        }
    }

    private void EnsureResources(ID2D1DeviceContext mainDc)
    {
        if (_dc is not null)
            return;

        var device = mainDc.Device;
        _dc = device.CreateDeviceContext(DeviceContextOptions.None);

        var size = new SizeI(N, N);
        var fmt = new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);

        _target = _dc.CreateBitmap(size, IntPtr.Zero, 0,
            new BitmapProperties1(fmt, 96, 96, BitmapOptions.Target));

        _staging = _dc.CreateBitmap(size, IntPtr.Zero, 0,
            new BitmapProperties1(fmt, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
    }

    private void DisposeResources()
    {
        _target?.Dispose(); _target = null;
        _staging?.Dispose(); _staging = null;
        _dc?.Dispose(); _dc = null;
    }

    public void Dispose() => DisposeResources();
}
