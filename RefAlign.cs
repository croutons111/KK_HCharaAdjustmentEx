using System.Collections;
using System.Collections.Generic;
using IllusionUtility.GetUtility;
using Manager;
using UnityEngine;

namespace KK_HCharaAdjustmentEx
{
    // ─── 参照アライメント（v1.11 / 帯域方式）───────────────────────────────
    // 方針（2026-07-10 の実測・対照実験で確定）:
    //   ・キャラメイクの正規身長スライダー（0〜100＝内部値0〜1）で作れる体（s=0.84〜1.00）は
    //     製品の正規データであり、本体（アニメ＋MotionIK）が正しく処理する → 【完全無介入】
    //     （対照実験: s=0.86/0.92 のバニラ子は机体位で無補正でも完璧に接地）
    //   ・スライダーアンロックMODで範囲外になった体だけを、最寄りの正規端
    //     （下限=身長0 / 上限=身長100 の標準プロポーション参照ボディ）の結合点に整列する。
    //     shift は「アンロック分」だけになり、過剰補正が原理的に出ない。
    //   ・結合点: 挿入=a_n_kokan / 奉仕=kindHoushi 別（口・手・胸）/
    //     愛撫は家具接触体位のみ（useDesk=手・useChair=尻。床・立ち・壁は対象外＝男の手が女に追従）。
    //     女性のみシフト（男は基準側）。
    //   ・採取・実測とも「短窓平均」「objTop 基準」（fileMove 相殺・ルートシフト不変）。
    //   ・【ステート別参照】実キャラの現在アニメステート（shortNameHash）ごとに参照を採取・整列する。
    //     Idle とループで体勢が変わる体位（例: 椅子奉仕のしゃがみ→膝立ち）や、Idle を通らない
    //     体位スワップにも正しいシフトが出る。ハッシュを直接 Animator.Play に渡すため
    //     ステート名の候補リストは不要（MOD体位・アナルA_系にも汎用）。
    //   ・ステート切替中・新ステート測定中は直前のシフトを維持（行為中にガタつかない）。
    //   ・口結合のみ「Idle / ループ族」の2区分（ループ間差は実側IKノイズ＝スパート沈み防止）。
    //   ・【同時採取】参照と実キャラを同じフレーム群で測定し shift を直接確定する。カメラ由来の
    //     首姿勢（首ミラーで両側に共通化）・ループ位相・表情のバイアスが差分で相殺され、
    //     同じ子なら何度測っても同じ shift になる（実測: 別時刻の実測窓では ±5cm ブレた）。
    //     ディスクキャッシュは廃止（実キャラとの同時測定が前提＝カード横断で使い回せる値が無い）。
    internal static class RefAlign
    {
        // 結合点の種別（参照キャッシュのキーに含む）
        private const int CoupKokan = 0;   // 挿入: a_n_kokan
        private const int CoupHand  = 1;   // 奉仕 kindHoushi=0: cf_j_hand_L/R 中点
        private const int CoupMouth = 2;   // 奉仕 kindHoushi=1: a_n_mouth（参照ベース・持ち上げは Mouth Shift Scale で調整）
        private const int CoupBust  = 3;   // 奉仕 kindHoushi=2: cf_d_bust00
        private const int CoupSiri  = 4;   // 愛撫（椅子）: cf_j_siri_L/R 中点（座り接触）
        private const int CoupFoot  = 5;   // 奉仕（足コキ系）: cf_j_foot_L/R 中点
        private const int CoupAuto  = 100; // 奉仕: ステートごとに幾何判定（dan 最近傍の部位を採用）

        // "Idle" ステートの shortNameHash（名前のみ由来＝全コントローラ共通。実測 2081823275）
        private static readonly int IdleStateHash = Animator.StringToHash("Idle");

        // 正規スライダーの実効スケール範囲（実測: s = 0.84 + 0.16×slider、線形）。
        // ゲームの素体シェイプデータ由来の定数（KK は更新終了済み・環境非依存）。
        // 参照ボディの端設定時に実測とのサニティチェックを行い、ズレていれば LogWarning。
        private const float S_LO = 0.84f;
        private const float S_HI = 1.00f;
        private const float BandEps = 0.002f;   // 帯の遊び（境界ぴったりのキャラのばたつき防止）

        private const float SettleSec  = 0.35f;   // 参照採取: Play 後の姿勢安定待ち
        private const float WindowMin  = 0.6f;    // 測定窓の下限（秒）
        private const float WindowMax  = 3.0f;    // 測定窓の上限（秒・長尺クリップの停滞防止）
        private const int   MeasureMinSamples   = 15;    // 同時採取: 有効サンプルの下限（不足なら見送り→再訪で再試行）
        private const float MaxShift = 0.3f;      // 安全上限
        private const float CapK     = 1.5f;      // 物理上限キャップ係数: |shift| ≤ 帯域からの距離 × CapK

        private static bool _hardFailed;           // 参照ボディ生成不能等（セッション中は機能停止）
        internal static bool Enabled =>
            Plugin.RefAlignEnabled != null && Plugin.RefAlignEnabled.Value && !_hardFailed;

        // ── 採取状態（同時採取方式: shift はセッション内 _shiftCache に直接確定） ──
        private static readonly HashSet<string> _sampling = new HashSet<string>();   // 採取中の measKey
        private static readonly HashSet<string> _failed   = new HashSet<string>();   // 採取不能（対象ステート無し等・再試行しない）

        // ── 適用の per-idx 状態（ステート別） ──
        private static readonly string[]  _key    = { "", "", "" };  // 現在の体位キー（変化で girlS 再測・シフト無効化）
        private static readonly string[]  _measKey = { "", "", "" }; // 適用中シフトの measKey（key|idx|stateToken）
        private static readonly Vector3[] _shiftLocal = new Vector3[3];
        private static readonly bool[]    _shiftValid = new bool[3]; // 適用中シフトの有無（測定中は旧値維持）
        private static readonly float[]   _girlS  = new float[3];    // キーごとに実測した実キャラのスケール
        private static readonly bool[]    _bandLogged = new bool[3]; // 帯域判定ログの重複抑止

