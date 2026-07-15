using HarmonyLib;

namespace KK_HCharaAdjustmentEx.VR
{
    internal static class Hooks
    {
        // H シーン開始（スポット初期化後）。本体側も同メソッドをリフレクションでフックして
        // CurrentFlags を確保するので、こちらは VR 入力の初期化のみ行う。
        [HarmonyPostfix]
        [HarmonyPatch(typeof(VRHScene), "MapSameObjectDisable")]
        internal static void MapSameObjectDisable(VRHScene __instance)
        {
            if (!Plugin.IsEnabled) return;
            VRInput.OnSceneStart(__instance);
        }
    }
}
