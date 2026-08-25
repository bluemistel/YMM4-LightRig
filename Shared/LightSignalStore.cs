using YukkuriMovieMaker.Player.Video;

namespace LightRig.Shared;

/// <summary>
/// シーン光源ターゲット → 消費エフェクト（リムライト等）間で光源状態を受け渡す共有ストア。
/// YMM4 プラグイン API には他アイテムの状態を問い合わせる手段が無いため、
/// 光源側の「シーン光源ターゲット」エフェクトが毎フレーム発信した <see cref="LightState"/> をここに保持する。
///
/// 選択規則・Usage/フレームまわりの注意は <see cref="FrameSignalStore{T}"/> を参照。
/// </summary>
internal static class LightSignalStore
{
    static readonly FrameSignalStore<LightState> store = new();

    /// <summary><paramref name="frame"/> は <c>TimelinePosition.Frame</c> を渡すこと。</summary>
    public static void Publish(Guid sceneId, TimelineSourceUsage usage, LightChannel channel, long frame, in LightState state)
        => store.Publish(sceneId, usage, channel, frame, state);

    /// <summary><paramref name="frame"/> は <c>TimelinePosition.Frame</c> を渡すこと。</summary>
    public static bool TryGet(Guid sceneId, TimelineSourceUsage usage, LightChannel channel, long frame, out LightState state)
        => store.TryGet(sceneId, usage, channel, frame, out state);
}