        // 確定済みシフトのセッションキャッシュ（key|idx|stateHash → シフト。ステート再訪時に即適用）
        private static readonly Dictionary<string, Vector3> _shiftCache = new Dictionary<string, Vector3>();

        // 奉仕の結合点判定結果（key|idx|stateHash → Coup*）。kindHoushi メタデータは実接触と食い違う
        // ことがある（足コキ系が「手」登録・イラマチオ系MODが「手」登録等）ため、ステートごとに
        // 「男の dan 基部に最も近い部位」を実測で採用する。
        private static readonly Dictionary<string, int> _coupCache = new Dictionary<string, int>();

        // ── 隠し参照ボディ（標準プロポーション・身長だけ端。挿入/手/胸/愛撫の整列用。口は dan 基準）。 ──
        // ※プロポーション移植は撤去（2026-07-12）: SetShapeBodyValue の部位別スケールがアニメ骨に
        //   乗らなかった（診断で口/骨盤比が実キャラと乖離）。口の高すぎは dan 基準測定で解決。
        private static GameObject? _refContainer;
        private static ChaControl? _refCha;
        private static int  _refBodyBound = -1;    // 参照ボディの現在の端（0=lo/1=hi、-1=未設定）
        private static readonly bool[] _boundChecked = new bool[2];   // 端ごとのサニティチェック済み

        // 【教訓 2026-07-12】実ペアの IK（FBBIK/MotionIK）・Lookat_dan を参照ペアへ複製する方式は撤去。
        // 手コキ系で「女性の手→dan マーカーを追う（FBBIK）」×「dan→女性の手ヌルを追う（Lookat_dan）」の
        // 相互参照が正のフィードバックになり、参照ペアだけ下向きに螺旋発散（手が床 y=0.02 まで落下）。
        // 実シーンは本体の更新順序・補正コンポーネント群で安定しており、その全複製は非現実的。
        // 参照は素のアニメ再生（ベイクド）とし、首制御のみ MirrorRefNeck で実側をミラーする。
        // ── 参照男性は廃止（2026-07-12）。IK 複製撤去で本来の用途（男 dan マーカー供給）が消え、
        //    残っていた首ターゲット写像も「実男性の実骨位置を objTop 相対でプロキシ写像」で代替できる
        //    （むしろ実位置を使うので近似が良い）ため、毎シーンの CreateMale ロードごと削除した。

        // ── 首制御ミラー（案A: 参照女性に実女性と同じ首条件を掛け、口そのものを測定する） ──
        private static GameObject? _refNeckProxy;      // カメラ等シーン側ターゲットの参照空間プロキシ
        private static bool        _neckMirrorWarned;  // ミラー失敗の警告（1回だけ）
        private static bool        _sampleBusy;        // 採取直列化（参照ボディは共有資源）

        // ── 同時採取のフレーム測定状態（HookMeasureTick が Plugin.LateUpdate 相で書く） ──
        // 測定位相は EnforceAndSave と同じ「実キャラの IK/首 LateUpdate より前」＝素のアニメ姿勢。
        // コルーチン（Update 相）で読むと前フレームの IK 適用後になり、IK で世界固定される結合点
        //（椅子に着く手等）が体格と無関係な値になって比較が壊れる（実測: 手 y 0.257→0.523。2026-07-12）。
        private static bool        _coMeasure;      // 測定窓が開いている
        private static int         _coCoup;
        private static int         _coStateHash;
        private static int         _coIdx;
        private static ChaControl? _coRealF;
        private static Vector3     _coAccR, _coAccA;
        private static int         _coN, _coStMiss, _coAMiss;

