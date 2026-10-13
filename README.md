# ContactsLoading

![ContactsLoading](https://raw.githubusercontent.com/MarkN2000/ContactsLoading/main/icon.png)

[Resonite](https://resonite.com/)のContacts（フレンド欄）の初期生成を複数の更新に分散し、タブを開いたときのフリーズを軽減するBepisLoader用Modです。

未読あり → オンライン → その他の順で生成します。オンラインにはSociable・Online・Away・Busyを含みます。生成中の行は非表示にし、優先分と残りをそれぞれ完成後にまとめて表示します。タブやダッシュを閉じても生成は続きます。

## 導入

1. [BepisLoader](https://thunderstore.io/c/resonite/p/ResoniteModding/BepisLoader/) 1.7.0以降を導入します。
2. [Releases](https://github.com/MarkN2000/ContactsLoading/releases/latest)から `ContactsLoading-0.3.0.zip` をダウンロードし、GaleのローカルMod取り込みで選びます。DLL単体ではなくZIPを取り込んでください。
3. 依存ModとしてBepInExResoniteShim 0.9.3・BepisResoniteWrapper 1.0.2・[Bepis Mod Settings](https://thunderstore.io/c/resonite/p/ResoniteModding/BepisModSettings/) 1.6.6以降を導入します。

既に依存Modが入っている場合も、上記の版以上になっていることを確認してください。

## 設定

Bepis Mod Settingsで **ContactsLoading → 生成 → 1更新あたりの件数** を変更できます。

| 項目 | デフォルト | 説明 |
| --- | --- | --- |
| 1更新あたりの件数 | **10** | 1回の更新で生成する最大件数。少ないほど負荷を分散し、多いほど全件の生成が早く完了します。 |

1以上の整数を指定してください。0以下は1として扱います。変更は次の分割生成から反映され、優先分・残りの両方に適用されます。次回起動時も使う場合は、Bepis Mod Settingsで保存してください。

## 対応状況

Resonite **2026.9.18.82**を対象にしています。最終ソートと一括表示時の負荷は残ります。実機での表示・性能、およびContactsを変更する他Modとの併用は未検証です。

## ライセンス

[MIT License](https://github.com/MarkN2000/ContactsLoading/blob/main/LICENSE) — Copyright (c) 2026 markn2000。

[markn`s homepage](https://markn2000.com/posts/2025-09-08-resonite-mod-list/)
