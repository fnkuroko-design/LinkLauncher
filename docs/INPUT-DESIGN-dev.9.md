# dev.9 右＋左呼び出し後の解放状態の調査

日付: 2026-10-10。dev.8配布後の実機報告に基づく追加調査。修正・実機改善の完了記録ではない。

## ユーザーの意図とdev.8記録の訂正

- 通常の右クリック・右ドラッグでは、右DOWNを遅らせず、通常の押下・移動・解放を維持する。
- 右ボタンを押しながら左クリックした場合は、LinkLauncherを呼び出し、元アプリの右クリックメニューを開かない。
- 両ボタンを離した後は、追加の右クリックなしでLinkLauncher内・元アプリ・Windowsの左クリックを使える。
- 許容された競合は、右DOWNで動作を開始するアプリの、呼び出し判定前の副作用。メニューの同時表示、押しっぱなし、解放漏れ、Windows全体の左クリック停止を許容したものではない。

dev.8の設計・検証文書は、独自の押下状態の残留まで受容済みとして記録した。その解釈は広すぎた。本記録で訂正する。完成済みdev.8のZIP・ソース履歴は保持する。

## 実機報告とコード上の確認を分ける

ユーザー報告:

- 右＋左でLinkLauncher自体は表示される。
- 元アプリの右クリックメニューも開く。Windowsデスクトップでも再現する。
- 両ボタンを離しても左クリックがWindows全体で効かず、もう一度右クリックすると使える。

コード上で確認できたこと:

- 右DOWNは次の低レベルフックへ渡す。
- 左DOWNによる呼び出し成立時、WM_CANCELMODEの通知要求が成功すると右UP・左UPを抑止する。
- 右DOWNは通過しているため、対応する右UPを止めるとアプリへの押下・解放が不均衡になる。
- SendNotifyMessageの成功は外部スレッドの処理完了を保証しない。WM_CANCELMODEは標準メニュー処理・キャプチャ等のキャンセルであり、ボタン解放そのものではない。
- 呼び出し後の再前面化処理は解放イベントを待つ補助であり、Windowsや元アプリのボタン状態を復元しない。

今回の手動再現と、入力を加工しない観測ツールで確認できたこと:

- デスクトップ空白で再現し、メニューが開くのは左ボタンを押した時点だった（ユーザー報告）。
- 右DOWNは下流フックを通過したが、呼び出しの左DOWN・左UP・右UPは下流で抑止された。
- 抑止された右UPの25.778ms後もGetAsyncKeyStateの右ボタン値は0x8000だった。次の通常右クリックのUPが通過した後は0となった。
- 2回目の呼び出しでも右UPの35.246ms後に0x8000が残り、その後の左DOWN/UP自体はフックを通過していた。
- 観測ツールは180秒で自身のフックを解除して終了した。常駐dev.8、設定、ユーザーデータは変更していない。

観測ログ: `artifacts/diagnostics/dev9-right-release-f5f088db90c541968abf9c77d6f02319/observation.log`。この環境での物理操作の証拠であり、全アプリ・全Windows環境を検証した記録ではない。

未確定:

- 元アプリ内でメニューを発生させた正確な処理と、キャンセル通知処理との時間関係。
- 新方式で実機のメニュー同時表示と左操作停止が改善するか。観測結果や模擬試験だけで改善済みとは扱わない。

## 編集対象と一時的な観測

最初の追加は `tests/RightReleaseDiagnostics/` の独立した調査用ツールだけ。配布アプリやPDF手書き・Codexのコードを変更しない。

- 低レベルフックで左右ボタンのDOWN/UPだけを観測し、CallNextHookExの戻り値をそのまま返す。観測ツール自身は入力を消費しない。
- ボタンイベント直前、およびイベント後のタイマーでGetAsyncKeyStateを照会し、前後の値を記録する。API照会値を対象アプリの内部状態と同一視しない。
- 入力生成・カーソル移動・キャプチャ変更・前面化・他プロセス終了を行わない。移動やキーボードを記録しない。
- 時間制限で終了し、自身のフックとタイマーを解除する。ログはローカル調査用で、正式配布物へ含めない。
- 手動確認は、再現する画面での右＋左1回、両ボタン解放後の左クリック、復帰用右クリックの範囲を基本とする。

観測の受入条件は、入力を加工しないこと、抑止とその後の状態を区別できること、常駐版・設定・データを変えずに終了できること。観測ツールのビルドをアプリ修正の成功とは報告しない。

## dev.9の変更方式

編集前に対象と受入条件を説明し、以下を実装する。

- `DesktopIntegration.cs`: 成立時の左DOWNだけを消費する。右UP・左UPは低レベルフックで止めない。通常の右入力の保留・再送・座標往復は導入しない。
- `ChordReleaseReceiver.cs`: UIスレッドで非表示の1×1の独立ウィンドウをあらかじめ用意する。呼び出し成立時に押下位置へ表示し、実際のforegroundとcaptureが自身であることを確認する。全画面の透明窓やカーソル移動は使わない。
- 解放の受け先を確保できた場合のみ左DOWNを消費し、左右UPをWindowsに処理させて受け取り窓のWndProcで受領する。WndProcで通常のメニュー生成を実行しない。両UPを受け取った後に自身のcaptureを解放し、窓を非表示にする。
- 受け先を確保できない場合は入力を消費しない。capture喪失・破棄・2秒の安全タイマーで中断する場合も、自身が持つcaptureだけを解放する。解放の注入はしない。
- `MousePressCancellation.cs`: 対象ウィンドウの同一性とcaptureの照合だけを残し、WM_CANCELMODE通知を廃止する。
- `tests/ChordReleaseChecks/` と `tests/MouseCancellationChecks.cs`: 解放順序・重複・開始失敗・capture喪失・timeout・破棄・対象照合を模擬APIで検証する。実機のWindows入力経路の代用とはしない。

受入条件は、右＋左で呼び出せること、元アプリのメニューが同時に開かないこと、解放後の次の左クリックが通常どおり使えること、通常の右クリックと右ドラッグに保留・再送がないこと。静止中の押下通知、通常メニュー表示、右ドラッグの開始をそれぞれ評価する。右DOWN時点で実行された元アプリの編集処理の取り消しは保証しない。

SetCaptureの成功照会を、元アプリで始まった右押下の後続UP配送の保証とは扱わない。ランチャー本体の表示中にcaptureが維持されることも実機確認する。capture喪失・2秒の安全タイマー後はWindowsの解放を通すことを優先し、メニュー抑止の保証をしない。試作の正常な短い呼び出しで要件を満たすかを確認してから配布を確定する。

## 一次資料

- [LowLevelMouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc): 次のフックへの受け渡しと抑止。
- [GetAsyncKeyState](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate): ボタン状態と照会失敗。対象アプリの押下フラグとは区別する。
- [WM_CANCELMODE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-cancelmode): 標準のキャンセル処理の範囲。
- [SendNotifyMessageW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendnotifymessagew): 別スレッドの処理完了を待たない通知。
- [WM_CONTEXTMENU](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-contextmenu): 標準右UPによるメニュー生成。
- [SetCapture](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcapture): foregroundでのキャプチャと入力の受け先。