        // ─────────────────────────────────────────────────────────────────────
        // Hooks から毎フレーム呼ばれる。適用すべきワールドシフト（comp 相当枠）を返す。
        // 正規範囲内のキャラ・対象外モードはゼロ。測定中・参照未確定は直前の確定シフトを維持。
        internal static Vector3 GetShift(HFlag flags, ChaControl cha, int idx, string key)
        {
            if (idx == 2 /*MALE*/) return Vector3.zero;
            int coup = CouplingFor(flags, cha);
            if (coup < 0) { ResetIdx(idx); return Vector3.zero; }   // 対象外モード（愛撫の非家具体位等）

            if (_key[idx] != key)
            {
                _key[idx]        = key;
                _measKey[idx]    = "";
                _shiftValid[idx] = false;   // 体位が変わったら旧シフトは無効（別ポーズの値のため）
                _girlS[idx]      = MeasureScaleDirect(cha);
                _bandLogged[idx] = false;
            }

            // 正規スライダー範囲内の体は本体が正しく処理する → 完全無介入（採取もしない）
            float s = _girlS[idx];
            if (s <= 0f) return Vector3.zero;   // 測定不能はバニラ
            if (s >= S_LO - BandEps && s <= S_HI + BandEps) return Vector3.zero;

            int bound = s < S_LO ? 0 : 1;
            if (!_bandLogged[idx])
            {
                _bandLogged[idx] = true;
                Plugin.Logger.LogInfo(string.Format(
                    "[HCharaAdjustmentEx] 参照整列対象 {0} s={1:F4}（正規範囲 {2:F2}-{3:F2} 外）→ {4} 参照へ整列",
                    idx == 0 ? "Female1" : "Female2", s, S_LO, S_HI, bound == 0 ? "下限(身長0)" : "上限(身長100)"));
            }

            // 適用値（測定の進行に関係なく、確定済みの直近シフトを返し続ける）。
            // 物理上限キャップは適用の瞬間に掛ける＝Config をリアルタイムに切り替えて比較できる。
            Vector3 applied = _shiftValid[idx]
                ? HSceneHooks.VanillaRotOf(idx) * ApplyCap(_shiftLocal[idx], s, bound2: s < S_LO ? 0 : 1, coup)
                : Vector3.zero;

            var anim = cha.animBody;
            if (anim == null) return applied;
            var st = anim.GetCurrentAnimatorStateInfo(0);
            int hash = st.shortNameHash;
            if (hash == 0) return applied;

            // 非ループステート（絶頂ワンショット・挿入遷移等）は測定・採取をせず直前シフトを維持する。
            //   ①周期性が無く「ループ1周期平均」の前提が崩れる（部分平均＝ノイズ）
            //   ②絶頂の瞬間にシフトが差し替わると最も目立つタイミングでガタつく
            //   ③絶頂はスパート（OLoop）の連続動作＝スパートの整列を引き継ぐのが正しい（ユーザー設計）
            if (!st.loop) return applied;

            if (!HSceneHooks.TryParseKeyPublic(key, out string animSig, out _)) return applied;

            // 口結合は「Idle / ループ族」の2区分にする。WLoop/SLoop/OLoop 間の実測差は
            // 実側 IK の胴変形由来のノイズで、ステート別に追うとスパートで沈む（実測 9cm）。
            // ループ族で 1 つの shift を共有しステート間一貫にする（初回に測ったループで確定）。
            // しゃがみ（Idle）→膝立ち（ループ）のような Idle/ループ差は維持される。
            // 口の細かい照準は本体の Lookat_dan＋eyeneck が実キャラ側で吸収する（本来の役割分担）。
            string stateToken = coup == CoupMouth
                ? (hash == IdleStateHash ? "Idle" : "Loop")
                : hash.ToString();
            string measKey = key + "|" + idx + "|" + stateToken;

            // 奉仕: 結合点をステートごとに幾何判定（dan 最近傍）。判定できるまで旧シフト維持。
            if (coup == CoupAuto)
            {
                if (!_coupCache.TryGetValue(measKey, out int resolved))
                {
                    if (anim.IsInTransition(0)) return applied;   // 遷移明けの姿勢で判定する
                    resolved = ResolveHoushiCoup(HSceneHooks.MaleOf(flags), cha, applied);
                    if (resolved < 0) return applied;             // dan 未解決等 → 次フレーム再試行
                    _coupCache[measKey] = resolved;
                    Plugin.Logger.LogInfo(string.Format(
                        "[HCharaAdjustmentEx] 奉仕結合点判定 {0} state={1} → coup={2}",
                        idx == 0 ? "Female1" : "Female2", hash, CoupName(resolved)));
                }
                coup = resolved;
            }

            // 確定済みシフトがあれば即適用（ステート再訪等）
            if (_shiftCache.TryGetValue(measKey, out Vector3 cached))
            {
                if (_measKey[idx] != measKey)
                {
                    _measKey[idx]    = measKey;
                    _shiftLocal[idx] = cached;
                    if (cached != Vector3.zero) _shiftValid[idx] = true;
                }
                return _shiftValid[idx]
                    ? HSceneHooks.VanillaRotOf(idx) * ApplyCap(_shiftLocal[idx], s, bound, coup)
                    : Vector3.zero;
            }

            if (_failed.Contains(measKey)) return applied;   // 採取不能なステート（参照側に無い等）

            // 適用中シフトが無い＝このステート初回（carry も cache も無い）。採取完了までの
            // バニラ表示を避けるため、スケール比の一次近似を暫定シフトとして即適用する。
            // _measKey は据え置き＝採取完了時に上の L196 キャッシュ命中で精密値へ差し替わる。
            if (!_shiftValid[idx])
            {
                Vector3 seed = ComputeSeedLocal(cha, coup, bound, s, idx);
                if (seed != Vector3.zero)
                {
                    _shiftLocal[idx] = seed;
                    _shiftValid[idx] = true;
                    applied = HSceneHooks.VanillaRotOf(idx) * ApplyCap(seed, s, bound, coup);
                }
            }

            // 同時採取を起動（参照と実キャラを同じフレーム群で測定し shift を直接確定）。
            // 完了までは暫定シフト（初回）または旧シフトを維持。
            StartSampling(measKey, coup, bound, cha, hash, idx);
            return applied;
        }

        // 適用時の最終加工（リアルタイム反映）。キャッシュは生値を保持し、ここで Config を反映する。
        //   ①口係数: 口奉仕（CoupMouth）は身長差の影響が最大で補正が最も大きく出るが、本体の
        //     Lookat_dan が dan を口へ向けて高さ残差を吸収するため、フル整列は過剰になりやすい。
        //     Config Mouth Shift Scale で弱める（残りは本体追従に任せる）。
        //   ②物理上限キャップ: 補正量は「帯域からの距離 × CapK」を超えられない（スケール差成分の上限）。
        //     プロポーション差成分はこれを超え得るが、測定バイアスと大きさで区別できないため、帯域に
        //     近い子ほど上限を絞る＝バイアス由来の浮き/離れを抑える（Config Shift Cap）。
        private static Vector3 ApplyCap(Vector3 shift, float s, int bound2, int coup)
        {
            if (coup == CoupMouth && Plugin.MouthShiftScale != null)
                shift *= Plugin.MouthShiftScale.Value;
            if (Plugin.ShiftCapEnabled == null || !Plugin.ShiftCapEnabled.Value) return shift;
            float dist  = bound2 == 0 ? (S_LO - s) : (s - S_HI);
            float allow = Mathf.Max(0f, dist) * CapK;
            if (shift.magnitude > allow)
                shift = shift.normalized * allow;
            return shift;
        }

        private static void ResetIdx(int idx)
        { _key[idx] = ""; _measKey[idx] = ""; _shiftValid[idx] = false; }

