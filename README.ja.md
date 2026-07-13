# KK_HCharaAdjustmentEx

[English](README.md) | **日本語**

> Koikatsu の H シーンで、体格が標準の範囲外のキャラを**自動で位置合わせ**し、さらに自分で行った**手動の位置調整をキャラ×体位ごとに保存**できる BepInEx プラグインです。

---

## 概要

H シーンでは体位によってキャラの位置がズレることがあります。特に身長スライダーのアンロック MOD で作った体（極端に小柄／大柄）で顕著です。本プラグインは2つのことを行います。

- **自動補正** — 体格が標準の範囲外のキャラを、その体位に合わせて自動で位置合わせします。標準の範囲内のキャラには介入しません（ゲーム本体が正しく処理するため）。
- **手動調整** — 好きなキャラの位置を自分で微調整して保存できます。保存した位置は、同じキャラ×同じ体位で自動的に再適用されます。

補正は**スムーズに**適用されます（急な瞬間移動はしません）。

> 本プラグインは **KK_HCharaAdjustment**（作者: deathweasel）の**拡張アドオン**です。位置調整ガイド（女の子・男の表示）は派生元が提供し、本プラグインが自動位置合わせと手動調整の保存・再適用を担います。ライセンスは後述の [License](#license) を参照。

---

## 動作環境

| 項目 | 要件 |
|---|---|
| ゲーム | Koikatsu（HF Patch） |
| 実行ファイル | `Koikatu.exe`（フル）/ `KoikatuVR.exe`（適用のみ） |
| フレームワーク | BepInEx 5.4.x |
| **必須依存** | **KK_HCharaAdjustment**（ガイドを提供） |

> KK_HCharaAdjustment がガイド（女1=`O` / 女2=`P` / 男=`I`）を提供します。手動編集（保存）は非VRビルドでのみ動作します（VR は適用専用）。

---

## 導入方法

1. **KK_HCharaAdjustment**（派生元）を導入済みであることを確認。
2. [Releases](../../releases) から最新の `KK_HCharaAdjustmentEx.dll` をダウンロード。
3. `BepInEx/plugins/` フォルダに配置。
4. ゲームを起動。

---

## 使い方

### 自動補正
特に操作は不要です。体格が標準の範囲外のキャラは、H シーン中に自動で位置合わせされます。`Auto Adjust > Enabled` でオフにできます。

### 手動調整
1. H シーン中、動かしたいキャラのガイドを表示（女1=`O` / 女2=`P` / 男=`I`）。
2. ガイドを掴んでキャラを動かす。
3. 次のいずれかで**保存**：
   - **右 Ctrl + S** を押す、または
   - 画面上部中央に出る **Save** ボタンをクリック、または
   - そのまま**別の体位に変える** — 動かしたキャラは自動保存されます（オフ可）。
4. 現在の体位の保存を取り消すには、**右 Ctrl + 右 Shift + S**、または **Reset** ボタン。

- 保存した調整は **キャラの組み合わせ×体位ごと**に保存され、その体位では自動補正より優先されます。
- 保存は非VRビルドが必要です。

---

## 設定

BepInEx の **ConfigurationManager**（既定 `F1`）で開けます。

| セクション | 項目 | 既定値 | 説明 |
|---|---|---|---|
| General | Enabled | ON | プラグイン全機能の有効/無効（OFF=バニラ） |
| Auto Adjust | Enabled | ON | 自動の位置合わせ |
| Auto Adjust | Shift Cap | OFF | 補正のかけすぎを抑える（浮き・離れの防止） |
| Auto Adjust | Mouth Shift Scale | 0.8 | 口の位置合わせの強さ（0〜1・小さいほど控えめ） |
| Manual Adjust | Buttons Show | ON | 画面の Save/Reset ボタンを表示 |
| Manual Adjust | Position Auto Save | ON | 体位変更時に動かした位置を自動保存 |
| Manual Adjust | Position Save | RCtrl+S | 手動調整を保存するキー |
| Manual Adjust | Position Reset | RCtrl+RShift+S | 手動調整をリセットするキー |

---

## 注意点

- 自動補正は男がバニラの範囲に収まる必要があります。
- 自動補正は完全ではありません。特に奉仕では種類によりズレが発生する場合があります。その際は手動調整を行ってください。
- 保存データは `BepInEx/config/` 内のテキストファイルにあり、手動で編集・削除も可能です（変更後はゲーム再起動で反映）。

---

## License

本プラグインは [KK_Plugins_CN](https://github.com/PopChicken/KK_Plugins_CN)（作者: PopChicken）に含まれる **KK_HCharaAdjustment** を派生元とする拡張実装です。

派生元と同じく **GNU General Public License v3.0** の下で配布します。

- 改変・再配布は GPL v3.0 の条件に従う必要があります。
- 配布時はソースコードの提供が必要です。
- 著作権表示とライセンス情報を保持してください。

[GNU General Public License v3.0](LICENSE)

---

## 免責

本 Mod は H シーン向けのアダルト（R18）コンテンツです。自己責任でご利用ください。