using System.ComponentModel.DataAnnotations;

namespace LightRig.Shared;

/// <summary>
/// シーン光源のチャンネル（発信側＝シーン光源ターゲット用）。
/// 「窓からの光」「室内灯」など複数光源を同一シーンに共存させるために分離する。
/// </summary>
public enum LightChannel
{
    [Display(Name = "チャンネル1")] Ch1 = 1,
    [Display(Name = "チャンネル2")] Ch2 = 2,
    [Display(Name = "チャンネル3")] Ch3 = 3,
    [Display(Name = "チャンネル4")] Ch4 = 4,
    [Display(Name = "チャンネル5")] Ch5 = 5,
    [Display(Name = "チャンネル6")] Ch6 = 6,
    [Display(Name = "チャンネル7")] Ch7 = 7,
    [Display(Name = "チャンネル8")] Ch8 = 8,
}

/// <summary>
/// シーン光源のチャンネル（受信側＝リムライト等の消費エフェクト用・無効を含む）。
/// Ch1〜Ch8 の数値は <see cref="LightChannel"/> と一致させること（(LightChannel)値 でキャストする）。
/// </summary>
public enum LightChannelOrOff
{
    [Display(Name = "無効")] Off = 0,
    [Display(Name = "チャンネル1")] Ch1 = 1,
    [Display(Name = "チャンネル2")] Ch2 = 2,
    [Display(Name = "チャンネル3")] Ch3 = 3,
    [Display(Name = "チャンネル4")] Ch4 = 4,
    [Display(Name = "チャンネル5")] Ch5 = 5,
    [Display(Name = "チャンネル6")] Ch6 = 6,
    [Display(Name = "チャンネル7")] Ch7 = 7,
    [Display(Name = "チャンネル8")] Ch8 = 8,
}