        // 椅子体位のうち「座らず椅子に手をつく」もの（結合点=尻でなく手）。実コントローラ名で判定。
        // 例: kha_f_04=椅子バック愛撫（ユーザー実機確認 2026-07-10）。該当を見つけたらここに追加する。
        private static readonly HashSet<string> _chairHandsCtrls = new HashSet<string> { "kha_f_04" };

        // 対象モードの結合点種別（-1=対象外）
        private static int CouplingFor(HFlag flags, ChaControl cha)
            => CouplingForInfo(flags.nowAnimationInfo, cha);

        // info 直受け版（外部API＝HSceneProc 外のペアと共用。本シーン経路と同一ロジック）
        private static int CouplingForInfo(HSceneProc.AnimationListInfo? info, ChaControl cha)
        {
            int mode = info != null ? (int)info.mode : -1;
            switch (mode)
            {
                case 2: case 7: case 8:   // sonyu / sonyu3P / sonyu3PMMF
                    return CoupKokan;
                case 1: case 6:           // houshi / houshi3P
                    switch (info!.kindHoushi)
                    {
                        case 1:  return CoupMouth;   // 口・胸はメタデータが信頼できる（実機実績）
                        case 2:  return CoupBust;
                        default: return CoupAuto;    // 手奉仕バケツのみ幾何判定（手/足/股下が混在するため）
                    }
                case 0:                   // aibu: 家具接触体位のみ対象（机=手をつく/椅子=座る or 手をつく）。
                {                         //       床・立ち・壁の愛撫はペア整列不要（男の手が女に追従）＝対象外。
                    if (info!.useDesk > 0) return CoupHand;
                    if (info!.useChair > 0)
                    {
                        // 椅子バック系（椅子に手をつく体位）は手を結合点にする
                        var rc = cha != null && cha.animBody != null ? cha.animBody.runtimeAnimatorController : null;
                        if (rc != null && _chairHandsCtrls.Contains(rc.name)) return CoupHand;
                        return CoupSiri;
                    }
                    return -1;
                }
                default:
                    return -1;            // 自慰等は対象外
            }
        }

        // 手奉仕（kindHoushi=0）の結合点判定: 手・足・股下（スマタ）のうち
        // 「男の dan 基部に最も近い部位」を返す（-1=判定不能）。
        // 口・胸は候補にしない（kindHoushi=1/2 のメタデータが信頼できるため。
        // 幾何に含めるとフェラ系で「腿に添えた手」が dan 基部に近く口が系統的に負ける）。
        // 距離は彼女のシフト適用前の位置で比較する（適用中シフトを差し引き＝判定の安定性確保）。
        private static int ResolveHoushiCoup(ChaControl? male, ChaControl cha, Vector3 appliedWorld)
        {
            var body = male != null ? male.objBodyBone : null;
            var dan  = body != null ? body.transform.FindLoop("cm_J_dan101_00") : null;
            if (dan == null) return -1;
            Vector3 danPos = dan.transform.position;

            float best = float.MaxValue; int bestC = -1;
            int[] candidates = { CoupHand, CoupFoot, CoupKokan };   // 手 / 足 / 股下(スマタ)
            for (int i = 0; i < candidates.Length; i++)
            {
                var p = CouplingPos(cha, candidates[i]);
                if (p == null) continue;
                float d = (p.Value - appliedWorld - danPos).sqrMagnitude;
                if (d < best) { best = d; bestC = candidates[i]; }
            }
            return bestC;
        }

        private static string CoupName(int coup) =>
            coup == CoupKokan ? "kokan" : coup == CoupHand ? "hand" :
            coup == CoupMouth ? "mouth" : coup == CoupBust ? "bust" :
            coup == CoupFoot ? "foot" : "siri";

        // ── 結合点のワールド座標（実キャラ・参照ボディ共用） ──
        private static Vector3? CouplingPos(ChaControl cha, int coup)
        {
            switch (coup)
            {
                case CoupKokan:
                {
                    var r = cha.GetReferenceInfo(ChaReference.RefObjKey.a_n_kokan);
                    return r != null ? (Vector3?)r.transform.position : null;
                }
                case CoupHand:
                {
                    var body = cha.objBodyBone; if (body == null) return null;
                    var l = body.transform.FindLoop("cf_j_hand_L");
                    var r = body.transform.FindLoop("cf_j_hand_R");
                    if (l != null && r != null) return (l.transform.position + r.transform.position) * 0.5f;
                    return l != null ? (Vector3?)l.transform.position : r != null ? (Vector3?)r.transform.position : null;
                }
                case CoupMouth:
                {
                    // 実際の口（a_n_mouth）で測る。かつては本体 eyeneck（ステート別の首パターン）が
                    // 実側だけに掛かり ±10cm の測定バイアスになるため cf_j_neck（首の回転ピボット）で
                    // 代用していたが、首基準は頭サイズ差の分だけ口位置がズレる
                    //（実測: 小柄で dan が下曲がり・体が前方へ過剰シフト。2026-07-12）。
                    // 現在は採取中に実女性の首制御状態を参照女性へミラーして同条件化しているため
                    //（MirrorRefNeck）、口そのものを測定できる。
                    var r = cha.GetReferenceInfo(ChaReference.RefObjKey.a_n_mouth);
                    if (r != null) return r.transform.position;
                    var body = cha.objBodyBone; if (body == null) return null;
                    var h = body.transform.FindLoop("cf_j_head");
                    if (h != null) return h.transform.position;
                    var nk = body.transform.FindLoop("cf_j_neck");
                    return nk != null ? (Vector3?)nk.transform.position : null;
                }
                case CoupBust:
                {
                    var body = cha.objBodyBone; if (body == null) return null;
                    var b = body.transform.FindLoop("cf_d_bust00") ?? body.transform.FindLoop("cf_j_spine03");
                    return b != null ? (Vector3?)b.transform.position : null;
                }
                case CoupSiri:
                {
                    var body = cha.objBodyBone; if (body == null) return null;
                    var l = body.transform.FindLoop("cf_j_siri_L");
                    var r = body.transform.FindLoop("cf_j_siri_R");
                    if (l != null && r != null) return (l.transform.position + r.transform.position) * 0.5f;
                    var k = cha.GetReferenceInfo(ChaReference.RefObjKey.a_n_kokan);
                    return k != null ? (Vector3?)k.transform.position : null;
                }
                case CoupFoot:
                {
                    var body = cha.objBodyBone; if (body == null) return null;
                    var l = body.transform.FindLoop("cf_j_foot_L");
                    var r = body.transform.FindLoop("cf_j_foot_R");
                    if (l != null && r != null) return (l.transform.position + r.transform.position) * 0.5f;
                    return l != null ? (Vector3?)l.transform.position : r != null ? (Vector3?)r.transform.position : null;
                }
            }
            return null;
        }

