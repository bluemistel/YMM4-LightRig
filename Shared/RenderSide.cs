using YukkuriMovieMaker.Player.Video;

namespace LightRig.Shared;

/// <summary>
/// 場面切り替え中の「前の場面側／後の場面側」を見分けるための判定。
///
/// <para>
/// 【なぜ必要か（2026-09・実機／IL 調査済み）】
/// 場面切り替え中、YMM4 は前後2本の <c>TimelineSource</c> を<b>並列に</b>描画する
/// （<c>TransitionSource.ctor</c> が <c>TransitionItemPicker</c> と <c>TimelineSource</c> を
/// <c>isBefore</c> 違いで2組生成する）。ところが両者へ渡される
/// <c>TimelineSourceDescription</c> は<b>同一インスタンス</b>なので、
/// SceneId・Usage・TimelinePosition・ScreenSize すべてが前後で等しい。
/// 説明オブジェクトから前後を見分けることは原理的にできない。
/// </para>
///
/// <para>
/// 唯一の非対称性が<b>アイテムの選択フレーム</b>にある。
/// <c>TransitionItemPicker.PickItems</c> は <c>isBefore</c> のとき
/// <b>切り替えアイテムの開始フレームでアイテムを選ぶ</b>のに、描画時刻は現在フレームのままなので、
/// 前の場面側では<b>すでに終わったアイテムが自分の終端より後ろを描かされる</b>状態になる。
/// 通常の再生では終わったアイテムはそもそも描画されないため、これは切り替えの前側でしか起きない。
/// </para>
///
/// <para>
/// これを発信側・消費側の双方で判定し、<b>同じ側どうしを優先して結び付ける</b>ことで、
/// 「前の場面にいる立ち絵が次の場面の環境光を拾う」事故を防ぐ。
/// ただし<b>切り替えをまたいで存在するアイテムは前後どちらでも「保持されていない」</b>ため
/// 見分けられない。そのケースは後の場面側の値が優先される（曖昧なままにできる情報が無い）。
/// </para>
/// </summary>
internal static class RenderSide
{
    /// <summary>
    /// このアイテムが自分の終端より後ろを描かされているか
    /// （＝場面切り替えの「前の場面」側として保持されている）。
    /// </summary>
    public static bool IsHeld(EffectDescription desc)
        => desc.ItemPosition.Frame >= desc.ItemDuration.Frame;
}
