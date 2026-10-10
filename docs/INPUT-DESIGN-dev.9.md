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

## dev.9の最初の変更方式（input-a〜c、不合格）

編集前に対象と受入条件を説明し、以下を実装する。

- `DesktopIntegration.cs`: 成立時の左DOWNだけを消費する。右UP・左UPは低レベルフックで止めない。通常の右入力の保留・再送・座標往復は導入しない。
- `ChordReleaseReceiver.cs`: UIスレッドで非表示の1×1の独立ウィンドウをあらかじめ用意する。呼び出し成立時に押下位置へ表示し、実際のforegroundとcaptureが自身であることを確認する。全画面の透明窓やカーソル移動は使わない。
- 解放の受け先を確保できた場合のみ左DOWNを消費し、左右UPをWindowsに処理させて受け取り窓のWndProcで受領する。WndProcで通常のメニュー生成を実行しない。両UPを受け取った後に自身のcaptureを解放し、窓を非表示にする。
- 受け先を確保できない場合は入力を消費しない。capture喪失・破棄・2秒の安全タイマーで中断する場合も、自身が持つcaptureだけを解放する。解放の注入はしない。
- `MousePressCancellation.cs`: 対象ウィンドウの同一性とcaptureの照合だけを残し、WM_CANCELMODE通知を廃止する。
- `tests/ChordReleaseChecks/` と `tests/MouseCancellationChecks.cs`: 解放順序・重複・開始失敗・capture喪失・timeout・破棄・対象照合を模擬APIで検証する。実機のWindows入力経路の代用とはしない。

受入条件は、右＋左で呼び出せること、元アプリのメニューが同時に開かないこと、解放後の次の左クリックが通常どおり使えること、通常の右クリックと右ドラッグに保留・再送がないこと。静止中の押下通知、通常メニュー表示、右ドラッグの開始をそれぞれ評価する。右DOWN時点で実行された元アプリの編集処理の取り消しは保証しない。

SetCaptureの成功照会を、元アプリで始まった右押下の後続UP配送の保証とは扱わない。ランチャー本体の表示中にcaptureが維持されることも実機確認する。capture喪失・2秒の安全タイマー後はWindowsの解放を通すことを優先し、メニュー抑止の保証をしない。試作の正常な短い呼び出しで要件を満たすかを確認してから配布を確定する。

## foreground拒否後の試作（input-d、不合格）

input-cで対象照合は成功したが、foreground要求後も元の画面が前景のままであり、capture前に不成立となった。複数回のフックを受信し、処理時間は約3〜8msだった。実測に基づき、強制的な前面化を呼び出し成立の前提にした方式を改める。

- `ChordReleaseReceiver.cs`: TOPMOST・SHOWWINDOW・NOACTIVATEで物理押下点へ小さな受け取り窓を出す。TryBegin成功はnative LEFTDOWNを受ける準備の意味へ変更する。
- `DesktopIntegration.cs`: 候補の物理LEFTDOWNもWindowsへ通す。受け取り窓が実際にWM_MOUSEACTIVATE→WM_LBUTTONDOWNを受け、foreground/captureを確認したStartedイベントだけでランチャーの表示を開始する。通常のマウスによる活性化を使う。
- native DOWN不達には250msのWindowsタイマー、成立後には2秒のWindowsタイマーを設定し、中断時に受け取り窓を隠す。これらはUIメッセージの処理で実行されるため、UIスレッドが応答しない場合の厳密な壁時計上限ではない。成立後の物理右UP・左UPはWindowsへ通し、自身のcaptureで受けて終了する。解放順序・重複・再押下・喪失・Disposeの後始末を検証する。
- 入力生成・カーソル移動・キー状態を変更するAttachThreadInput・アプリ別例外を導入しない。

同一の物理LEFTDOWNの処理中に作った窓へヒットテストを切り替えられるかは、公式のLowLevelMouseProc資料では保証されない。入力が自身へ配送されることをデスクトップ空白で確認する。受け取り窓への到達を確認しないまま呼び出し成立や修正完了とは扱わない。

実機結果は2回とも表示されず、元の右メニューが出た。準備は成功したが、受け窓へのWM_MOUSEACTIVATE/WM_LBUTTONDOWNは届かず、WaitingForNativeDownのままタイマーで中断した。この環境で同一の物理DOWNの配送先をフック途中から切り替える方式は不成立と記録する。

## 後段のメッセージ処理を使う試作（input-e、実機不合格）

対象は `DesktopIntegration.cs`、新規の `NativeMouseChord.cs`、`src/LinkLauncher.InputBridge/`、ビルド設定と配布スクリプト。input-dの受け窓方式は使わない。物理入力の配送先を途中から変えることと、LEFTDOWNの注入による代用を避ける。単独DOWNの注入では、既に入力列にある物理UPより後にDOWNが処理され、押しっぱなしを作る危険があるため採用しない。