        // cf_n_height を毎回 FindLoop して直接実測（キャッシュ陳腐化の影響を受けない）
        private static float MeasureScaleDirect(ChaControl cha)
        {
            var body = cha != null ? cha.objBodyBone : null;
            var h = body != null ? body.transform.FindLoop("cf_n_height") : null;
            if (h == null) return -1f;
            float root = cha!.transform.lossyScale.y;
            if (root <= 0f) root = 1f;
            return h.transform.lossyScale.y / root;
        }

        // 採取完了までの暫定シフト（スケール比の一次近似＝旧 bodyComp 相当）。
        // refCoupling − actualCoupling ≈ (sEdge/s − 1) × (coupling − cf_n_height)。
        // 精密値は同時採取が上書きする。左右無効・小柄の沈み禁止・MaxShift は本採取と揃える。
        private static Vector3 ComputeSeedLocal(ChaControl cha, int coup, int bound, float s, int idx)
            => ComputeSeedLocalFrame(cha, coup, bound, s, HSceneHooks.VanillaRotOf(idx));

        // 回転フレーム指定版（本シーン=vanillaRot / 外部API=キャラの現在ルート回転）
        private static Vector3 ComputeSeedLocalFrame(ChaControl cha, int coup, int bound, float s, Quaternion frameRot)
        {
            if (s <= 0f) return Vector3.zero;
            var couplingW = CouplingPos(cha, coup);
            var body = cha.objBodyBone;
            var h = body != null ? body.transform.FindLoop("cf_n_height") : null;
            if (couplingW == null || h == null) return Vector3.zero;
            float sEdge = bound == 0 ? S_LO : S_HI;
            Vector3 seedWorld = (sEdge / s - 1f) * (couplingW.Value - h.transform.position);
            Vector3 seed = Quaternion.Inverse(frameRot) * seedWorld;
            seed.x = 0f;                                      // 左右は常に無効（本採取と同じ）
            if (bound == 0 && seed.y < 0f) seed.y = 0f;       // 小柄の沈み禁止（同上）
            if (seed.magnitude > MaxShift) seed = seed.normalized * MaxShift;
            return seed;
        }

        // ── 外部プラグイン連携（HCharaAdjustApi 経由・例: KK_Rankou のゲストペア）──────
        // HSceneProc 外のペアに適用する自動補正シフト（ワールド）をワンショットで計算する。
        // 同時採取（参照ボディ）は「共有資源の直列採取＋LateUpdate 同位相測定＋継続適用」が
        // 前提のため外部ペアには使わず、解析シード（一次近似）のみ返す。
        // 帯域内（正規スライダー範囲）のキャラは完全にゼロ＝無介入（本シーンと同じ）。
        internal static Vector3 ComputeExternalShiftWorld(
            ChaControl female, ChaControl? male, HSceneProc.AnimationListInfo info)
        {
            if (Plugin.RefAlignEnabled == null || !Plugin.RefAlignEnabled.Value) return Vector3.zero;
            if (female == null || info == null) return Vector3.zero;

            int coup = CouplingForInfo(info, female);
            if (coup < 0) return Vector3.zero;                 // 対象外モード（床・立ち・壁の愛撫等）
            if (coup == CoupAuto)
            {
                coup = ResolveHoushiCoup(male, female, Vector3.zero);   // 外部はシフト適用前に呼ばれる
                if (coup < 0)
                {
                    Plugin.Logger.LogWarning(
                        "[HCharaAdjustmentEx] API: 奉仕結合点の幾何判定不能（男性 dan 未解決）→ 自動補正なしで続行（要調査）");
                    return Vector3.zero;
                }
            }

            float s = MeasureScaleDirect(female);
            if (s <= 0f) return Vector3.zero;                  // 測定不能はバニラ（本シーンと同じ）
            if (s >= S_LO - BandEps && s <= S_HI + BandEps) return Vector3.zero;   // 正規範囲内=無介入
            int bound = s < S_LO ? 0 : 1;

            Quaternion rot = female.transform.rotation;
            Vector3 local = ComputeSeedLocalFrame(female, coup, bound, s, rot);
            if (local == Vector3.zero) return Vector3.zero;
            local = ApplyCap(local, s, bound, coup);           // Mouth Shift Scale / Shift Cap を反映
            Vector3 world = rot * local;
            Plugin.Logger.LogInfo(string.Format(
                "[HCharaAdjustmentEx] API自動補正(シード): {0} s={1:F4}（{2}参照）coup={3} shift=({4:F3},{5:F3},{6:F3})",
                female.chaFile?.parameter?.fullname ?? female.name, s,
                bound == 0 ? "下限" : "上限", CoupName(coup), world.x, world.y, world.z));
            return world;
        }

