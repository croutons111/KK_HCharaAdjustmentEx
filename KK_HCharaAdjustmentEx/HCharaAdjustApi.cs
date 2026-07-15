using System.Collections.Generic;
using IllusionUtility.GetUtility;
using UnityEngine;

namespace KK_HCharaAdjustmentEx
{
    /// <summary>
    /// 外部プラグイン連携用 API（例: KK_Rankou のゲスト補正）。
    /// v1.12 で自動補正は「帯域方式の参照アライメント」に一本化され、スケール比 comp は
    /// 撤去された。本 API の comp 系は互換のため残置し常に false を返す。
    /// 【ApiVersion 4】TryComputeAdjustment は本シーンと同じ排他優先順:
    ///   完全一致キーの手動保存（絶対デルタ・手動が大正義）＞ 自動補正（帯域シード）＞ ゼロ。
    /// 自動補正は解析シード（一次近似）のワンショット計算＝帯域内キャラは完全にゼロ。
    /// 【ApiVersion 5】一時オフセット（AddTransientOffset 系）を追加。本シーンの継続適用の
    ///   目標位置へ加算される非永続の微調整枠（KK_HCharaAdjustmentEx.VR が使用）。
    /// </summary>
    public static class HCharaAdjustApi
    {
        public const int ApiVersion = 5;

        private class Bones
        {
            public Transform? hips;
            public Transform? height;
            public bool       failed;   // ボーン欠落（再試行しない・警告は1回だけ）
        }

        private static readonly Dictionary<ChaControl, Bones> _bones =
            new Dictionary<ChaControl, Bones>();

        /// <summary>【v1.12 で撤去】スケール比 comp は参照アライメントに置換された。常に false（計算不能）。</summary>
        public static bool TryComputeBodyComp(ChaControl female, ChaControl male, int mode, out Vector3 compWorld)
        { compWorld = Vector3.zero; return false; }

        /// <summary>【v1.12 で撤去】常に false。</summary>
        public static bool TryComputeBodyComp(ChaControl female, ChaControl male, float strength, out Vector3 compWorld)
        { compWorld = Vector3.zero; return false; }

