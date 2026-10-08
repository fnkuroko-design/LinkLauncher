# LinkLauncher 0.1.1-dev.6 検証記録

日付: 2026-10-08。Windows / WPF / .NET 10。dev.1〜dev.5と正式版v0.1.0を保持。

## 変更

- 常時最前面ではない他ウィンドウの裏に潜る報告を受け、表示時のZ順操作を前面化の成否から独立させた。毎回引き上げて通常帯の先頭へ戻し、以前の背面位置を復元しない。実前面HWNDを成功判定に用いる。
- AttachThreadInputを削除。表示直後とマウスの呼び出しボタンを全て放した後に一度ずつ補正し、新規クリック・非表示・要求世代の変更で古い補正を取り消す。
- 追加・編集ウィンドウでFileDropをPreviewイベントとして受け付ける。入力欄上でもファイル／フォルダ1件をリンク先へ入力する。既存の名前は保持し、複数件は案内して上書きしない。保存は従来の保存操作時のみ。

## 実施した確認

- 統合Release / win-x64ビルド: 警告0、エラー0。
- ActivationCompletionChecks: 4/4 PASS。解放とUI表示のどちらが先でもコールバックが1回だけ有効になること、次の要求・キャンセル・null・二重解放を確認。
- MouseChecks: 7/7 PASS。既存のボタン順序、移動によるドラッグ復元、通常クリックへの復元を確認。
- EditorDropChecks: 4/4 PASS。画面を表示しないSTAのWPFテストで、実LinkEditorのリンク先欄・名前欄からPreviewイベントを発火し、Windowへのトンネル、ファイル名補完、フォルダ判定、既存名保持、複数件案内、存在しないパス・内部形式・非FileDrop拒否を確認。ドロップだけではResultとライブラリが変更されないことも確認した。
- EditorDropChecksの初回ビルドはテストが公開コンストラクターを前提にして失敗した。実際の内部DragEventArgsコンストラクターを[公式WPFソース](https://source.dot.net/PresentationCore/System/Windows/DragEventArgs.cs.html)で確認してテスト側だけを修正し、上記4件が通過した。Run/Show/ShowDialog/UIAutomationは使わず、各イベント後もWindowが非表示であることを確認した。
- サブエージェントの読取レビューで、再試行の取り消しが表示要求自体も取り消す経路を見つけ、表示要求と再試行の有効性を分けて修正した。
- Computer Useは隔離データの最終確認に限定。エクスプローラーからCtrl + Alt + Spaceで呼び出し、前面化補助を使わずに検索欄のフォーカスとdev.6画面を確認した。その際の隔離データは自動非表示を無効にして、観測中のフォーカス移動でウィンドウが消えないようにした。
- 自動非表示を有効にした初回の観測では取得前にウィンドウが隠れたため、呼び出し失敗とは判定していない。後続の追加ボタン操作は別画面との重なりで実行できず、再取得でもuser input detectedが通知されたため自動操作を停止した。

## 確認範囲

裏に表示される元の再現条件は特定できていないため、全条件で解消したとの判定はしていない。実マウスによる呼び出しの低レベルフック経路、複数DPI環境などの網羅確認は行っていない。

エクスプローラーから実際にドラッグしてドロップする操作は、画面操作の競合のため未完了。フォームへの入力処理とWPFイベント経路の確認とは区別する。

前面化とZ順は[SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow)と[SetWindowPos](https://learn.microsoft.com/ja-jp/windows/win32/api/winuser/nf-winuser-setwindowpos)のMicrosoft仕様に基づく。恒常的な最前面設定やOS制限の解除は行わない。

## 配布

- `scripts/Publish.ps1` で最終ソースのRelease / win-x64 / framework-dependent publish成功。警告・エラーなし。
- ZIP: `artifacts/releases/LinkLauncher-v0.1.1-dev.6-win-x64.zip`、170,622 bytes（約166.6 KiB）。展開フォルダーは369,434 bytes（約360.8 KiB）。
- FileVersion `0.1.1.6`、ProductVersion `0.1.1-dev.6+b0b75919369a2fda3788d7e018d031230edb1dab`。ソースコミットとローカルタグ `v0.1.1-dev.6` が一致する。
- ZIPの9ファイルを期待リストと照合し、各エントリーを展開読取できることを確認。実行用4ファイル、README、導入HTML、MIT LICENSE、THIRD_PARTY_NOTICES、.NETライセンスを含み、ランタイム本体・ユーザーデータ・PDBを含まない。
- runtimeconfigはMicrosoft.NETCore.AppとMicrosoft.WindowsDesktop.Appの10.0.0共有フレームワークを要求する。
- SHA-256記録と再計算値が一致: `6ae95447522e090bb9e6fe75b0f0d8cd795c86cdcd5beaad32c5268214488bf3`。
- dev.1〜dev.5と正式版v0.1.0の旧ZIP6件は従来のハッシュと一致。
- 検証用インスタンスを終了し、配布EXEを通常の保存先で `--background` 起動。Windowsスタートアップの登録先は変更していない。

本開発版はローカル配布物として作成し、GitHubへ公開していない。