- WH_MOUSE_LLでは右＋左のDOWN/UPを通す。Windowsが物理入力を処理した後、GetMessage/PeekMessage段階のWH_MOUSEで呼び出しに当たるLEFTDOWNと対応UPのウィンドウ通知だけを消費する。HC_ACTIONだけを扱い、通常右クリック・右ドラッグは通す。5pxは呼び出し候補の取り消し条件で、ドラッグの引き渡し待ちには使わない。
- 32bit・64bitのWinAPI専用DLLと32bitの補助プロセスで両bitnessを扱う。C#本体は.NET 10のまま。VCランタイムや.NETランタイムを同梱しない。対象アプリのコード、個別除外設定、入力生成、カーソル移動は使わない。
- 同一ルートウィンドウ内で右DOWN→左DOWNとなった場合だけ候補を成立させる。共有状態の短い排他区間にウィンドウ操作や同期送信を入れない。呼び出し元のスレッドが所有するcaptureだけを解放し、UIへ上限75msの同期通知で表示結果を問い合わせる。表示後、元のforegroundプロセスから前面化を要求する。元のウィンドウへのWM_CANCELMODEは使わない。
- 呼び出しの開始・終了を世代番号と左右の解放状態で照合する。開始失敗は通常入力を通す。UP処理や時間経過だけでは解放待ち状態を消さず、長押し後の対応UPも処理する。新しいDOWNでは前の要求を終了させ、新しいクリック組を通す。Windowsの入力列を消費しないことを、対象アプリ独自の内部押下フラグが必ず解消することと同一視しない。

呼び出し元が右DOWNで取得したcaptureは、UP通知の抑止後も残るとランチャーへの次のクリックを元アプリへ配送するおそれがあるため解除する。解除は呼び出し元スレッド自身が所有するcaptureに限定する。WM_CAPTURECHANGEDに対する元アプリの反応は実機で未確認。同期UI通知のtimeoutや再入により、UI受理より前に元アプリのUPが処理された場合のメニュー抑止も保証しない。

受入条件は、デスクトップで繰り返し呼び出せること、元メニューが出ないこと、解放後の左クリックが正常であること。その後、Codexの移動なしのメニュー、PDF手書きの静止中の右DOWN通知と短い右ドラッグをそれぞれ確認する。模擬状態遷移とビルド成功だけで実機改善とは扱わない。Raw Inputや保護されたアプリ、異なる権限のウィンドウを含む全アプリへの保証はしない。

2026-10-11のユーザー確認では2回とも表示されず、元の右メニューが出た。両bitnessの登録を示すstatus=7と低レベルの左右DOWNのログはあるが、nativeからのstage通知は無かった。診断版の入口計数がないため、フック未到達と共有マッピング・対象スレッド照合による早期returnをまだ区別できない。表示方式の調整や追加の入力試験で成功扱いにせず、input-fでこの到達段階を調べる。

input-fの編集対象は`BridgeHook.c`の診断ビルド限定部分。固定幅の共有カウンターに入口回数と早期returnの理由、最後の対象・実行PID/TIDを保持する。外部からの読み取りで確認し、ファイルへの記録・同期通知をフック入口へ加えない。入力消費や呼び出し方式はeのままで、修正実装の成功試験とは数えない。受入条件は診断セクションの存在と共通layout、負のcodeの即時受け渡し、正式ビルドへの診断混入がない構造である。

## 一次資料

- [LowLevelMouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc): 次のフックへの受け渡しと抑止。
- [MouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/mouseproc): GetMessage/PeekMessage段階のマウスメッセージ処理とHC_ACTION。
- [SetWindowsHookExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw): 他プロセスのフックと32bit・64bitの制約。
- [入力再送の順序](https://devblogs.microsoft.com/oldnewthing/20121206-00/?p=5903): 物理UPと後挿入DOWNの順序が押しっぱなしを生む例。
- [GetAsyncKeyState](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate): ボタン状態と照会失敗。対象アプリの押下フラグとは区別する。
- [WM_CANCELMODE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-cancelmode): 標準のキャンセル処理の範囲。
- [SendNotifyMessageW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendnotifymessagew): 別スレッドの処理完了を待たない通知。
- [WM_CONTEXTMENU](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-contextmenu): 標準右UPによるメニュー生成。
- [SetCapture](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcapture): foregroundでのキャプチャと入力の受け先。
- [SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow): 前面化の制限と、条件を満たしても拒否される場合。
- [Mouse Input Overview](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-mouse-input): 別スレッドの窓への実クリックによる活性化・capture解除。
- [WM_MOUSEACTIVATE](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate): 非活性窓への通常クリックによるDOWN前の活性化。
