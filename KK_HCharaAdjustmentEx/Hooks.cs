using HarmonyLib;
using HSceneUtility;
using IllusionUtility.GetUtility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace KK_HCharaAdjustmentEx
{
    // 継続適用方式:
    //   ・ChaControl のルート(transform.position)を毎 LateUpdate で「保存値どおり」に強制し続ける。
    //   ・スポットのバニラルートは「シーン開始 / スポット移動(ChangeCategory)」の直後にだけ取得。
    //   ・ガイドを掴んでいる間(isDrag)は強制せずユーザー操作に任せ、離した瞬間に保存。
    internal static class HSceneHooks
    {
        // キャラindex: 0=Female1, 1=Female2, 2=Male
        private const int F1 = 0, F2 = 1, MALE = 2;

        // 補正適用のスムージング（瞬間移動回避）。exp 平滑・大移動(スポット移動等)はスナップ。
        private const float SmoothRate     = 12f;    // 収束レート（大きいほど速い・~95% を 3/rate 秒で）
        private const float SmoothSnapDist = 0.5f;   // これ超の移動は瞬間（スポット移動・大きな手動配置）

        private static object? _lastHSceneInstance;

        // スポットのバニラルート（index別）
        private static readonly Vector3[]    _vanillaRoot = new Vector3[3];
        private static readonly Quaternion[] _vanillaRot  = { Quaternion.identity, Quaternion.identity, Quaternion.identity };
        // 次の LateUpdate でバニラルートを取り直す（シーン開始/スポット移動で立てる）
        private static bool _captureVanillaNext = true;

        // 体位変更時の自動保存: この体位でドラッグしたキャラ（index別）と、その絶対デルタ・残差メタ。
        // ガイド表示中の isDrag を観測して記録し、体位変更/シーン終了で旧キーへ SaveComponent する。
        private static string?   _prevKey;                  // 前フレームの体位キー（体位変更検知）
        private static int       _prevPosture = -1;         // _prevKey の posture
        private static readonly bool[]    _draggedPose = new bool[3];
        private static readonly Vector3[] _dragAbs     = new Vector3[3];
        private static Vector3   _dragRefShiftF1, _dragRefShiftF2;  // ドラッグ時の F1/F2 自動補正シフト（残差再構成メタ）

        // 派生元コントローラ型・GuideObjectフィールドのリフレクションキャッシュ
        private static Type?      _ctrlType;
        private static FieldInfo? _guideField;
        private static bool       _ctrlResolved;

        // AnimationLoader(MOD体位)の SwapAnimationMapping 参照キャッシュ
        private static bool         _alChecked;
        private static PropertyInfo? _piSwapMapping;
        private static FieldInfo?    _fiCtrlF, _fiCtrlF1;

        // ─── 体格補正（スケール比による解析的補正） ─────────────────────────
        // アニメの腰の軌道（クリップの hips 平行移動）は全キャラ共通で、
        // cf_n_height のスケール s が掛かって骨盤位置になる。
        // (s0/s − 1)×(骨盤 − ルート) を root に足すと骨盤がベース体格(s0)の軌道に乗る。
        private static readonly ChaControl?[] _boneCha    = new ChaControl?[3];
        private static readonly Transform?[]  _hipsTrf    = new Transform?[3];
        private static readonly Transform?[]  _heightTrf  = new Transform?[3];
        private static readonly bool[]        _boneWarned = new bool[3];

        // 体位切替ごとの補正量ログ（診断用）の重複抑止
        private static readonly string[] _refShiftLogKey = { "", "", "" };

        // 直前フレームで root を強制していたか（ゼロ化した瞬間のバニラ復元用）
        private static readonly bool[] _wasEnforced = new bool[3];

        // 一時オフセット（HCharaAdjustApi 経由・VR コントローラー微調整用）。
        // 保存しない＝体位変更・スポット移動・シーン終了で必ずゼロへ戻す（VR は一人称/三人称で
        // 見えが違うため永続化しない設計。ユーザー決定 2026-07-13）。
        private static readonly Vector3[] _transientOffset = new Vector3[3];
        // 直前フレームの一時オフセット。変化中（=VR ドラッグ中）はスムージングを通さず
        // 直接代入する（平滑 12/s の遅延で「ゴム引き」の追従感になるため。実機報告 2026-07-15）。
        private static readonly Vector3[] _prevTransient = new Vector3[3];

        // ─── 非VR: Hシーン開始 ──────────────────────────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HSceneProc), "MapSameObjectDisable")]
        private static void HSceneProc_MapSameObjectDisable(HSceneProc __instance)
        {
            if (!Plugin.IsEnabled) return;
            Plugin.CurrentFlags      = __instance.flags;
            Plugin.CurrentHSceneProc = __instance;

            if (!ReferenceEquals(__instance, _lastHSceneInstance))
            {
                _lastHSceneInstance = __instance;
                ResetSceneState();
            }
        }

        // ─── スポット移動（バニラルート取り直しをマーク） ───────────────────
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HSceneProc), "ChangeCategory")]
        private static void HSceneProc_ChangeCategory_Prefix()
        {
            if (!Plugin.IsEnabled) return;
            _captureVanillaNext = true;
        }

        // ─── 椅子・机ヌルオブジェクト配置の保護 ─────────────────────────────
        // 本体 SetMapObject は椅子/机の付属オブジェクトを「女1の現在ルート位置」基準で配置する。
        // 参照整列のシフトが乗ったままだと家具ごと浮くため、配置の直前だけルートをバニラへ戻す
        //（配置後は次の LateUpdate の強制適用がシフトを再適用。家具は再アンカーされないので浮かない）。
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HSceneProc), "SetMapObject")]
        private static void HSceneProc_SetMapObject_Prefix()
        {
            if (!Plugin.IsEnabled) return;
            UnshiftFemaleRootForMapObject();
        }

        private static void UnshiftFemaleRootForMapObject()
        {
            var flags = Plugin.CurrentFlags;
            if (flags == null) return;
            var cha = GetCha(flags, F1);
            if (cha == null) return;
            if (!_wasEnforced[F1]) return;                     // シフト未適用なら何もしない
            Vector3 d = cha.transform.position - _vanillaRoot[F1];
            if (d.sqrMagnitude < 1e-8f) return;                // 実質バニラ位置
            if (d.magnitude > 0.6f) return;                    // スポット移動直後（バニラ記録が古い）は触らない
            cha.transform.position = _vanillaRoot[F1];
            Plugin.Logger.LogDebug(string.Format(
                "[HCharaAdjustmentEx] SetMapObject 前にシフト解除（家具アンカー保護）d=({0:F3},{1:F3},{2:F3})",
                d.x, d.y, d.z));
        }

        // ─── 体位アイテム（ノート・本等）のアンカー保護 ─────────────────────
        // ItemObject.SetTransform は独立配置アイテムを「女1の objTop 位置」へ毎フレーム再アンカーする。
        // 参照整列のシフトに追従してアイテムが浮くため、アンカー座標からシフト分を差し引く。
        // ボーン親子付けアイテム（手持ち等）は SetTransform の対象外＝影響なし。
        // ItemObject は VR でも共用クラスなので、このパッチで VR も自動カバー。
        private static Transform? _itemAnchorDummy;   // Transform 版の引数差し替え用ダミー

        // 現在の女1シフト（家具/アイテムのアンカー補正量）。ゼロ or 信頼できない場合は Vector3.zero。
        private static Vector3 CurrentF1Shift()
        {
            var flags = Plugin.CurrentFlags;
            if (flags == null) return Vector3.zero;
            var cha = GetCha(flags, F1);
            if (cha == null || !_wasEnforced[F1]) return Vector3.zero;
            Vector3 d = cha.transform.position - _vanillaRoot[F1];
            if (d.sqrMagnitude < 1e-8f || d.magnitude > 0.6f) return Vector3.zero;
            return d;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemObject), "SetTransform", typeof(Transform))]
        private static void ItemObject_SetTransform_Prefix(ref Transform _transform)
        {
            if (!Plugin.IsEnabled || _transform == null) return;
            Vector3 d = CurrentF1Shift();
            if (d == Vector3.zero) return;
            if (_itemAnchorDummy == null)
            {
                var go = new GameObject("HCAEx_ItemAnchor");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _itemAnchorDummy = go.transform;
            }
            _itemAnchorDummy.SetPositionAndRotation(_transform.position - d, _transform.rotation);
            _transform = _itemAnchorDummy;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemObject), "SetTransform", typeof(Vector3), typeof(Vector3))]
        private static void ItemObject_SetTransform_Prefix2(ref Vector3 _pos)
        {
            if (!Plugin.IsEnabled) return;
            _pos -= CurrentF1Shift();
        }

        internal static void VRChangeCategoryPrefix()
        {
            if (!Plugin.IsEnabled) return;
            _captureVanillaNext = true;
        }

        internal static void VRSetMapObjectPrefix()
        {
            if (!Plugin.IsEnabled) return;
            UnshiftFemaleRootForMapObject();
        }

        // ─── Hシーン終了 ─────────────────────────────────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HSceneProc), "EndProc")]
        private static void HSceneProc_EndProc()
        {
            if (_prevKey != null) FlushAutoSave(_prevKey, _prevPosture);   // 最後の体位のドラッグを保存
            Plugin.CurrentFlags      = null;
            Plugin.CurrentHSceneProc = null;
            _lastHSceneInstance      = null;
            ResetSceneState();
            RefAlign.OnSceneEnd();   // 参照ボディ破棄（シーン所属のため）
        }

        // VR: Hシーン開始（CurrentFlags 設定 + バニラ取り直しマーク）
        internal static void VRMapSameObjectDisablePostfix(object __instance)
        {
            if (!Plugin.IsEnabled) return;
            try
            {
                var flags = GetFlags(__instance);
                if (flags == null) return;
                Plugin.CurrentFlags = flags;

                if (!ReferenceEquals(__instance, _lastHSceneInstance))
                {
                    _lastHSceneInstance = __instance;
                    ResetSceneState();
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[HCharaAdjustmentEx] VR init error: " + e.Message);
            }
        }

        private static void ResetSceneState()
        {
            _captureVanillaNext = true;
            _prevKey     = null;
            _prevPosture = -1;
            for (int i = 0; i < 3; i++)
            {
                _boneCha[i]           = null;
                _hipsTrf[i]           = null;
                _heightTrf[i]         = null;
                _refShiftLogKey[i]        = "";
                _wasEnforced[i]       = false;
                _draggedPose[i]       = false;
                _transientOffset[i]   = Vector3.zero;
            }
            RefAlign.OnSceneReset();
        }

        // ── 一時オフセット（HCharaAdjustApi から） ──
        internal static void AddTransientOffset(int idx, Vector3 worldDelta)
        { if (idx >= 0 && idx < 3) _transientOffset[idx] += worldDelta; }
        internal static Vector3 GetTransientOffset(int idx)
            => idx >= 0 && idx < 3 ? _transientOffset[idx] : Vector3.zero;
        internal static void ClearTransientOffsets()
        { for (int i = 0; i < 3; i++) _transientOffset[i] = Vector3.zero; }

        // RefAlign から使う公開アクセサ
        internal static bool TryParseKeyPublic(string key, out string anim, out string cards)
            => AdjustmentStore.TryParseKey(key, out anim, out cards);
        internal static Vector3    VanillaRootOf(int idx) => _vanillaRoot[idx];
        internal static Quaternion VanillaRotOf(int idx)  => _vanillaRot[idx];
        internal static ChaControl? MaleOf(HFlag flags)   => GetCha(flags, MALE);

        private static HFlag? GetFlags(object instance)
        {
            var field = instance.GetType().GetField("flags", BindingFlags.Instance | BindingFlags.Public);
            return field?.GetValue(instance) as HFlag;
        }

        // ─── 毎 LateUpdate（Plugin.LateUpdate から呼ばれる） ─────────────────

        internal static void EnforceAndSave()
        {
            var flags = Plugin.CurrentFlags;
            if (flags == null) return;

            ResolveController();

            if (_captureVanillaNext)
            {
                CaptureVanilla(flags);
                _captureVanillaNext = false;
            }

            string key = BuildKey(flags);

            // exact = 完全一致キー（絶対デルタ＝手動位置そのもの。自動補正を参照しない）
            var exact = AdjustmentStore.GetEntry(key);

            // ── 体位変更を検知したら、旧体位でドラッグしたキャラを自動保存 ──
            if (_prevKey != null && key != _prevKey)
            {
                FlushAutoSave(_prevKey, _prevPosture);
                ClearTransientOffsets();   // VR 微調整は体位ごと（持ち越さない・保存しない）
            }
            _prevKey     = key;
            _prevPosture = GetPosture(flags);

            for (int idx = 0; idx < 3; idx++)
            {
                var cha = GetCha(flags, idx);
                if (cha == null) continue;

                var  guide = GetGuide(cha);
                bool shown = guide != null && guide.gameObject.activeInHierarchy;

                // ── ガイド表示中＝調整モード：当方は位置を触らない（派生元/ユーザーが掴んで動かす）。
                //    ドラッグ実績と現在位置を記録し、体位変更時に自動保存する（Config）。
                //    RCtrl+S / 画面ボタンでも明示的に保存できる ──
                if (shown)
                {
                    if (guide!.isDrag) _draggedPose[idx] = true;
                    if (_draggedPose[idx])
                    {
                        // ドラッグ後は毎フレーム現在位置を追跡（離した後の最終位置を拾う）
                        _dragAbs[idx] = Quaternion.Inverse(_vanillaRot[idx]) *
                                        (cha.transform.position - _vanillaRoot[idx]);
                        _dragRefShiftF1 = RefShiftLocal(flags, F1, key);
                        _dragRefShiftF2 = RefShiftLocal(flags, F2, key);
                    }
                    continue;
                }

                // 自動補正 = 帯域方式の参照アライメント（RefAlign）のみ（v1.12 で従来 comp・推定・机クランプを撤去）
                Vector3 refShift = RefAlign.Enabled
                    ? RefAlign.GetShift(flags, cha, idx, key)
                    : Vector3.zero;

                // ── ガイド非表示中＝適用（瞬間移動を避けスムーズに寄せる） ──
                Vector3 abs   = GetAbs(exact, idx);
                Vector3 trans = _transientOffset[idx];

                // 体位切替ごとに「何が効いているか」を1回ログ（診断用）。
                // ★v1.20.4: 手動保存が効いているときの行を追加した。従来は refAlign の行しか無く、
                //   しかもそれは**手動保存に上書きされても関係なく出る**ため、ログだけでは
                //   「保存が読み戻されたのか自動補正なのか」を判別できなかった（外部API 経路には
                //   `API手動適用(完全一致・絶対デルタ)` があるのに本シーンだけ穴が空いていた）。
                //   排他（下の target と同じ優先順）なので、出る行は必ずどちらか一方。
                //   ※自動補正の行は文言も書式も従来のまま（既存ログの読み方を変えない）。
                if (key != _refShiftLogKey[idx])
                {
                    _refShiftLogKey[idx] = key;
                    if (abs != Vector3.zero)
                        Plugin.Logger.LogInfo(string.Format(
                            "[HCharaAdjustmentEx] 手動適用(完全一致・絶対デルタ): {0} {1} abs=({2:F3},{3:F3},{4:F3})",
                            key, Name(idx), abs.x, abs.y, abs.z)
                            + (refShift != Vector3.zero
                                ? string.Format("（自動補正 ({0:F3},{1:F3},{2:F3}) を上書き）",
                                                refShift.x, refShift.y, refShift.z)
                                : ""));
                    else if (refShift != Vector3.zero)
                        Plugin.Logger.LogInfo(string.Format(
                            "[HCharaAdjustmentEx] {0} refAlign=({1:F3},{2:F3},{3:F3})",
                            Name(idx), refShift.x, refShift.y, refShift.z));
                }

                bool active = abs != Vector3.zero || refShift != Vector3.zero || trans != Vector3.zero;
                if (!active && !_wasEnforced[idx]) continue;   // 補正なし＝何もしない

                // 目標: (完全一致の絶対デルタ（手動が大正義）＞ 自動補正 ＞ バニラ) ＋ 一時オフセット（VR微調整）
                Vector3 target = (abs != Vector3.zero      ? _vanillaRoot[idx] + _vanillaRot[idx] * abs
                               : refShift != Vector3.zero ? _vanillaRoot[idx] + refShift
                               :                            _vanillaRoot[idx]) + trans;

                Vector3 cur = cha.transform.position;
                float   dt  = Time.deltaTime;
                // 一時オフセット変化中（VR ドラッグ中）は遅延ゼロで直接追従。離すと従来のスムージングへ。
                bool transientMoving = trans != _prevTransient[idx];
                _prevTransient[idx] = trans;
                // 大移動（スポット移動・大きな手動配置）は瞬間、補正級の小移動はスムーズに。
                cha.transform.position =
                    (dt <= 0f || transientMoving ||
                     (target - cur).sqrMagnitude > SmoothSnapDist * SmoothSnapDist)
                        ? target
                        : Vector3.Lerp(cur, target, 1f - Mathf.Exp(-SmoothRate * dt));

                if (active) _wasEnforced[idx] = true;
                else if ((cha.transform.position - _vanillaRoot[idx]).sqrMagnitude < 1e-8f)
                    _wasEnforced[idx] = false;   // バニラ復帰が完了したら継続適用を止める
            }
        }

        // 体位変更/シーン終了で、旧体位でドラッグしたキャラを自動保存する（Config でON/OFF）。
        private static void FlushAutoSave(string oldKey, int oldPosture)
        {
            bool on = Plugin.AutoSaveOnPoseChange == null || Plugin.AutoSaveOnPoseChange.Value;
            for (int idx = 0; idx < 3; idx++)
            {
                if (on && _draggedPose[idx])
                {
                    AdjustmentStore.SaveComponent(oldKey, idx, _dragAbs[idx], oldPosture,
                        _dragRefShiftF1, _dragRefShiftF2);
                    Plugin.ShowSaved(Name(idx));
                }
                _draggedPose[idx] = false;
            }
        }

        // ガイド（男/女1/女2）のいずれかが表示中か（OnGUI のボタン表示要否）。
        internal static bool AnyGuideShown()
        {
            var flags = Plugin.CurrentFlags;
            if (flags == null) return false;
            for (int idx = 0; idx < 3; idx++)
            {
                var cha = GetCha(flags, idx);
                if (cha == null) continue;
                var guide = GetGuide(cha);
                if (guide != null && guide.gameObject.activeInHierarchy) return true;
            }
            return false;
        }

        // ─── 手動保存（RCtrl+S / 画面ボタン、Plugin から） ─────────────────
        // ガイド表示中（O/P/I）のキャラの現在位置を、この体位×キャラ組の
        // 完全一致キーへ「絶対デルタ」として保存する。以降その体位は手動位置に固定
        // （RefAlign を無視＝手動が大正義）。ガイド表示中は位置＝ガイド位置（掴んだ値）。
        internal static void SaveManualFromGuide()
        {
            var flags = Plugin.CurrentFlags;
            if (flags == null) return;
            string key = BuildKey(flags);

            bool any = false;
            for (int idx = 0; idx < 3; idx++)
            {
                var cha = GetCha(flags, idx);
                if (cha == null) continue;
                var guide = GetGuide(cha);
                if (guide == null || !guide.gameObject.activeInHierarchy) continue;

                Vector3 absLocal = Quaternion.Inverse(_vanillaRot[idx]) *
                                   (cha.transform.position - _vanillaRoot[idx]);
                AdjustmentStore.SaveComponent(key, idx, absLocal, GetPosture(flags),
                    RefShiftLocal(flags, F1, key),
                    RefShiftLocal(flags, F2, key));
                Plugin.ShowSaved(Name(idx));
                _draggedPose[idx] = false;   // 明示保存したので自動保存の保留は解除
                any = true;
            }
            if (!any)
                Plugin.ShowMessage("Save: no guide shown (press O/P/I to show a guide, then adjust)");
        }

        // ─── リセット（RCtrl+RShift+S、Plugin.Update から） ─────────────────
        // この体位×キャラ組の手動保存を削除し、RefAlign（自動）/バニラへ戻す。
        internal static void ResetManualFromKey()
        {
            var flags = Plugin.CurrentFlags;
            if (flags == null) return;
            string key = BuildKey(flags);
            for (int i = 0; i < 3; i++) _draggedPose[i] = false;   // 保留中のドラッグも破棄
            Plugin.ShowMessage(AdjustmentStore.ClearEntry(key)
                ? "Position reset (this pose)"
                : "Position reset: nothing saved for this pose");
        }

        // 保存瞬間の自動補正量（そのキャラの vanillaRot ローカル系・残差再構成メタ用）
        private static Vector3 RefShiftLocal(HFlag flags, int idx, string key)
        {
            var cha = GetCha(flags, idx);
            if (cha == null) return Vector3.zero;
            Vector3 refShift = RefAlign.Enabled
                ? RefAlign.GetShift(flags, cha, idx, key)
                : Vector3.zero;
            return Quaternion.Inverse(_vanillaRot[idx]) * refShift;
        }

        // エントリの保存時の自動補正量（残差再構成用）。男は常にゼロ
        private static Vector3 RefShiftAtSaveLocal(AdjustmentStore.Entry e, int idx) =>
            idx == F1 ? e.refShiftF1 : idx == F2 ? e.refShiftF2 : Vector3.zero;

        private static void CaptureVanilla(HFlag flags)
        {
            ClearTransientOffsets();   // スポット移動/シーン開始＝バニラ基準が変わる → VR 微調整もリセット
            for (int idx = 0; idx < 3; idx++)
            {
                var cha = GetCha(flags, idx);
                if (cha == null)
                {
                    _vanillaRoot[idx] = Vector3.zero;
                    _vanillaRot[idx]  = Quaternion.identity;
                    continue;
                }
                _vanillaRoot[idx] = cha.transform.position;
                _vanillaRot[idx]  = cha.transform.rotation;
                _wasEnforced[idx] = false;
                ResolveBones(cha, idx);   // 診断: 全キャラの体格スケールをシーン/スポットごとに必ず記録
            }
        }

        private static ChaControl? GetCha(HFlag flags, int idx)
        {
            switch (idx)
            {
                case F1:   return flags.lstHeroine.Count > 0 ? flags.lstHeroine[0]?.chaCtrl : null;
                case F2:   return flags.lstHeroine.Count > 1 ? flags.lstHeroine[1]?.chaCtrl : null;
                default:   return flags.player?.chaCtrl;
            }
        }

        // 絶対デルタ（保存時にユーザーが見た位置そのもの）。外部API HCharaAdjustApi からも使用。
        internal static Vector3 GetAbs(AdjustmentStore.Entry? entry, int idx)
        {
            if (entry == null) return Vector3.zero;
            return idx == F1 ? entry.f1 : idx == F2 ? entry.f2 : entry.male;
        }

        private static string Name(int idx) => idx == F1 ? "Female1" : idx == F2 ? "Female2" : "Male";

        // ─── 体格補正 ───────────────────────────────────────────────────────
        // 各キャラの cf_n_height 実スケール s（ABMX等の改変込み）を測り、
        // (s0/s − 1)×(骨盤 − ルート) をワールド系で返す。標準体形(s≈s0)はゼロ。
        // 瞬時値で計算するため採寸待ちが無く、揺れ成分には小さい係数しか掛からない。

        private static bool ResolveBones(ChaControl cha, int idx)
        {
            if (!ReferenceEquals(cha, _boneCha[idx]))
            {
                _boneCha[idx]   = cha;
                _hipsTrf[idx]   = null;
                _heightTrf[idx] = null;
                var body = cha.objBodyBone;
                if (body != null)
                {
                    var hips   = body.transform.FindLoop("cf_j_hips");
                    var height = body.transform.FindLoop("cf_n_height");
                    _hipsTrf[idx]   = hips   != null ? hips.transform   : null;
                    _heightTrf[idx] = height != null ? height.transform : null;
                }
                if (_hipsTrf[idx] == null || _heightTrf[idx] == null)
                {
                    if (!_boneWarned[idx])
                    {
                        _boneWarned[idx] = true;
                        Plugin.Logger.LogWarning("[HCharaAdjustmentEx] " + Name(idx) +
                            " の cf_j_hips / cf_n_height が見つからない → 体格補正なしで動作（要調査）");
                    }
                }
                else
                {
                    Plugin.Logger.LogInfo(string.Format(
                        "[HCharaAdjustmentEx] {0} 体格スケール s={1:F4}",
                        Name(idx), MeasureScale(cha, idx)));
                }
            }
            return _hipsTrf[idx] != null && _heightTrf[idx] != null;
        }

        // cf_n_height の実効スケール（キャラルートのスケールで正規化・診断ログ用）
        private static float MeasureScale(ChaControl cha, int idx)
        {
            float rootScale = cha.transform.lossyScale.y;
            if (rootScale <= 0f) rootScale = 1f;
            return _heightTrf[idx]!.lossyScale.y / rootScale;
        }

        // 残差 = 絶対デルタ − 保存時の自動補正シフト。未調整（絶対デルタゼロ）は情報なし＝ゼロのまま
        // （外部API HCharaAdjustApi からも使用。idx: 0=f1 1=f2 2=male）
        internal static Vector3 ResidualOf(AdjustmentStore.Entry e, int idx)
        {
            Vector3 abs = GetAbs(e, idx);
            if (abs == Vector3.zero) return Vector3.zero;
            return abs - RefShiftAtSaveLocal(e, idx);
        }

        // 外部API用: 任意の ChaControl 組でキーを構築（Hシーンの BuildKey と同一形式）。
        // info はゲーム本体 lstAnimInfo の同一インスタンスであること（AnimSig の swap 実体名解決が参照比較のため）。
        internal static string BuildKeyFor(HSceneProc.AnimationListInfo? info,
            ChaControl? f1, ChaControl? f2, ChaControl? male)
        {
            int    mode     = 0;
            int    animId   = 0;
            string animName = "";
            string animSig  = "none";
            try
            {
                if (info != null)
                {
                    mode     = (int)info.mode;
                    animId   = info.id;
                    animName = info.nameAnimation ?? "";
                    animSig  = AnimSig(info);
                }
            }
            catch { }
            // 男が参加しないモード（レズ/自慰）は男スロットを固定（MaleParticipates 参照）。
            // ★外部 API（KK_Rankou）はこの2モードで male に null を渡すので元から "none" になるが、
            //   将来 null 以外が渡ってもキーがぶれないよう本シーンと同じ規則をここでも掛ける。
            string maleId = MaleParticipates(mode) ? ChaCardId(male) : "none";
            return mode + "_" + animId + "_" + Sanitize(animName) + "_" + animSig + "_" +
                   ChaCardId(f1) + "_" + ChaCardId(f2) + "_" + maleId;
        }

        // ChaControl から H シーンの CardId（heroine.charFile.charaFileName）と同じ識別子を得る
        private static string ChaCardId(ChaControl? cha)
        {
            if (cha == null) return "none";
            try
            {
                return CardId(cha.chaFile?.charaFileName, cha.chaFile?.parameter?.fullname);
            }
            catch { return "none"; }
        }

        // キャラ idx の残差が得られるエントリだけで平均（未調整キャラの 0 で薄めない）
        private static Vector3 AverageResidual(List<AdjustmentStore.Entry> group, int idx)
        {
            Vector3 sum = Vector3.zero;
            int     n   = 0;
            foreach (var e in group)
            {
                if (GetAbs(e, idx) == Vector3.zero) continue;
                sum += ResidualOf(e, idx);
                n++;
            }
            return n > 0 ? sum / n : Vector3.zero;
        }

        internal static int GetPosture(HFlag flags)
        {
            try
            {
                var info = flags.nowAnimationInfo;
                return info != null ? info.posture : -1;
            }
            catch { return -1; }
        }

        // ─── 派生元ガイドの掴み検出 ─────────────────────────────────────────

        private static void ResolveController()
        {
            if (_ctrlResolved) return;
            _ctrlResolved = true;
            _ctrlType = AccessTools.TypeByName("KK_Plugins.HCharaAdjustment+HCharaAdjustmentController");
            if (_ctrlType != null)
                _guideField = AccessTools.Field(_ctrlType, "GuideObject");
            if (_ctrlType == null || _guideField == null)
                Plugin.Logger.LogWarning("[HCharaAdjustmentEx] 派生元 HCharaAdjustmentController が見つからない → 保存は無効（適用のみ）");
            else
                Plugin.Logger.LogInfo("[HCharaAdjustmentEx] 派生元ガイド連携を有効化");
        }

        private static HSceneGuideObject? GetGuide(ChaControl cha)
        {
            if (_ctrlType == null || _guideField == null) return null;
            var ctrl = cha.GetComponent(_ctrlType);
            if (ctrl == null) return null;
            return _guideField.GetValue(ctrl) as HSceneGuideObject;
        }

        // ─── キー生成 ─────────────────────────────────────────────────────

        // 男がその行為に参加するモードか。false のモードはキーの男スロットを "none" に固定する。
        //
        // ★なぜ必要か（v1.20.4・本体を追って確定）:
        //   本体 FreeH は**レズ(5)と自慰(3)で男を選ばせない**。`FreeHScene.LesbianSetup()` が結線するのは
        //   heroine/partner だけで男のカード枠自体が無く（cardMaleNormal / cardMale3P の2つのみ）、
        //   開始ボタンの成立条件も `n != 0 || resultPlayer != null` / `n != 3 || …` ＝
        //   **通常(0)と3P(3)のときしか男を要求しない**（Game_FreeHScene.cs:585）。
        //   ところが選ばれていなくても `player = member.resultPlayer.Value`（同 594）がそのまま H へ渡り、
        //   `HSceneProc` が `flags.player = flags.player ?? new SaveData.Player(...)`（Game_HSceneProc.cs:761）で
        //   無条件に埋める（null なら空 ChaFileControl／触っていれば前の選択が commonSpace の
        //   FreeHBackData 経由で持ち越される）。
        //   ＝**この2モードのキーに載る男は「そのシーンで決まった値」ではなくセッション履歴の残り**で、
        //     同じ体位・同じ女で保存しても次回は別キーになって読み出せないことがある。
        //   男は行為に参加しないのだから、キーから外すのが正しい。
        //
        // ★覗き(4)は対象外にしていない: 結合点が無く補正が一切走らない（CouplingForInfo が -1）ので
        //   キーが効く場面が無く、触っても得が無い。sonyu/houshi/aibu/3P は男が実際に参加するので当然含める。
        //   自慰から男が加わって sonyu へ遷移した場合も、そのときの mode は 2 なので正しく男が入る。
        internal static bool MaleParticipates(int mode) => mode != 3 && mode != 5;

        // 既存キーを現行規則へ正規化する（v1.20.3 以前に保存された「男入りのレズ/自慰キー」の移行用）。
        // 形式が 7 要素でない・対象モードでないものはそのまま返す。
        internal static string NormalizeKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            var p = key.Split('_');
            if (p.Length != 7) return key;                       // 想定外の形＝触らない
            if (!int.TryParse(p[0], out int mode)) return key;
            if (MaleParticipates(mode)) return key;
            if (p[6] == "none") return key;                      // 既に正規化済み
            p[6] = "none";
            return string.Join("_", p);
        }

        // mode はアニメ固有の nowAnimationInfo.mode を使う。
        // animSig（女性アニメのアセットパス）を含め、id/名前を流用したMOD体位が
        // バニラ体位とキー衝突しないようにする。
        // キー形式: {mode}_{animId}_{animName}_{animSig}_{f1card}_{f2card}_{malecard}
        internal static string BuildKey(HFlag flags)
        {
            int    mode     = 0;
            int    animId   = 0;
            string animName = "";
            string animSig  = "none";
            string f1 = "none", f2 = "none", male = "none";

            try
            {
                var info = flags.nowAnimationInfo;
                if (info != null)
                {
                    mode     = (int)info.mode;
                    animId   = info.id;
                    animName = info.nameAnimation ?? "";
                    animSig  = AnimSig(info);
                }
            }
            catch { }
            try
            {
                if (flags.lstHeroine.Count > 0)
                    f1 = CardId(flags.lstHeroine[0].charFile?.charaFileName,
                                flags.lstHeroine[0].parameter?.fullname);
                if (flags.lstHeroine.Count > 1)
                    f2 = CardId(flags.lstHeroine[1].charFile?.charaFileName,
                                flags.lstHeroine[1].parameter?.fullname);
                if (flags.player != null)
                    male = CardId(flags.player.charFile?.charaFileName,
                                  flags.player.parameter?.fullname);
            }
            catch { }

            // 男が参加しないモード（レズ/自慰）は男スロットを固定（MaleParticipates 参照）。
            // ここを通さないと `flags.player` の持ち越し次第でキーが変わり、保存が読み出せなくなる。
            if (!MaleParticipates(mode)) male = "none";

            return mode + "_" + animId + "_" + Sanitize(animName) + "_" + animSig + "_" + f1 + "_" + f2 + "_" + male;
        }

        // 体位固有の署名。アセットパスに加え、配置を決める lstCategory(カテゴリ+fileMove)・
        // posture・kindHoushi を含める。MOD体位が id/名前/アセットを流用しても、これらが
        // 異なるため衝突しない（机対面座位[MOD] と 机バック[バニラ] の区別）。
        private static string AnimSig(HSceneProc.AnimationListInfo info)
        {
            var sb = new StringBuilder();
            try { sb.Append(info.pathFemaleBase?.assetpath); sb.Append('/'); sb.Append(info.pathFemaleBase?.file); } catch { }
            try { sb.Append('|'); sb.Append(info.paramFemale?.path?.assetpath); sb.Append('/'); sb.Append(info.paramFemale?.path?.file); } catch { }
            try { sb.Append("|p"); sb.Append(info.posture); sb.Append("h"); sb.Append(info.kindHoushi); } catch { }
            try
            {
                if (info.lstCategory != null)
                    foreach (var c in info.lstCategory) { sb.Append('|'); sb.Append(c.category); sb.Append(':'); sb.Append(c.fileMove); }
            }
            catch { }
            // MOD体位は実コントローラが SwapAnimationMapping にあり、path 側は流用のまま。
            // これを引いて実体名を加える（MOD体位とバニラ体位が id/名前を流用しても区別できる）。
            try { string sw = SwapControllerName(info); if (!string.IsNullOrEmpty(sw)) { sb.Append("|sw"); sb.Append(sw); } } catch { }
            return Sanitize(sb.ToString());
        }

        // AnimationLoader.SwapAnim.SwapAnimationMapping[info] の実女性コントローラ名を返す。
        // 未導入/未登録なら ""（バニラ体位は登録されないことが多い）。
        internal static string SwapControllerName(HSceneProc.AnimationListInfo info)
        {
            if (!_alChecked)
            {
                _alChecked = true;
                var swapType = AccessTools.TypeByName("AnimationLoader.SwapAnim");
                if (swapType != null)
                    _piSwapMapping = swapType.GetProperty("SwapAnimationMapping",
                        BindingFlags.Public | BindingFlags.Static);
            }
            if (_piSwapMapping == null) return "";
            var map = _piSwapMapping.GetValue(null, null) as System.Collections.IDictionary;
            if (map == null) return "";
            foreach (System.Collections.DictionaryEntry e in map)
            {
                if (!ReferenceEquals(e.Key, info)) continue;
                var swapInfo = e.Value;
                if (swapInfo == null) return "";
                if (_fiCtrlF == null)
                {
                    _fiCtrlF  = swapInfo.GetType().GetField("ControllerFemale");
                    _fiCtrlF1 = swapInfo.GetType().GetField("ControllerFemale1");
                }
                string? cf  = _fiCtrlF?.GetValue(swapInfo)  as string;
                string? cf1 = _fiCtrlF1?.GetValue(swapInfo) as string;
                return (cf ?? "") + "/" + (cf1 ?? "");
            }
            return "";
        }

        private static string CardId(string? charaFileName, string? fullname)
        {
            if (!string.IsNullOrEmpty(charaFileName))
                return Sanitize(Path.GetFileNameWithoutExtension(charaFileName));
            return Sanitize(fullname);
        }

        private static string Sanitize(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "none";
            var sb = new StringBuilder();
            foreach (char c in s!)
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
            return sb.Length > 0 ? sb.ToString() : "none";
        }
    }
}
