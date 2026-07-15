using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.VR;

namespace KK_HCharaAdjustmentEx.VR
{
    // 右コントローラー A ボタンの入力ステートマシン（旧 KK_HCharaPosVR の Controller.cs を移植）。
    // transform は直接触らず、移動デルタを本体の一時オフセット（HCharaAdjustApi）へ渡すだけ。
    // 本体が LateUpdate で「(手動保存 or 自動補正) ＋ 一時オフセット」を強制適用するため、
    // 旧 PosVR で起きていた本体との綱引き（引き戻し）は構造的に発生しない。
    internal static class VRInput
    {
        // idx は本体と共通（0=Female1 / 1=Female2）
        private const int F1 = 0, F2 = 1;

        private static VRHScene? _scene;
        private static bool _female2Exists;
        private static int  _activeTarget = F1;

        private enum Phase { Idle, PressStarted, WaitSecondTap, Moving }
        private static Phase   _phase = Phase.Idle;
        private static float   _phaseTime;
        private static bool    _prevPress;
        private static Vector3 _lastHandPos;

        // 長押し判定: 200ms 以上でリピート移動モード、400ms 以内の2回タップで対象切り替え
        private const float HoldThreshold   = 0.20f;
        private const float DoubleTapWindow = 0.40f;

        // VRHScene → VRViveCameraManager → VRViveControllerManager → lstController → device
        private static readonly FieldInfo _fManagerVR = AccessTools.Field(typeof(VRHScene),                "managerVR");
        private static readonly FieldInfo _fCtrlMgr   = AccessTools.Field(typeof(VRViveCameraManager),    "scrControllerManager");
        private static readonly FieldInfo _fLstCtrl   = AccessTools.Field(typeof(VRViveControllerManager),"lstController");
        private static readonly FieldInfo _fDevice    = AccessTools.Field(typeof(VRViveController),       "device");

        internal static void OnSceneStart(VRHScene scene)
        {
            _scene         = scene;
            _female2Exists = scene.flags != null && scene.flags.lstHeroine != null &&
                             scene.flags.lstHeroine.Count > 1;
            _activeTarget  = F1;
            _phase         = Phase.Idle;
            _prevPress     = false;
        }

        internal static void OnDisabled()
        {
            _phase     = Phase.Idle;
            _prevPress = false;
        }

        internal static void Tick()
        {
            // シーン終了で VRHScene は破棄される → Unity の == null（破棄判定込み）で停止
            if (_scene == null) return;
            if (!VRDevice.isPresent) return;

            bool  pressed = IsAButtonPressed();
            float now     = Time.time;

            switch (_phase)
            {
                case Phase.Idle:
                    if (pressed && !_prevPress)
                    {
                        _phaseTime = now;
                        _phase     = Phase.PressStarted;
                    }
                    break;

                case Phase.PressStarted:
                    if (!pressed)
                    {
                        // 短く離した → 1回目タップ確定、2回目待ち
                        _phaseTime = now;
                        _phase     = Phase.WaitSecondTap;
                    }
                    else if (now - _phaseTime >= HoldThreshold)
                    {
                        // 長押し → 移動モード開始
                        _lastHandPos = GetRightHandPos();
                        _phase       = Phase.Moving;
                    }
                    break;

                case Phase.WaitSecondTap:
                    if (pressed && !_prevPress)
                    {
                        // 2回目タップ → Female1 ↔ Female2 切り替え
                        if (_female2Exists)
                        {
                            _activeTarget = _activeTarget == F1 ? F2 : F1;
                            TriggerHaptic();
                        }
                        _phase = Phase.Idle;
                    }
                    else if (now - _phaseTime > DoubleTapWindow)
                    {
                        _phase = Phase.Idle;   // タイムアウト → キャンセル
                    }
                    break;

                case Phase.Moving:
                    var handPos = GetRightHandPos();
                    if (pressed)
                    {
                        Vector3 delta = handPos - _lastHandPos;
                        if (delta != Vector3.zero)
                        {
                            float scale = _activeTarget == F2
                                ? (Plugin.F2MoveScale != null ? Plugin.F2MoveScale.Value : 1f)
                                : (Plugin.F1MoveScale != null ? Plugin.F1MoveScale.Value : 1f);
                            var cam = Camera.main;
                            Quaternion rot = cam != null ? cam.transform.rotation : Quaternion.identity;
                            HCharaAdjustApi.AddTransientOffset(_activeTarget, rot * (delta * scale));
                        }
                    }
                    else
                    {
                        _phase = Phase.Idle;   // 離した → 位置確定（オフセットは本体側に残る）
                    }
                    _lastHandPos = handPos;
                    break;
            }

            _prevPress = pressed;
        }

        // 右コントローラー固定・A ボタン固定（mask = 1<<7 = 0x80）
        private static bool IsAButtonPressed()
        {
            var dev = RightDevice();
            return dev != null && dev.GetPress(0x80UL);
        }

        private static void TriggerHaptic() => RightDevice()?.TriggerHapticPulse(2000);

        private static SteamVR_Controller.Device? RightDevice()
        {
            var scene = _scene;
            if (scene == null) return null;

            var managerVR = _fManagerVR.GetValue(scene) as VRViveCameraManager;
            if (managerVR == null) return null;

            var ctrlMgr = _fCtrlMgr.GetValue(managerVR) as VRViveControllerManager;
            if (ctrlMgr == null) return null;

            var lstCtrl = _fLstCtrl.GetValue(ctrlMgr) as List<VRViveController>;
            if (lstCtrl == null || lstCtrl.Count < 2) return null;

            return _fDevice.GetValue(lstCtrl[1]) as SteamVR_Controller.Device;
        }

        private static Vector3 GetRightHandPos() =>
            InputTracking.GetLocalPosition(VRNode.RightHand);
    }
}