        // ── 同時採取 ─────────────────────────────────────────────────────────
        // 参照ボディに対象ステートを再生させ、実キャラと【同じフレーム群】で両者の結合点を測り、
        // shift = 参照平均 − 実測平均 を直接確定する。
        private static void StartSampling(string measKey, int coup, int bound, ChaControl realFemale, int stateHash, int idx)
        {
            if (_sampling.Contains(measKey)) return;
            if (Plugin.Instance == null || realFemale == null) return;
            var ctrl = realFemale.animBody != null
                ? realFemale.animBody.runtimeAnimatorController : null;
            if (ctrl == null) return;
            _sampling.Add(measKey);
            Plugin.Instance.StartCoroutine(IE_Sample(measKey, coup, bound, ctrl, stateHash, realFemale, idx));
        }

        // 採取は同時 1 本に直列化する。参照ボディは共有資源のため、並行採取が互いの Play() で
        // ステートを奪い窓が汚染される（実測: ベンチフェラで窓 119 フレーム中 63 が対象外。2026-07-12）。
        // ラッパー方式で例外時も busy を確実に解除する（yield を含む本体を安全に try で包めないため）。
        private static IEnumerator IE_Sample(string measKey, int coup, int bound, RuntimeAnimatorController ctrl, int stateHash, ChaControl realF, int idx)
        {
            while (_sampleBusy) yield return null;
            _sampleBusy = true;
            var inner = IE_SampleCore(measKey, coup, bound, ctrl, stateHash, realF, idx);
            while (true)
            {
                object? cur;
                try
                {
                    if (!inner.MoveNext()) break;
                    cur = inner.Current;
                }
                catch (System.Exception e)
                {
                    Plugin.Logger.LogWarning(
                        "[HCharaAdjustmentEx] 同時採取で例外 → この採取を中断（要調査） " + measKey + ": " + e.Message);
                    break;
                }
                yield return cur;
            }
            _coMeasure = false;   // 例外中断でも測定窓を確実に閉じる
            _sampleBusy = false;
            _sampling.Remove(measKey);
        }

        // 同時採取のフレーム測定。Plugin.LateUpdate（EnforceAndSave と同位相＝実キャラの
        // IK/首 LateUpdate より前の素のアニメ姿勢）から毎フレーム呼ばれる。
        // 参照・実キャラとも同じフレーム・同じ位相で測る＝カメラ/位相バイアスが差分で相殺され、
        // IK で世界固定される結合点も「素のアニメどうし」の比較になる。
        internal static void HookMeasureTick()
        {
            if (!_coMeasure) return;
            try
            {
                var refCha = _refCha;
                var realF  = _coRealF;
                var rAnim  = refCha != null ? refCha.animBody : null;
                if (refCha == null || rAnim == null || _refContainer == null) return;
                if (rAnim.GetCurrentAnimatorStateInfo(0).shortNameHash != _coStateHash)
                { _coStMiss++; return; }   // 参照が対象外ステート（コルーチンが再ピン留めする）
                var aAnim = realF != null ? realF.animBody : null;
                if (aAnim == null ||
                    aAnim.GetCurrentAnimatorStateInfo(0).shortNameHash != _coStateHash ||
                    aAnim.IsInTransition(0))
                { _coAMiss++; return; }    // 実キャラが対象ステートに居ない＝両側とも計上しない

                var pR   = CouplingPos(refCha, _coCoup);
                var pA   = CouplingPos(realF!, _coCoup);
                var topR = refCha.objTop;   // objTop 基準（fileMove 相殺・ルートシフト不変）
                var topA = realF!.objTop;
                if (pR == null || pA == null || topR == null || topA == null) return;
                _coAccR += Quaternion.Inverse(_refContainer.transform.rotation) * (pR.Value - topR.transform.position);
                _coAccA += Quaternion.Inverse(HSceneHooks.VanillaRotOf(_coIdx)) * (pA.Value - topA.transform.position);
                _coN++;
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogDebug("[HCharaAdjustmentEx] 同時採取フレーム測定失敗（このフレームは計上せず続行）: " + e.Message);
            }
        }

