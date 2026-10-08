# LinkLauncher 0.1.1-dev.5 検証記録

日付: 2026-10-08。Windows / WPF / .NET 10。dev.1〜dev.4と正式版v0.1.0を保持。

## 変更

- 呼び出し経路を共通化し、前面化APIの成否を確認して補助処理を行う。表示後に一度だけ再試行し、古い表示要求・閉じる要求を世代で無効化する。
- 外側クリックの監視を追加し、コンテキストメニューが閉じた後にも自動非表示を再判定する。固定・モーダル・ドラッグ中の扱いを保つ。
- カテゴリの同階層並び替えとリンクの並び替えを、ドラッグ／右クリックメニューの上下移動で行う。ローカルJSONへバックアップ付きで保存する。
- 常時タイマー、ネットワーク、ファイル走査、外部パッケージを追加しない。

## 実施した確認

- Release / win-x64ビルド: 警告0、エラー0。
- CoreChecks: 7/7 PASS。MouseChecks: 7/7 PASS。順序処理を追加したOrderChecks: 5/5 PASS。
- OrderChecksで旧データの従来順、同じ親内の移動、絞り込みに隠れたリンクの順序保持、追加・編集時の順序、保存後の再読込とバックアップ、不正な負の順序の拒否を確認。初回の期待値不一致は日本語文字列の文化依存順を前提にしたテスト用データを修正した。
- Computer Useは隔離データで最終UIに限定。初回表示後の検索欄フォーカス、リンクCの先頭へのドラッグ、展開中のカテゴリAの上へのカテゴリBのドラッグを確認し、JSONのOrderと親カテゴリが正しく保存されたことを確認した。
- ダーク表示の右クリックメニューに「上へ移動」「下へ移動」が表示され、対象が一件だけの一覧では無効になることを確認した。
- 隔離データで自動非表示を有効にして再起動後、確認用メモ帳からCtrl + Alt + Spaceで呼び出し、Computer Useの前面化補助を使わずに検索欄がフォーカスされたことと保存した順序の維持を確認した。
- 複数リンクの操作メニューで先頭の「上へ移動」が無効、「下へ移動」が有効であることを確認。そのメニューを開いた状態から確認用メモ帳をクリックし、ランチャーが非表示（MainWindowHandle=0）で常駐を続けることを確認した。UI自動化の入力は注入入力のため、実マウスの低レベルフック経路を検証したものではない。

## 確認範囲

実マウスのフック入力と、管理者権限の画面、複数DPI環境など全条件を網羅した実機確認は行っていない。Windowsの前面化制限を解除する仕組みではなく、常時最前面を強制しない。[MicrosoftのSetForegroundWindow仕様](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow)を前提にした処理である。

正式版v0.1.0の公開ZIPは変更せず、本版を次の動作確認用の開発版として作成する。

## 配布

- `scripts/Publish.ps1` によりRelease / win-x64 / framework-dependent ZIPを作成。警告・エラーなし。
- ZIP: `artifacts/releases/LinkLauncher-v0.1.1-dev.5-win-x64.zip`、169,474 bytes（約165.5 KiB）。展開フォルダーは366,188 bytes（約357.6 KiB）。
- FileVersion `0.1.1.5`、ProductVersion `0.1.1-dev.5+0c8ba621b7362c1acb497ea5bcdaaa4f0767b247`。ソースコミットとローカルタグ `v0.1.1-dev.5` の対象が一致する。
- ZIP内の9ファイルを期待リストと照合。実行用4ファイル、README、導入HTML、MIT LICENSE、THIRD_PARTY_NOTICES、.NETライセンスを同梱。ランタイム本体・ユーザーデータ・PDBを含まない。
- runtimeconfigは `Microsoft.NETCore.App` と `Microsoft.WindowsDesktop.App` の10.0.0共有フレームワークを要求する。
- SHA-256記録と再計算値が一致: `c56238ecacf94ed1d8bba18865ff02ffcbedb01892a77f6535fbfcee76b1b0e1`。
- dev.1〜dev.4と正式版v0.1.0の旧ZIPは従来のハッシュと一致。
- 検証用インスタンスを終了し、配布EXEを通常の保存先で `--background` 起動。Windowsスタートアップの登録先は変更していない。

本開発版はローカル配布物として作成し、GitHubへ公開していない。
