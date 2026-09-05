using System.Collections.Generic;
using UnityEngine;

namespace KK_HCharaAdjustmentEx
{
    // ─── レズ（mode=5）結合点の診断ログ ─────────────────────────────────────
    // 【位置は一切動かさない。計測して1回だけログを出すだけ。既定 OFF】
    //
    // 目的: レズに自動補正が要るのかを、実装前に数値で確かめる。
    //
    // レズには SEX の男のような「動かない剛体アンカー」が無い。代わりに本体が
    // 相互身長ブレンドを持っている（Game_HLes.cs:128-136）:
    //     SetAnimatorFloat(female,  "height1", female1.GetShapeBodyValue(0));
    //     SetAnimatorFloat(female1, "height1", female.GetShapeBodyValue(0));
    // ＝各女性の Animator に「相手の身長」を毎フレーム供給する（#252 の height の
    // レズ版かつ双方向）。よって正解は「標準体の位置へ寄せること」ではなく
    // 「相手の結合点と出会うこと」であり、測るべきは参照ボディとの差ではなく
    // 【女2人の結合点どうしの実距離】になる（＝参照ボディも採取コルーチンも要らない）。
    //
    // 判定の仕方: 帯域内どうしのペアで1回、帯域外の子を含むペアで1回まわして比べる。
    //   ・帯域外でも距離が同程度      → 相互 height1 が効いている＝無介入が正解（クローズ）
    //   ・帯域外で距離が明確に開く    → その体位・その結合点に補正余地あり。
    //     どのペアの数値が開いたかが、そのまま結合点と役割（舐め手/受け手）の答えになる。
    //
    // 役割は決め打ちせず候補ペアの距離を全部出す。推測で結合点を決めて実装すると、
    // 外れたときに「補正が悪いのか役割判定が悪いのか」を切り分けられないため。
    internal static class LesDiag
    {
        private const int LesbianMode = 5;
        private const int Frames      = 90;    // 蓄積フレーム数（同一 体位×ステート で1回だけ出す）
        // 蓄積前に捨てるフレーム数。自動補正は「解析シード → 同時採取の精密値」へ収束するので、
        // ステートを見た直後から数えると収束前の姿勢が平均に混ざる。補正後の残差を測るために待つ。
        private const int WarmFrames  = 120;

        private sealed class Acc
        {
            public int     warm;
            public int     n;
            public float[] dist = new float[PairCount];
            public float[] dy   = new float[PairCount];
        }

        // 候補ペア（測る側の結合点 → 相手の結合点）。役割は決め打ちしない。
        private const int PairCount = 5;
        private static readonly string[] PairName =
        {
            "kokan↔kokan", "F1mouth↔F2kokan", "F2mouth↔F1kokan", "F1hand↔F2kokan", "F2hand↔F1kokan",
        };
        // {F1 側の結合点, F2 側の結合点}
        private static readonly int[,] PairCoup =
        {
            { RefAlign.CoupKokan, RefAlign.CoupKokan },
            { RefAlign.CoupMouth, RefAlign.CoupKokan },
            { RefAlign.CoupKokan, RefAlign.CoupMouth },
            { RefAlign.CoupHand,  RefAlign.CoupKokan },
            { RefAlign.CoupKokan, RefAlign.CoupHand  },
        };

        private static readonly Dictionary<string, Acc> _acc  = new Dictionary<string, Acc>();
        private static readonly HashSet<string>         _done = new HashSet<string>();

        internal static void OnSceneEnd()
        {
            _acc.Clear();
            _done.Clear();
        }

        // Plugin.LateUpdate から毎フレーム（RefAlign.HookMeasureTick と同位相＝
        // 実キャラの IK/首 LateUpdate より前の素のアニメ姿勢）。
        internal static void Tick()
        {
            if (Plugin.LesbianDiagLog == null || !Plugin.LesbianDiagLog.Value) return;
            var flags = Plugin.CurrentFlags;
            if (flags == null) return;

            ChaControl? f1, f2;
            try
            {
                if (flags.lstHeroine == null || flags.lstHeroine.Count < 2) return;
                if (flags.nowAnimationInfo == null || (int)flags.nowAnimationInfo.mode != LesbianMode) return;
                f1 = flags.lstHeroine[0]?.chaCtrl;
                f2 = flags.lstHeroine[1]?.chaCtrl;
            }
            catch { return; }
            if (f1 == null || f2 == null) return;

            var anim = f1.animBody;
            if (anim == null) return;
            int stateHash;
            try { stateHash = anim.GetCurrentAnimatorStateInfo(0).shortNameHash; }
            catch { return; }
            if (anim.IsInTransition(0)) return;   // 遷移中は姿勢が混ざる

            string key = HSceneHooks.BuildKeyFor(flags.nowAnimationInfo, f1, f2, null) + "|" + stateHash;
            if (_done.Contains(key)) return;

            if (!_acc.TryGetValue(key, out var a)) { a = new Acc(); _acc[key] = a; }
            if (a.warm < WarmFrames) { a.warm++; return; }   // 補正の収束待ち

            for (int i = 0; i < PairCount; i++)
            {
                var pa = RefAlign.CouplingPos(f1, PairCoup[i, 0]);
                var pb = RefAlign.CouplingPos(f2, PairCoup[i, 1]);
                if (pa == null || pb == null) return;   // 骨が揃わない＝このフレームは丸ごと捨てる
                Vector3 d = pa.Value - pb.Value;
                a.dist[i] += d.magnitude;
                a.dy[i]   += d.y;
            }
            a.n++;
            if (a.n < Frames) return;

            _done.Add(key);
            _acc.Remove(key);
            Report(key, f1, f2, a);
        }

        private static void Report(string key, ChaControl f1, ChaControl f2, Acc a)
        {
            float s1 = RefAlign.MeasureScaleDirect(f1);
            float s2 = RefAlign.MeasureScaleDirect(f2);
            var   sb = new System.Text.StringBuilder();
            sb.Append("[HCharaAdjustmentEx] レズ診断 ").Append(key).Append('\n');
            sb.AppendFormat("  sF1={0:F4}({1}) sF2={2:F4}({3}) n={4}\n",
                s1, BandStr(s1), s2, BandStr(s2), a.n);
            sb.Append("  ");
            for (int i = 0; i < PairCount; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.AppendFormat("{0} d={1:F3} dy={2:+0.000;-0.000}",
                    PairName[i], a.dist[i] / a.n, a.dy[i] / a.n);
            }
            Plugin.Logger.LogInfo(sb.ToString());
        }

        private static string BandStr(float s) =>
            s <= 0f ? "測定不能" : RefAlign.InBand(s) ? "帯域内" : "帯域外";
    }
}
