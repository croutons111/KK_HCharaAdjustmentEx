using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;

namespace KK_HCharaAdjustmentEx
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid    = "KK_HCharaAdjustmentEx";
        public const string PluginName    = "KK_HCharaAdjustmentEx";
        public const string PluginVersion = "1.20.4";

        internal static new ManualLogSource Logger = null!;
        internal static Plugin Instance = null!;   // RefAlign のコルーチンホスト

        // 全機能の有効/無効（全プラグイン共通ルール）
        internal static ConfigEntry<bool>? MasterEnabled;
        public   static bool IsEnabled => MasterEnabled == null || MasterEnabled.Value;

        // 自動補正（Auto Adjust）: 参照アライメント。正規スライダー範囲外の体を正規端の参照に整列
        // 非VR/VR は同一 cfg を共有するため、モード別トグルは別項目にして IsVRMode で選択する
        internal static ConfigEntry<bool>? RefAlignEnabled;      // 非VR
        internal static ConfigEntry<bool>? RefAlignEnabledVR;    // VR
        internal static ConfigEntry<bool>? PreciseSampling;      // 非VR（既定ON）
        internal static ConfigEntry<bool>? PreciseSamplingVR;    // VR（既定OFF＝コスト対策）
        internal static ConfigEntry<bool>? ShiftCapEnabled;
        internal static ConfigEntry<float>? MouthShiftScale;

        // 手動調整（Manual Adjust）: 画面ボタン表示・体位変更時の自動保存・保存/リセット ホットキー
        internal static ConfigEntry<bool>? ShowButtons;
        internal static ConfigEntry<bool>? AutoSaveOnPoseChange;
        internal static ConfigEntry<KeyboardShortcut>? SaveKey;
        internal static ConfigEntry<KeyboardShortcut>? ResetKey;


        // 保存通知
        private static string _saveMessage      = "";
        private static float  _saveMessageUntil = 0f;

        // H シーン中の状態（Hooks から書き込む）
        internal static HFlag?      CurrentFlags;
        internal static HSceneProc? CurrentHSceneProc;
        internal static bool        IsVRMode;

        private readonly Harmony _harmony = new Harmony(PluginGuid);

        private void Awake()
        {
            Logger   = base.Logger;
            Instance = this;

            MasterEnabled = Config.Bind("General", "Enabled", true,
                "Enable/disable the whole plugin (OFF = vanilla behavior).");
            RefAlignEnabled = Config.Bind("Auto Adjust", "Enabled", true,
                "Enable/disable automatic position adjustment (desktop / non-VR).");
            RefAlignEnabledVR = Config.Bind("Auto Adjust", "Enabled (VR)", true,
                "Enable/disable automatic position adjustment in VR.");
            PreciseSampling = Config.Bind("Auto Adjust", "Precise Sampling", true,
                "Refine the automatic adjustment by measuring a hidden reference body (desktop / non-VR). " +
                "OFF = use the fast analytic approximation only.");
            PreciseSamplingVR = Config.Bind("Auto Adjust", "Precise Sampling (VR)", false,
                "Same as Precise Sampling, but for VR. Default OFF: loading and animating the reference body " +
                "costs CPU time and can cause frame drops in VR. The fast approximation is used instead.");

            ShiftCapEnabled = Config.Bind("Auto Adjust", "Shift Cap", false,
                "Cap the automatic adjustment to avoid over-correction (prevents floating / separation).");

            MouthShiftScale = Config.Bind("Auto Adjust", "Mouth Shift Scale", 0.8f,
                new ConfigDescription(
                    "Strength of mouth alignment (0-1, lower = subtler).",
                    new AcceptableValueRange<float>(0f, 1f)));

            ShowButtons = Config.Bind("Manual Adjust", "Buttons Show", true,
                "Show on-screen Save/Reset buttons while a guide is displayed.");

            AutoSaveOnPoseChange = Config.Bind("Manual Adjust", "Position Auto Save", true,
                "Automatically save a moved position when the pose changes.");

            SaveKey = Config.Bind("Manual Adjust", "Position Save",
                new KeyboardShortcut(KeyCode.S, KeyCode.RightControl),
                "Key to save the manually adjusted position.");
            ResetKey = Config.Bind("Manual Adjust", "Position Reset",
                new KeyboardShortcut(KeyCode.S, KeyCode.RightControl, KeyCode.RightShift),
                "Key to reset the manual adjustment (back to auto/vanilla).");

            AdjustmentStore.Initialize(Paths.ConfigPath);

            IsVRMode = Type.GetType("VRHScene, Assembly-CSharp") != null;

            _harmony.PatchAll(typeof(HSceneHooks));

            if (IsVRMode)
                PatchVRScene();

            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded  VR={IsVRMode}");
        }

        // VRHScene を実行時リフレクションでパッチ
        private void PatchVRScene()
        {
            var vrType = Type.GetType("VRHScene, Assembly-CSharp");
            if (vrType == null) return;

            PatchVR(vrType, "MapSameObjectDisable",
                nameof(HSceneHooks.VRMapSameObjectDisablePostfix), isPrefix: false);
            PatchVR(vrType, "ChangeCategory",
                nameof(HSceneHooks.VRChangeCategoryPrefix), isPrefix: true);
            PatchVR(vrType, "SetMapObject",
                nameof(HSceneHooks.VRSetMapObjectPrefix), isPrefix: true);

            Logger.LogInfo("[HCharaAdjustmentEx] VR patch applied");
        }

        private void PatchVR(Type vrType, string method, string hook, bool isPrefix)
        {
            var target = AccessTools.Method(vrType, method);
            if (target == null)
            {
                Logger.LogWarning("[HCharaAdjustmentEx] VRHScene." + method + " not found");
                return;
            }
            var hm = new HarmonyMethod(
                typeof(HSceneHooks).GetMethod(hook, BindingFlags.Static | BindingFlags.NonPublic));
            if (isPrefix) _harmony.Patch(target, prefix: hm);
            else          _harmony.Patch(target, postfix: hm);
        }

        // 毎フレーム、保存値どおりに位置を強制し続ける（継続適用）
        private void LateUpdate()
        {
            if (!IsEnabled || CurrentFlags == null) return;
            HSceneHooks.EnforceAndSave();
            // 同時採取のフレーム測定はこの位相（実キャラの IK/首 LateUpdate より前＝素のアニメ姿勢）で行う。
            // コルーチン（Update 相）で読むと前フレームの IK 適用後の姿勢になり、IK で世界固定される
            // 結合点（椅子に着く手等）が体格と無関係な値になって比較が壊れる（実測 2026-07-12）。
            RefAlign.HookMeasureTick();
        }

        // 手動保存/リセット ホットキー
        private void Update()
        {
            if (!IsEnabled || CurrentFlags == null) return;
            // リセット（RCtrl+RShift+S）を先に判定（RCtrl+S を包含するため）
            if (ResetKey != null && ResetKey.Value.IsDown())
                HSceneHooks.ResetManualFromKey();
            else if (SaveKey != null && SaveKey.Value.IsDown())
                HSceneHooks.SaveManualFromGuide();
        }

        internal static void ShowSaved(string label) =>
            ShowMessage("Position saved (" + label + ")");

        internal static void ShowMessage(string msg)
        {
            _saveMessage      = msg;
            _saveMessageUntil = Time.time + 3f;
        }

        private void OnGUI()
        {
            if (!IsEnabled) return;

            DrawButtons();

            if (string.IsNullOrEmpty(_saveMessage) || Time.time > _saveMessageUntil) return;

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 20,
                alignment = TextAnchor.LowerLeft
            };

            float pad = 10f;
            float h   = 30f;
            var rect  = new Rect(pad, Screen.height - h - pad, Screen.width - pad * 2, h);
            DrawLabelWithOutline(rect, _saveMessage, style, Color.white, Color.black);
        }

        // ガイド表示中（O/P/I）だけ Save/Reset ボタンを画面上中央に描画（非VR・Config でON/OFF のみ）
        private void DrawButtons()
        {
            if (IsVRMode) return;
            if (ShowButtons == null || !ShowButtons.Value) return;
            if (CurrentFlags == null || !HSceneHooks.AnyGuideShown()) return;

            const float w = 90f, h = 30f, gap = 8f, top = 10f;
            float panelW = w * 2 + gap;
            float x = (Screen.width - panelW) * 0.5f;   // 上中央

            var style = new GUIStyle(GUI.skin.button) { fontSize = 16 };
            if (GUI.Button(new Rect(x, top, w, h), "Save", style))
                HSceneHooks.SaveManualFromGuide();
            if (GUI.Button(new Rect(x + w + gap, top, w, h), "Reset", style))
                HSceneHooks.ResetManualFromKey();
        }

        private static void DrawLabelWithOutline(Rect rect, string text, GUIStyle style, Color textColor, Color outlineColor)
        {
            style.normal.textColor = outlineColor;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0)
                        GUI.Label(new Rect(rect.x + dx, rect.y + dy, rect.width, rect.height), text, style);
            style.normal.textColor = textColor;
            GUI.Label(rect, text, style);
        }
    }
}