        private static IEnumerator IE_SampleCore(string measKey, int coup, int bound, RuntimeAnimatorController ctrl, int stateHash, ChaControl realF, int idx)
        {
            if (!EnsureRefBody()) { _sampling.Remove(measKey); yield break; }
            var cha = _refCha!;
            // 生成直後はロード未完了の可能性 → animBody が立つまで待つ（最大2秒）
            float bootUntil = Time.time + 2f;
            while ((cha.animBody == null || cha.objBodyBone == null) && Time.time < bootUntil)
                yield return null;
            var anim = cha.animBody;
            if (anim == null)
            {
                Plugin.Logger.LogWarning("[HCharaAdjustmentEx] 参照ボディの animBody が2秒経っても未初期化 → 採取中断（次の体位で再試行） " + measKey);
                _sampling.Remove(measKey);
                yield break;
            }

            // 参照ボディを要求された端（身長0 or 100）に設定し、定数とサニティチェック。
            // ※プロポーション移植は撤去（2026-07-12）: SetShapeBodyValue の部位別スケールがアニメ骨に
            //   乗らず（身長 index のみ有効・口/骨盤比が実キャラと乖離＝診断で確認）、無効だった。
            //   口奉仕の「高すぎ」は dan 基準測定（下記）で解く。参照ボディは口以外（挿入/手/胸/愛撫）用。
            if (_refBodyBound != bound)
            {
                cha.SetShapeBodyValue(0, bound == 0 ? 0f : 1f);
                _refBodyBound = bound;
                yield return null;   // シェイプ反映待ち（2フレーム）
                yield return null;
                float s = MeasureScaleDirect(cha);
                if (!_boundChecked[bound])
                {
                    _boundChecked[bound] = true;
                    float expect = bound == 0 ? S_LO : S_HI;
                    if (Mathf.Abs(s - expect) > 0.005f)
                        Plugin.Logger.LogWarning(string.Format(
                            "[HCharaAdjustmentEx] 参照ボディの端スケールが定数と不一致（実測 {0:F4} / 期待 {1:F2}）。" +
                            "素体シェイプが標準と異なる環境の可能性＝要調査（整列は実測体で続行）", s, expect));
                    else
                        Plugin.Logger.LogInfo(string.Format(
                            "[HCharaAdjustmentEx] 参照ボディ {0} s={1:F4}（定数と一致）",
                            bound == 0 ? "下限(身長0)" : "上限(身長100)", s));
                }
            }

            // 実キャラのコントローラ資産を共有し、対象ステートをハッシュで直接再生
            //（ロード処理・クロスフェード無し。名前解決不要＝MOD体位にも汎用）
            anim.runtimeAnimatorController = ctrl;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;   // 画面外でもボーン更新
            if (!anim.HasState(0, stateHash))
            {
                Plugin.Logger.LogWarning("[HCharaAdjustmentEx] 同時採取: 対象ステートが参照側に無い → このステートは整列なし " + measKey);
                _sampling.Remove(measKey);
                _failed.Add(measKey);   // 再試行しない（このステートは旧シフト/バニラのまま）
                yield break;
            }
            anim.Play(stateHash, 0, 0f);

            yield return null;   // ステート反映後にクリップ長を取得（窓＝ループ1周期・位相バイアス除去）
            float clipLen = anim.GetCurrentAnimatorStateInfo(0).length;
            float window  = Mathf.Clamp(clipLen, WindowMin, WindowMax);

            // 落ち着き待ち。この間も首ミラーを回す（neckLookCtrl のスムージング収束を待つため）
            float settleUntil = Time.time + SettleSec;
            while (Time.time < settleUntil) { MirrorRefNeck(realF); yield return null; }

            // 窓の間、実際の測定は HookMeasureTick（Plugin.LateUpdate 相）が毎フレーム行う。
            // ここ（Update 相）は窓の制御・参照の再ピン留め・首ミラーのみを担当する。
            _coCoup      = coup;
            _coStateHash = stateHash;
            _coIdx       = idx;
            _coRealF     = realF;
            _coAccR = Vector3.zero; _coAccA = Vector3.zero;
            _coN = 0; _coStMiss = 0; _coAMiss = 0;
            _coMeasure   = true;
            float until = Time.time + window;
            float hardUntil = Time.time + window * 2f + 1f;   // 計上失敗延長の上限（無限ループ防止）
            int lastN = 0;
            while (Time.time < until && Time.time < hardUntil)
            {
                MirrorRefNeck(realF);   // 実女性の首制御状態を参照女性へ写す（口測定の同条件化）
                if (anim.GetCurrentAnimatorStateInfo(0).shortNameHash != stateHash)
                    anim.Play(stateHash, 0, 0f);        // 参照の再ピン留め（外れフレームは hook 側で計上除外）
                if (_coN == lastN) until += Time.deltaTime;   // 前フレーム計上できなかった分は窓を延長
                lastN = _coN;
                yield return null;
            }
            _coMeasure = false;
            _sampling.Remove(measKey);
            int n = _coN, stMiss = _coStMiss, aMiss = _coAMiss;
            if (n < MeasureMinSamples)
            {
                // 実キャラが窓中に対象ステートを離れた等 → 今回は見送り（旧シフト維持・ステート再訪で再試行）
                Plugin.Logger.LogDebug(string.Format(
                    "[HCharaAdjustmentEx] 同時採取見送り {0}（有効 {1} / 参照外れ {2} / 実側外れ {3}）",
                    measKey, n, stMiss, aMiss));
                yield break;
            }
            Vector3 refAvg = _coAccR / n;
            Vector3 actAvg = _coAccA / n;
            Vector3 shift = refAvg - actAvg;   // 参照ベース（口も他も同じ測定）
            if (shift.magnitude > MaxShift) shift = shift.normalized * MaxShift;
            // 左右（X）補正は常に無効（ユーザー決定）: 体格差の補正は前後・上下で完結し、
            // X はほぼ測定ノイズ＝左右にずらすと相手・家具との横位置関係がむしろ崩れる。
            shift.x = 0f;
            // 下限側（小柄キャラ）の Y マイナス補正は禁止（沈み込み防止・ユーザー決定）。持ち上げ方向のみ。
            if (bound == 0 && shift.y < 0f) shift.y = 0f;
            // ※口奉仕の「高すぎ」は Mouth Shift Scale（適用時に ApplyCap で乗算・既定 0.8）で抑える。
            _shiftCache[measKey] = shift;
            Plugin.Logger.LogInfo(string.Format(
                "[HCharaAdjustmentEx] 参照整列(同時採取) {0} coup={1} ref=({2:F3},{3:F3},{4:F3}) actual=({5:F3},{6:F3},{7:F3}) shift=({8:F3},{9:F3},{10:F3}) n={11} stMiss={12} aMiss={13}",
                measKey, CoupName(coup), refAvg.x, refAvg.y, refAvg.z,
                actAvg.x, actAvg.y, actAvg.z, shift.x, shift.y, shift.z, n, stMiss, aMiss));
        }

