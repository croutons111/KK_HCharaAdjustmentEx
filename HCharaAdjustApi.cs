using System.Collections.Generic;
using IllusionUtility.GetUtility;
using UnityEngine;

namespace KK_HCharaAdjustmentEx
{
    /// <summary>
    /// 外部プラグイン連携用 API（例: KK_Rankou のゲスト補正）。
    /// v1.12 で自動補正は「帯域方式の参照アライメント」（Hシーン本体のみ）に一本化され、
    /// スケール比 comp は撤去された。本 API の comp 系は互換のため残置し常に false を返す。
    /// TryComputeAdjustment は「完全一致キーの手動残差」のみ返す（外部キャラへの
    /// 参照アライメント適用は未対応・TODO）。
    /// </summary>
    public static class HCharaAdjustApi
    {
        public const int ApiVersion = 3;

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
        /// 【ApiVersion 3】完全一致キー（同カード組で保存した手動補正）の残差を返す。
        /// info はゲーム本体 lstAnimInfo の**同一インスタンス**を渡すこと（MOD体位の同定が参照比較のため）。
        /// v1.12 で自動 comp・別カード流用（推定）は撤去＝手動保存が無ければゼロを返す。
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
                string key = HSceneHooks.BuildKeyFor(info, female, null, male);
                var    src = AdjustmentStore.GetEntry(key);
                if (src == null) return true;   // 手動保存なし＝補正なし

                // 残差（保存時の自動補正を除いた手動分）を各キャラの現在向きでワールド化
                Vector3 resF = HSceneHooks.ResidualOf(src, 0);
                Vector3 resM = HSceneHooks.ResidualOf(src, 2);
                if (resF != Vector3.zero) femaleOffsetWorld = female.transform.rotation * resF;
                if (male != null && resM != Vector3.zero)
                    maleOffsetWorld = male.transform.rotation * resM;

                if (resF != Vector3.zero || resM != Vector3.zero)
                    Plugin.Logger.LogInfo(string.Format(
                        "[HCharaAdjustmentEx] API残差適用(完全一致): {0} resF=({1:F3},{2:F3},{3:F3}) resM=({4:F3},{5:F3},{6:F3})",
                        src.key, resF.x, resF.y, resF.z, resM.x, resM.y, resM.z));
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogWarning(
                    "[HCharaAdjustmentEx] API残差解決失敗 → 補正なしで続行（要調査）: " + e.Message);
            }
            return true;
        }

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