        /// <summary>
        /// 【ApiVersion 4】外部ペア（HSceneProc 外）に足すべきワールドオフセットを返す。
        /// 本シーンと同じ排他優先順（v1.6.0「手動が大正義」・合算しない）:
        ///   1. 完全一致キーの手動保存あり → 絶対デルタのみ（女・男とも。自動補正は加えない）
        ///   2. なし → 女性のみ自動補正（帯域外キャラの解析シード。帯域内・対象外モードはゼロ）
        /// info はゲーム本体 lstAnimInfo の**同一インスタンス**を渡すこと（MOD体位の同定が参照比較のため）。
        /// female/male とも各キャラの現在ルート回転でワールド化済み＝そのままルート位置に加算してよい。
        /// false = プラグイン無効等で全く計算できない。
        /// </summary>
        public static bool TryComputeAdjustment(
            ChaControl female, ChaControl male, int mode,
            HSceneProc.AnimationListInfo info,
            out Vector3 femaleOffsetWorld, out Vector3 maleOffsetWorld)
        {
            femaleOffsetWorld = Vector3.zero;
            maleOffsetWorld   = Vector3.zero;
            if (!Plugin.IsEnabled || female == null) return false;
            if (info == null) return true;
            try
            {
                string key  = HSceneHooks.BuildKeyFor(info, female, null, male);
                var    src  = AdjustmentStore.GetEntry(key);
                Vector3 absF = HSceneHooks.GetAbs(src, 0);
                Vector3 absM = HSceneHooks.GetAbs(src, 2);
                if (absF != Vector3.zero || absM != Vector3.zero)
                {
                    // 手動保存＝保存した見た目そのものを再現（自動補正は参照しない）
                    femaleOffsetWorld = female.transform.rotation * absF;
                    if (male != null && absM != Vector3.zero)
                        maleOffsetWorld = male.transform.rotation * absM;
                    Plugin.Logger.LogInfo(string.Format(
                        "[HCharaAdjustmentEx] API手動適用(完全一致・絶対デルタ): {0} absF=({1:F3},{2:F3},{3:F3}) absM=({4:F3},{5:F3},{6:F3})",
                        src!.key, absF.x, absF.y, absF.z, absM.x, absM.y, absM.z));
                }
                else
                {
                    // 手動保存なし → 自動補正（帯域外の女性のみ・男は基準側でゼロ）
                    femaleOffsetWorld = RefAlign.ComputeExternalShiftWorld(female, male, info);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogWarning(
                    "[HCharaAdjustmentEx] API補正解決失敗 → 補正なしで続行（要調査）: " + e.Message);
                femaleOffsetWorld = Vector3.zero;
                maleOffsetWorld   = Vector3.zero;
            }
            return true;
        }

        /// <summary>
        /// 【ApiVersion 5】本シーン（H シーン中）のキャラに一時オフセットを加算する。
        /// idx: 0=Female1 / 1=Female2 / 2=Male。worldDelta はワールド座標の差分。
        /// 継続適用（毎 LateUpdate の位置強制）の目標位置へ加算されるため、呼び出し側は
        /// transform を直接動かさないこと（綱引きになる）。
        /// 保存されず、体位変更・スポット移動・H シーン終了で自動的にゼロへ戻る。
        /// </summary>
        public static void AddTransientOffset(int idx, Vector3 worldDelta)
        {
            if (!Plugin.IsEnabled) return;
            HSceneHooks.AddTransientOffset(idx, worldDelta);
        }

        /// <summary>現在の一時オフセット（ワールド）。リセット済み・範囲外はゼロ。</summary>
        public static Vector3 GetTransientOffset(int idx) => HSceneHooks.GetTransientOffset(idx);

        /// <summary>全キャラの一時オフセットを即時ゼロへ戻す（呼び出し側プラグインの無効化時など）。</summary>
        public static void ClearTransientOffsets() => HSceneHooks.ClearTransientOffsets();

        /// <summary>cf_n_height の実効スケール（キャラルートのスケールで正規化）。</summary>
        public static bool TryGetScale(ChaControl cha, out float scale)
        {
            scale = 1f;
            if (cha == null) return false;
            if (!Resolve(cha, out var b)) return false;
            float root = cha.transform.lossyScale.y;
            if (root <= 0f) root = 1f;
            scale = b.height!.lossyScale.y / root;
            return scale >= 0.01f;
        }

        private static bool Resolve(ChaControl cha, out Bones bones)
        {
            if (_bones.TryGetValue(cha, out bones))
            {
                if (bones.failed) return false;
                if (bones.hips != null && bones.height != null) return true;
                _bones.Remove(cha);   // 破棄されていたら取り直し
            }
            Sweep();

            bones = new Bones();
            var body = cha.objBodyBone;
            if (body != null)
            {
                var hips   = body.transform.FindLoop("cf_j_hips");
                var height = body.transform.FindLoop("cf_n_height");
                bones.hips   = hips   != null ? hips.transform   : null;
                bones.height = height != null ? height.transform : null;
            }
            if (bones.hips == null || bones.height == null)
            {
                bones.failed = true;
                Plugin.Logger.LogWarning(
                    "[HCharaAdjustmentEx] API: cf_j_hips / cf_n_height が見つからない → スケール測定不能（要調査） cha=" +
                    (cha.chaFile?.parameter?.fullname ?? cha.name ?? "?"));
            }
            _bones[cha] = bones;
            return !bones.failed;
        }

        // 破棄済み ChaControl のキャッシュ掃除（Unity の == で破棄判定）
        private static void Sweep()
        {
            if (_bones.Count < 8) return;
            List<ChaControl>? dead = null;
            foreach (var kv in _bones)
            {
                if (kv.Key != null && (kv.Value.failed || kv.Value.hips != null)) continue;
                // kv.Key はフェイクnull（破棄済み）でも C# 参照としては有効
                (dead ?? (dead = new List<ChaControl>())).Add(kv.Key!);
            }
            if (dead != null)
                foreach (var d in dead) _bones.Remove(d);
        }
    }
}
