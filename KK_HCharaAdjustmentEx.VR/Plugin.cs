using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace KK_HCharaAdjustmentEx.VR
{
    // VR コントローラーによる H シーン中キャラ位置の一時微調整（旧 KK_HCharaPosVR を統合）。
    // 位置の実体・強制適用・リセットはすべて本体（KK_HCharaAdjustmentEx）側にあり、
    // 本 DLL は VR 入力を HCharaAdjustApi.AddTransientOffset へ橋渡しするだけの薄いフロントエンド。
    // 調整は保存されない（一人称で合わせた位置は三人称ではズレるため、恒久補正には使わない設計）。
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("KoikatuVR")]
    [BepInDependency(KK_HCharaAdjustmentEx.Plugin.PluginGuid)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid    = "KK_HCharaAdjustmentEx.VR";
        public const string PluginName    = "KK_HCharaAdjustmentEx.VR";
        public const string PluginVersion = "1.0.0";

        internal static new ManualLogSource Logger = null!;

        internal static ConfigEntry<bool>?  MasterEnabled;
        internal static ConfigEntry<float>? F1MoveScale;
        internal static ConfigEntry<float>? F2MoveScale;
        public static bool IsEnabled => MasterEnabled == null || MasterEnabled.Value;

        private readonly Harmony _harmony = new Harmony(PluginGuid);

        private void Awake()
        {
            Logger = base.Logger;

            MasterEnabled = Config.Bind("General", "Enabled", true,
                "Enable/disable VR controller position adjustment. OFF = vanilla behavior. " +
                "When turned ON during an H scene, re-enter the H scene to take effect.");
            F1MoveScale = Config.Bind("Female 1", "Move Scale", 1.0f, "Movement scale for Female1");
            F2MoveScale = Config.Bind("Female 2", "Move Scale", 1.0f, "Movement scale for Female2");

            // OFF にした瞬間、掛かり中の一時オフセットを戻す（後始末なので Enabled でゲートしない）
            MasterEnabled.SettingChanged += (s, e) =>
            {
                if (MasterEnabled != null && !MasterEnabled.Value)
                {
                    VRInput.OnDisabled();
                    HCharaAdjustApi.ClearTransientOffsets();
                }
            };

            _harmony.PatchAll(typeof(Hooks));
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded");
        }

        private void Update()
        {
            if (!IsEnabled) return;
            VRInput.Tick();
        }
    }
}