        // 隠し参照ボディ（標準プロポーションの女）を生成。失敗したらセッション中は機能停止。
        private static bool EnsureRefBody()
        {
            if (_refCha != null && _refCha.gameObject != null) return true;
            if (_hardFailed) return false;
            try
            {
                var file = new ChaFileControl();   // 標準プロポーション素体（身長は採取時に端へ設定）
                _refContainer = new GameObject("HCAEx_RefBody");
                _refContainer.transform.position = new Vector3(0f, -40f, 0f);   // マップ外（非表示扱い）
                var scene = Plugin.CurrentHSceneProc != null
                    ? Plugin.CurrentHSceneProc.gameObject.scene
                    : UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(_refContainer, scene);

                Singleton<Character>.Instance.BeginLoadAssetBundle();
                _refCha = Singleton<Character>.Instance.CreateFemale(_refContainer, 90, file, true);
                Singleton<Character>.Instance.EndLoadAssetBundle(false);
                if (_refCha == null) throw new System.Exception("CreateFemale returned null");
                Singleton<Character>.Instance.loading.Load(_refCha, false);
                _refBodyBound = -1;   // 端は未設定（採取時に設定）
                Plugin.Logger.LogInfo("[HCharaAdjustmentEx] 参照ボディ生成 OK（標準プロポーション素体）");
                return true;
            }
            catch (System.Exception e)
            {
                _hardFailed = true;
                if (_refContainer != null) Object.Destroy(_refContainer);
                _refContainer = null; _refCha = null;
                Plugin.Logger.LogWarning(
                    "[HCharaAdjustmentEx] 参照ボディ生成失敗 → 参照整列をこのセッションでは無効化（要調査）: " + e.Message);
                return false;
            }
        }

        // 実女性の首制御状態（パターン・角度制限・ターゲット）を参照女性へ写す（採取中毎フレーム）。
        // 本体 HMotionEyeNeckFemale の最終出力は chara.neckLookCtrl への書き込みに集約される
        //（ptnNo/rate/target と neckTypeStates[5,6] の角度制限）ため、パターンテーブルの複製ではなく
        // 実行時状態のミラーで実ペアと同条件にする。ターゲットは参照空間の等価物へ写像する。
        private static void MirrorRefNeck(ChaControl? realF)
        {
            try
            {
                if (_refCha == null || realF == null) return;
                var rn = realF.neckLookCtrl;
                var fn = _refCha.neckLookCtrl;
                if (rn == null || fn == null) return;

                fn.ptnNo = rn.ptnNo;
                fn.rate  = rn.rate;

                // 角度制限（HMotionEyeNeckFemale.SetRange が体位リストから書く先）を写す
                var rs = rn.neckLookScript;
                var fsc = fn.neckLookScript;
                if (rs != null && fsc != null && rs.neckTypeStates != null && fsc.neckTypeStates != null)
                {
                    for (int t = 5; t <= 6 && t < rs.neckTypeStates.Length && t < fsc.neckTypeStates.Length; t++)
                    {
                        var a = rs.neckTypeStates[t].aParam;
                        var b = fsc.neckTypeStates[t].aParam;
                        if (a == null || b == null) continue;
                        for (int j = 0; j < a.Length && j < b.Length; j++)
                        {
                            b[j].upBendingAngle   = a[j].upBendingAngle;
                            b[j].downBendingAngle = a[j].downBendingAngle;
                            b[j].minBendingAngle  = a[j].minBendingAngle;
                            b[j].maxBendingAngle  = a[j].maxBendingAngle;
                        }
                    }
                }

                // ターゲットの写像
                var tgt = rn.target;
                if (tgt == null) { fn.target = null; return; }
                if (realF.objNeckLookTarget != null && tgt == realF.objNeckLookTarget.transform)
                {
                    // 固定方向モード: 実側の保存パラメータで参照側の自前ターゲットを再構成（自己相対＝写像不要）
                    var st = realF.fileStatus;
                    _refCha.ChangeLookNeckTarget(st.neckTargetType, null, st.neckTargetRate, st.neckTargetAngle, st.neckTargetRange);
                    return;
                }
                if (tgt.IsChildOf(realF.transform))
                {
                    // 実女性自身の骨（自分の股間等）→ 参照女性の同名骨
                    var m = _refCha.objBodyBone != null
                        ? _refCha.objBodyBone.transform.FindLoop(tgt.name) : null;
                    if (m != null) { fn.target = m.transform; return; }
                }
                // それ以外（実男性の骨・カメラ等シーン側）→ 参照空間の等価位置にプロキシを置く。
                // 実男性の頭/股間はワールド実位置をそのまま objTop 相対で写像＝参照男性なしでも
                // 「実ペアで男の骨があった相対方向」を参照女性から再現できる（実位置ぶん近似が良い）。
                if (_refNeckProxy == null) _refNeckProxy = new GameObject("HCAEx_RefNeckTarget");
                var realTop = realF.objTop; var refTop = _refCha.objTop;
                if (realTop == null || refTop == null) return;
                _refNeckProxy.transform.position = refTop.transform.TransformPoint(
                    realTop.transform.InverseTransformPoint(tgt.position));
                fn.target = _refNeckProxy.transform;
            }
            catch (System.Exception e)
            {
                if (!_neckMirrorWarned)
                {
                    _neckMirrorWarned = true;
                    Plugin.Logger.LogWarning(
                        "[HCharaAdjustmentEx] 首ミラー失敗 → 首制御なしで採取続行（口測定に頭サイズ誤差が残る・要調査）: " + e.Message);
                }
            }
        }

        // ── ライフサイクル（Hooks の ResetSceneState / EndProc から） ──
        internal static void OnSceneReset()
        {
            for (int i = 0; i < 3; i++) ResetIdx(i);
        }

        internal static void OnSceneEnd()
        {
            OnSceneReset();
            _sampling.Clear();
            _shiftCache.Clear();   // 実測シフトはカード×体位×ステート依存＝シーン限り
            _coupCache.Clear();
            _failed.Clear();       // 採取不能ステートも measKey がカード依存＝跨いでも当たらない＝毎シーン破棄
            if (_refContainer != null) Object.Destroy(_refContainer);
            if (_refNeckProxy != null) Object.Destroy(_refNeckProxy);
            _refNeckProxy     = null;
            _sampleBusy       = false;
            _neckMirrorWarned = false;
            _coMeasure        = false;
            _coRealF          = null;
            _refContainer     = null;
            _refCha           = null;
            _refBodyBound     = -1;
        }
    }
}
