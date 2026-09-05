using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace KK_HCharaAdjustmentEx
{
    internal static class AdjustmentStore
    {
        internal class Entry
        {
            public string  key  = "";
            // f1/f2/male = 絶対デルタ（保存時にユーザーが見た位置そのもの、vanillaRootローカル系）。
            // 完全一致キーへはこれをそのまま適用する（自動補正を参照しない＝設定変更に不動）。
            // ゼロ = 未調整（そのキャラは自動補正の経路）。
            public Vector3 f1;
            public Vector3 f2;
            public Vector3 male;
            // 保存時の体位 posture（-1=不明）。同キャラ組の姿勢グループ平均に使う。
            public int     posture = -1;
            // 保存時の自動補正シフト（ローカル系）。別キャラへ流用する際に
            // 残差 = 絶対デルタ − 保存時シフト を再構成するためのメタ。男は常にゼロで不要。
            public Vector3 refShiftF1;
            public Vector3 refShiftF2;
        }

        // 形式(v1.6.0〜):
        //   体位行 16列: {key}={f1 3},{f2 3},{male 3},{posture},{refShiftF1 3},{refShiftF2 3}
        //   15列以下の体位行（v1.5以前）は保存セマンティクスが違うため読み込まない（要再調整）。
        //   base_ 行（v1.7〜v1.12 のベース補正・v1.13で廃止）は読込時に無警告スキップ＝次回保存で自動消去。
        private static string      _path    = "";
        private static List<Entry> _entries = new List<Entry>();

        // 保存のたびに増える版数。推定キャッシュの無効化に使う。
        internal static int Version { get; private set; }

        internal static void Initialize(string configPath)
        {
            // BepInEx の設定ファイル（{GUID}.cfg）と衝突しない別名にする
            _path = Path.Combine(configPath, "KK_HCharaAdjustmentEx_data.txt");
            Load();
            Plugin.Logger.LogInfo(
                "[HCharaAdjustmentEx] " + _entries.Count + " entries loaded from " + _path);
        }

        internal static Entry? GetEntry(string key) =>
            _entries.Find(e => e.key == key);

        // 完全一致エントリを削除（手動リセット）。存在した場合 true。
        internal static bool ClearEntry(string key)
        {
            int i = _entries.FindIndex(e => e.key == key);
            if (i < 0) return false;
            _entries.RemoveAt(i);
            Version++;
            WriteFile();
            return true;
        }

        // キー分解: {mode}_{animId}_{animName}_{animSig}_{f1}_{f2}_{male}
        // 各成分は Sanitize 済み（英数字のみ）なので '_' 区切りで正確に 7 要素になる。
        internal static bool TryParseKey(string key, out string anim, out string cards)
        {
            anim  = "";
            cards = "";
            var p = key.Split('_');
            if (p.Length != 7) return false;
            anim  = string.Join("_", p, 0, 4);
            cards = string.Join("_", p, 4, 3);
            return true;
        }

        // ①用: 同一体位（anim 部一致）×別カード組のエントリ
        internal static List<Entry> FindSameAnimOtherCards(string anim, string cards)
        {
            var r = new List<Entry>();
            foreach (var e in _entries)
                if (TryParseKey(e.key, out var a, out var c) && a == anim && c != cards)
                    r.Add(e);
            return r;
        }

        // ②用: 同一カード組×同 posture の別体位エントリ
        internal static List<Entry> FindSameCardsSamePosture(string cards, int posture, string excludeKey)
        {
            var r = new List<Entry>();
            if (posture < 0) return r;
            foreach (var e in _entries)
                if (e.key != excludeKey && e.posture == posture &&
                    TryParseKey(e.key, out _, out var c) && c == cards)
                    r.Add(e);
            return r;
        }

        // 推定値を初確定するとき、画面に効いていた推定デルタごとエントリ化する
        // （メモリ上のみ。直後の SaveComponent が書き出す）
        internal static void SeedEntry(string key, Vector3 f1, Vector3 f2, Vector3 male)
        {
            if (_entries.Find(e => e.key == key) != null) return;
            _entries.Add(new Entry { key = key, f1 = f1, f2 = f2, male = male });
        }

        // キャラ1人分（idx 0=f1,1=f2,2=male）だけ更新（他キャラは保持）。
        // refShiftF1/refShiftF2 は保存瞬間の自動補正シフトで毎回上書き（残差再構成メタ）。
        internal static void SaveComponent(string key, int idx, Vector3 v, int posture,
            Vector3 refShiftF1, Vector3 refShiftF2)
        {
            var entry = _entries.Find(e => e.key == key);
            if (entry == null)
            {
                entry = new Entry { key = key };
                _entries.Add(entry);
            }
            if      (idx == 0) entry.f1   = v;
            else if (idx == 1) entry.f2   = v;
            else               entry.male = v;
            if (posture >= 0) entry.posture = posture;
            entry.refShiftF1 = refShiftF1;
            entry.refShiftF2 = refShiftF2;
            Version++;
            WriteFile();
        }

        private static void Load()
        {
            _entries.Clear();
            if (!File.Exists(_path)) return;
            int skippedLegacy = 0;
            int migrated      = 0;
            try
            {
                foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;

                    string   k     = line.Substring(0, eq);
                    string[] parts = line.Substring(eq + 1).Split(',');

                    // 旧ベース行（v1.7〜v1.12・v1.13で廃止）は無警告スキップ。次回保存で消える。
                    if (k.StartsWith("base_")) continue;

                    if (parts.Length < 16)
                    {
                        if (parts.Length >= 9) skippedLegacy++;   // v1.5以前=残差保存で意味が違う
                        continue;
                    }

                    // 0-8: 絶対デルタ / 9: posture / 10-15: refShiftF1, refShiftF2
                    float[] v  = new float[16];
                    bool    ok = true;
                    for (int i = 0; i < 16; i++)
                    {
                        if (i == 9) continue;   // posture は int として別処理
                        if (!float.TryParse(parts[i], NumberStyles.Float,
                                CultureInfo.InvariantCulture, out v[i]))
                        { ok = false; break; }
                    }
                    if (!ok) continue;

                    // v1.20.4: 男が参加しないモード（レズ/自慰）のキーから男を落とす移行。
                    // 旧版はセッションの残りカスの男が載っていたので、そのままでは二度と一致しない
                    //（＝ユーザーが手で追い込んだ値が読めなくなる）。**メモリ上だけ**で読み替え、
                    // ファイルへは次の通常保存のときに正規化された形で書かれる（読込時に書き戻さない）。
                    string norm = HSceneHooks.NormalizeKey(k);
                    if (norm != k)
                    {
                        if (_entries.Exists(e => e.key == norm))
                        {
                            // 同一体位・同一カード組で男違いの保存が複数ある＝先勝ちで1つだけ残す。
                            // どちらが「本物」かは決められないので握り潰さず必ず知らせる。
                            Plugin.Logger.LogWarning(
                                "[HCharaAdjustmentEx] キー移行: 男違いの重複を破棄（先に読んだ方を採用）"
                                + " 破棄=" + k + " 採用=" + norm + "（要確認）");
                            continue;
                        }
                        migrated++;
                        k = norm;
                    }

                    var entry = new Entry
                    {
                        key    = k,
                        f1     = new Vector3(v[0],  v[1],  v[2]),
                        f2     = new Vector3(v[3],  v[4],  v[5]),
                        male   = new Vector3(v[6],  v[7],  v[8]),
                        refShiftF1 = new Vector3(v[10], v[11], v[12]),
                        refShiftF2 = new Vector3(v[13], v[14], v[15])
                    };
                    if (int.TryParse(parts[9], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int posture))
                        entry.posture = posture;
                    _entries.Add(entry);
                }
                if (skippedLegacy > 0)
                    Plugin.Logger.LogWarning("[HCharaAdjustmentEx] 旧形式(v1.5以前)の " + skippedLegacy +
                        " 行をスキップ（v1.6で保存セマンティクス変更＝絶対デルタ化。該当体位は要再調整）");
                if (migrated > 0)
                    Plugin.Logger.LogInfo("[HCharaAdjustmentEx] キー移行: レズ/自慰の " + migrated +
                        " 件から男を落として読み替えた（v1.20.4。次の保存でファイルも新形式になる）");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[HCharaAdjustmentEx] Load failed: " + e.Message);
            }
        }

        private static void WriteFile()
        {
            try
            {
                var lines = new List<string>(_entries.Count);
                foreach (var e in _entries)
                    lines.Add(e.key + "=" + Fmt(e.f1) + "," + Fmt(e.f2) + "," + Fmt(e.male) +
                              "," + e.posture.ToString(CultureInfo.InvariantCulture) +
                              "," + Fmt(e.refShiftF1) + "," + Fmt(e.refShiftF2));
                File.WriteAllLines(_path, lines.ToArray(), Encoding.UTF8);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[HCharaAdjustmentEx] Save failed: " + e.Message);
            }
        }

        private static string Fmt(Vector3 v) =>
            v.x.ToString("R", CultureInfo.InvariantCulture) + "," +
            v.y.ToString("R", CultureInfo.InvariantCulture) + "," +
            v.z.ToString("R", CultureInfo.InvariantCulture);
    }
}
