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

## 実行場所の相違への対応（input-g、実機確認待ち）

input-fのデスクトップ空白でのポインター観測では、対象Explorer PID 11528/TID 11532の通知が本体PID 3256と補助PID 9488のinstaller側で実行された。共有取得やtarget照会の失敗ではなく、対象PID/TIDと実行PID/TIDの一致ガードで除外されていた。MouseProc/SetWindowsHookExの仕様はinstallerスレッドでの実行を許すため、この一致を全ての処理の前提にする設計は誤りだった。

編集対象は`BridgeHook.c`、`BridgeHook.h`、`NativeMouseChord.cs`、プロジェクトと配布ファイルの定義、診断ビルド限定の`DesktopIntegration.cs`。64bitフック一本でsource側またはinstaller側に届く通知を処理する。32bitフック・補助プロセスは起動せず、重複した状態更新を避ける。32bit/64bitの通知先の相違を特定アプリの例外にしない。

- 呼び出しの候補は通常の右DOWNで作り、右UPで解除する。LEFTDOWN時はそのキュー上の順序とtarget照合から右押下を判断し、installer側のGetKeyStateをsource側の状態として使わない。中・追加ボタンは同じ通知列で追跡する。二重クリックの2回目のDOWN通知も押下として扱う。
- sourceスレッド自身のcaptureは従来の照合後に解除する。別スレッドで実行される場合はGetGUIThreadInfoでsource側のcaptureを確認し、PID/TID/rootが対象と一致する捕捉窓にだけ同期WM_CANCELMODEを送る。再照会でcaptureが解除されない場合、照会不成立やtimeoutの場合はLEFTDOWNを消費せず、通常の入力を通す。右DOWN時点の編集や、アプリ独自の内部フラグの取消しを保証しない。
- 低レベルフックの物理DOWN/UPは通す。呼び出し時だけWH_MOUSEのウィンドウ宛てLEFTDOWNと対応UPを抑止する。この区別によりWindowsの非同期ボタン状態を残さない設計だが、改善は実機で確認する。診断版では完了直後と25msタイマー後に右・左のGetAsyncKeyStateを読み、high bitが消えているか観測する。新しいボタン入力があれば後の確認を飛ばす。low bitは押下中判定に使わない。
- capture取消しとUI通知には経過時間から残り75msを渡す。同じメッセージキューへのSendMessageTimeoutではtimeoutが無視され、source自身のReleaseCaptureも同期処理を含むため、フック全体の厳密な75ms上限とは報告しない。通常の右クリック・右ドラッグの経路にはこれらの同期処理を加えない。
- source側で実行される場合だけforeground権限付与を行う。installer側では既存の表示・一時Topmost・再活性化処理を使うが、前面化の成功を保証しない。表示、元メニューの抑止、解放後の左クリック、Windowsのボタン状態を別々に確認する。

input-gはソース`1d8e3f3c6d81bf0cd96b3f01eab8bc858ec3fb30`からビルド・起動済み。5個の配布対象バイナリだけを試作アプリのフォルダーへ配置し、補助プロセスは起動していない。初期のコンパイルではSHORTの符号付きマスク変換が警告をエラーとして検出したため、起動時の追加ボタン状態照会をUSHORTとunsigned maskに修正した。最終ビルドは警告・エラー出力なし。入力通知の到達を読み取り専用で確認したが、右＋左の実機受入はまだ未確認。受入は従来どおりで、デスクトップでの呼び出しが改善してから、Codexの移動なしの通常メニューとPDF手書きの静止右DOWN通知・ドラッグ開始を少数の手動操作で確認する。診断を正式配布物へ残さない。

gの`contextSkipped`はsource側ではないコールバックの観測数であり、installer側で処理を続けた場合も含む。fの同名カウンターと異なり、入力を全て処理対象外にした回数として解釈しない。

## メニュー生成の順序の観測（input-h）

gのユーザー確認では2回とも表示され左クリックも使えたが、左押下の時点で元のデスクトップメニューが開き、元画面での左クリックまで残った。受入未達。アプリ終了後にも同じ右保持→左押下でデスクトップのメニューが開いたという比較報告がある。これを右DOWNで編集する特殊アプリの問題として除外しない。

右DOWNを既に通しているため、元アプリの内部状態をUP抑止だけで必ず正常化できない。gのログは物理ボタンの解放を確認できたが、メニュー生成がHC_NOREMOVE時の照会、HC_ACTIONより前の標準処理、またはcapture解除で始まったかを記録していない。source側のReleaseCaptureをWM_CANCELMODEへ置換する案はまだ実装せず、hでは通知順序だけを受動観測する。物理ボタン状態自体が解除されたとの断定と、元アプリが右クリックを完了扱いにしたとの観測は区別する。

`BRIDGE_INPUT_PROBE`限定のWH_CALLWNDPROCはメッセージを変更せずCallNextHookExへ渡し、対象PID/TIDに3秒の観測窓を設ける。通常WH_MOUSEのHC_NOREMOVEも引き続きそのまま通す。記録のGetTickCountとcapture解除前後を比較し、WndProc通知が観測できなければ原因を確定したと扱わない。汎用的なWM_CANCELMODEの標準動作はメニュー処理取消とcapture解除だが、アプリ独自の処理を元に戻す保証ではない。正式版にはこの観測機構を残さない。

## 操作の取消通知（input-i）

hの1回のデスクトップ再現では、左押下受理・capture解除・WM_CAPTURECHANGEDと同じtickの47ms後にWM_CONTEXTMENU、さらにメニュー開始を観測した。左右UPはその後に来て消費され、Windowsの非同期状態は両方0だった。従ってこのメニューはUPだけの抑止で防げず、単なるcapture解除で元アプリが右クリックを完了扱いにした可能性が高い。これは元アプリの内部コードを直接観測した確定原因とは区別する。

iの変更対象は`BridgeHook.c`のsource-context処理。捕捉ウィンドウのPID/TID/rootを照合してWM_CANCELMODEを送り、GetCaptureがNULLへ変わることを確認する。直接のReleaseCaptureは使わず、installer側と同じ標準取消プロトコルへ揃える。入力の再送・移動・全窓への取消・アプリ別例外は使わない。通知失敗・残存captureでは状態をrejectし、入力を通す。WM_CANCELMODEを処理しない独自UIや捕捉なしの独自右DOWN状態は取消を保証しない。実機受入はまだ未確認で、まずデスクトップの最小確認を行う。

## 呼び出し元の標準メニュー終了（input-j）

iの最小確認でもランチャーと元メニューの両方が出た。WM_CANCELMODEとWM_CAPTURECHANGEDの到達後、31msで新たなWM_CONTEXTMENU、次にメニュー開始が起きた。取消は受理済みでも、元の右DOWNから始まった処理が後からメニューを生成した。右UPはそれより後に消費され、ボタン状態は解放された。

jでは操作取消しに加え、WH_CALLWNDPROCで受理済みの呼び出し元の標準popupメニュー開始を認識し、source自身のスレッドでEndMenuを呼ぶ。ACTIVE/受理済み/未解放、PID/TID/root一致、callback実行PID/TID一致、開始から2秒未満で限定する。単独右クリックや別窓には適用しない。フックチェーンの通知は変更せず次へ渡す。登録できない場合は入力仲介を有効にしない。共有構造体のlayoutは維持し、statusにmenu-hook bit 16を追加、C#はmask 21を確認する。

EndMenuは呼んだスレッドのactive menuを終了するAPIであり、元アプリの処理を全般的に巻き戻す機能ではない。独自描画メニュー・保護されたアプリ・installer側にしか通知が来ない場合には同じ保証をしない。正式版では順序観測・カウンター・ログを除去し、必要な標準メニュー終了の処理を残す。

## 解放後のメニューと最初の通常クリック（input-k）

input-jのユーザー確認ではデスクトップの最初の呼び出しはランチャーだけを表示し、解放後の左クリックも使えた。一方、リンク以外のボタンは最初のクリックが効かず、タスクバーではランチャーと元メニューが両方出た。再確認でメニューの見た目の表示は左押下の瞬間ではなく、両ボタンを離してから少し動かした時点と報告された。既存の3秒・同一TIDの順序観測だけでは、この実操作とメニュー通知の一対一の対応は確定していない。

kでは、ウィンドウ宛てのUPを管理するBRIDGE_STATEとは別に、受理済みの元root/PID/TIDをBRIDGE_MENU_GUARDへ保持する。UI ACKとBridgeStateAcceptが成立した場合だけ設定する。ランチャーを表示した呼び出しは、解放やポインター移動でこの情報を失わない。次の物理ボタンDOWN・ホイール、ランチャー内のキー入力、登録ホットキー、ランチャーを隠す操作、停止で消去する。標準のキーボード由来WM_CONTEXTMENU（lParam=-1）も消去対象にする。呼び出しによってランチャーを隠した場合に限り、開始から2秒以内の取消しとする。別ウィンドウ・別PID/TID・ランチャー自身のメニューには適用しない。

この情報は物理ボタンの押下状態ではなく、物理入力やUPを保留・再送・注入しない。WH_CALLWNDPROCは通知をそのまま次へ渡し、照合したsourceスレッドでのみEndMenuを呼ぶ。独自描画メニュー、別スレッド・別rootへ委譲されたメニューは未対応の可能性があり、タスクバーを特別扱いして照合を緩めない。共有layoutはversion 3 / 136 bytesへ更新し、bitness間の固定幅と8-byte alignmentを維持する。

最初のボタンについては、native pendingが残っている間のLL早期returnより前に、新しいDOWNによる古いActivationCompletionの取消しを移す。新しいDOWNは外側クリックの判定など通常処理にも進める。旧nativeセッションが次のLEFTDOWNで完了しても、旧検索欄フォーカスの完了callbackを実行しない。ButtonBaseがフォーカス喪失でIsPressed/captureを取り消す実装と症状は整合するが、改善は手動確認前に断定しない。

試作限定でメニュー判定のkind 8（bit 0:対象、1:本体表示、2:root一致、3:TID一致）、9（通知側root）、10（通知側TID）、11/12（受理したsource root/TID）を記録する。診断のPostMessageはstate lock解放後に実行する。正式配布から観測コードとログを除去する。

### タスクバーの公式仕様の調査（2026-10-11）

Microsoftの資料には、アプリアイコンの右クリックでジャンプリストを開くこと、Shift＋右クリックでウィンドウメニューを開くこと、タスクバー空白の右クリックから設定を開くことが記載されている。一方、調べた公式資料には「右保持→左クリック→両解放後の移動」で表示する順序や内部のスレッド・root・Win32 menu APIの保証は見つからなかった。一般のDefWindowProcが右UPでWM_CONTEXTMENUを作る仕様を、タスクバー内部の仕様と同一視しない。Q&Aの投稿を公式契約の証拠に採用しない。

- [Windowsのタスクバー操作](https://support.microsoft.com/en-us/windows/keyboard-shortcuts-in-windows-dcc61a57-8ff0-cffe-9796-cb9706c75eec)
- [ジャンプリスト](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/jump-list)
- [タスクバーのカスタマイズ](https://support.microsoft.com/en-au/windows/experience/personalization/customize-the-taskbar-in-windows)
- [WPF ButtonBaseの公式ソース](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/ButtonBase.cs)

## 非表示後の連続呼び出しとキュー経路の観測（input-l）

input-kの実機確認では、デスクトップ空白の元メニュー抑止、リンク以外のボタンの最初のクリック、静止した通常右クリックは改善した。ただしタスクバーの解放後移動によるメニューは未解決で、HideOnPointerLeave有効時にはデスクトップ/Explorerの連続呼び出しが成功・失敗を交互に繰り返した。

ログの成功seq 45（source root 6751010 / PID 11528 / TID 30112）ではEndMenu成功後の旧window UPが記録されず、次のRIGHTDOWN（339711ms）で旧seqのCOMPLETE、次のLEFTDOWNで候補なしの拒否が発生した。seq 46にも同じパターンがある。BridgeStateOnRightDownはACTIVEかつ同じroot/PIDの場合だけ、旧stateを消して早期returnしていた。新しい右押下が旧stateを消すだけの操作になることが、交互失敗と整合する。

lでは同じ対象の新しいRIGHTDOWNも旧seqをsupersedeした後、新しいCANDIDATEへ進める。物理RIGHTDOWNは引き続き通過し、新しい単独RIGHTUPや移動によるドラッグの通知も通す。古いwindow UPを補う入力注入・カーソル移動・擬似クリックは使わない。ボタンを保持中に本体が隠れる可能性があるため、非表示通知で無条件にACTIVEを消す対処は採用しない。古い完了callbackのフォーカス移動を防ぐkの処理を維持する。

タスクバーの記録はsource root 65988 / TID 13300で受理・左右UP完了となり、読み取り専用のWin32窓情報ではShell_TrayWnd、その子にComposition/InputSite/CoreWindowのclassが存在した。これはWM_POINTER経路や独自メニューが原因と断定する証拠ではない。kのメニュー照合kind 8にはroot/TID不一致の拒否は記録されておらず、単にsource照合を全Explorerへ緩める根拠はない。

試作だけに受動WH_GETMESSAGEを登録する。PM_REMOVEのキュー通知を読むだけでMSGは変更せず、常にCallNextHookExへ渡す。source PID内の別TIDを含め、右/左・非クライアントのDOWN/UP、WM_CONTEXTMENU、WM_POINTERDOWN/UP/CAPTURECHANGED、解放後の最初のMOVE/POINTERUPDATEを10秒以内で限定して記録する。kind 14は元のwParam、15は通知root、16はTID、stage 55は登録結果。既存CALLWNDPROCは従来の同一TIDの範囲で観測する。これは原因の記録であり、メニュー取消範囲の拡大ではない。正式版にはGETMESSAGE観測hook・全診断ソース・ログを含めない。

### 離席中の自動検証と復旧条件

ユーザーは、マウスのクリック機能とカーソル表示が維持される、または事故時に確実に復旧して返せる場合に限り、検証用の入力生成を許可した。元の座標や物理押下状態への復元を要求したものではない。許可は更新されたが、公開Computer Use APIには右保持＋左押下の操作がなく、無断で非公開helperプロトコルを拡張しない。API初期化のみで、マウス/キーボード入力・カーソル移動・画面の活性化は行っていない。今回の対象動作の自動実機合否は確認できず、ビルドと純状態試験後に手動試験の段階でユーザーの指示どおりゴールを一時停止する。

### input-lのメニュー所有先観測とinput-mの取消通知

input-lの手動確認はデスクトップ連続3回・通常右クリックが正常、タスクバー空白とスタートでは元メニューが残り、アプリアイコンでは残らなかった。配布外の180秒の受動WinEvent観測で、空白/スタートの順の両試行はsource root 65988 / PID 11528 / TID 13300のInputSite窓66420からEVENT_SYSTEM_MENUPOPUPSTARTを通知し、同じsource rootをownerとするXaml_WindowedPopupClassの窓1705038/1770574を表示した。class名で対処を分岐する根拠にはしない。GUI thread infoのmenuOwnerは0で、従来の標準menu開始通知はなかった。

1回目はseq 18の右UPがtick 39404484、popup開始が39404625（141ms後）。2回目はseq 19の右UPが39416203、popup開始が39417109（906ms後）。フックのボタン解放完了とメニュー実表示を同じ時点と扱わず、非同期のメニュー表示イベントを取消の契機にする。

mは所有者側スレッドのWINEVENT_OUTOFCONTEXTでEVENT_SYSTEM_MENUSTART/END/MENUPOPUPSTARTの狭い範囲を登録し、処理するのはSTARTとPOPUPSTARTのみ。受理済みMenuGuardのroot/PID/TIDに通知先が一致し、eventTidも一致、イベント時刻が受理以降かつ現在時刻以前で、次の通常入力による消去/非表示条件にも合う場合だけ、その通知窓とsource rootへWM_CANCELMODEを送る。二つの送信は合計25msの予算で、各送信前に対象・guardを再照合し、hook内の取消再入を避ける。sourceは別プロセスで、SMTO_ABORTIFHUNG | SMTO_BLOCKを使用する。配送成功をメニュー終了成功と同一視しない。

元のWM_CANCELMODEによるcapture取消と標準メニューのEndMenuを保持する。ポップアップのWM_CLOSE/破壊、擬似Escape/クリック/UP、カーソル移動、特定class/appの条件分岐は導入しない。通常の右DOWN/ドラッグの通過とwindow UPの仲介はlから変更しない。メニュー実表示後のout-of-context通知のため一瞬の表示や独自UIの通知非対応は残り得る。実機の取消合否を別途確認する。

登録失敗ではnative hooksを停止し開始失敗を報告する。ready statusは53（mouse 1 + enabled 4 + CallWnd menu 16 + WinEvent menu 32）へ変更、停止時にUnhookWinEventとstatus解除を行う。stage 56とtrace kind 17/18は試作用の登録・配送結果で、正式版では全診断とともに除去する。

純状態試験は12/12。新しいケースは非同期イベントの受理前/別root/PID/TID/非表示/消去/再受理前の旧イベントとtick wrap/未来時刻を拒否する境界を確認する。Windowsの配送と独自メニューの取消結果はこの試験では確認しない。

### input-mの限界と次の設計判断

タスクバー空白/スタートで元メニューが残るとのユーザー報告に対し、source子窓とrootの両WM_CANCELMODE配送は成功しsource threadのtraceも記録された。mで独自popupを取消できたとは扱わない。デスクトップでの連続呼び出し/最初のボタン/通常右クリックはユーザーが正常と確認した。

MicrosoftのWinUI focus設計資料のWindowed Popups / Light Dismissは、popup自身がWin32 focusを取らず元islandにfocusを維持し、LostFocusでlight dismissする設計とその依存を説明する。本taskbar実装の断定はせず、前面が本体へ移った後にsource popupが開く現象を考える根拠として扱う。通常取消に応じない任意のUIに対し、統一された外部取消APIを確認できていない。UI Automation Menuにはrequired control patternsがないので、UIA Collapse/Window.Closeへ置き換えれば一律解決とは扱わない。

次の候補は、受理済みsourceのpopup開始時だけ実際にsource→本体の活性化を行う、または即時表示を非活性にして最初の本体クリックで通常の活性化を行う方式。前者はsourceへのfocusイベント再発と本体dismiss/前面復帰の管理、後者はクリック前の検索入力の変更を伴う。どちらも未実装。WM_KILLFOCUSを偽造したり、AttachThreadInputで入力状態を共有したり、WM_CLOSEでsource popup窓を強制破壊する処理は追加していない。

### input-n: ユーザー選択による即時表示を維持する前面往復

ユーザーは①「即時表示/検索入力を維持し、元のpopup表示時だけsourceを活性化して本体へ戻す試作」を選択した。mの通知取消に加え、EVENT_SYSTEM_MENUPOPUPSTARTを受けたownerスレッドからのみ実行する。guardのroot/PID/TID/イベント時刻、source窓の生存/表示、本体の表示/前面、全マウスボタンの物理解放を確認する。GUI thread infoで標準menu modeの場合は除外し、既存EndMenuを優先する。guardの旧reservedスロットをfocusAttemptedにして受理ごとに1回に制限する（shared layout/version/sizeは3/136を維持）。

owner通知phase 3の同期scope内からBridgeRestoreMenuFocusを呼ぶ。native側は当該WinEvent callback中のrequestとowner PID/TIDを確認するので、単なるWindowMessageだけでsourceを選んで活性化はできない。開始前にAllowSetForegroundWindowを自分のPIDへ要求し、失敗ならsourceへ移らない。SetForegroundWindow(source root)の後、WM_NULLをSMTO_ABORTIFHUNG | SMTO_BLOCKで最大50msだけ送り、非同期活性化をsourceが処理する機会を与える。sourceの実foregroundとfocus rootを記録し、同じguardが有効・本体表示・元のrootまたは本体がforeground・GetLastInputInfoの時刻が変わっていない場合にだけ本体へのSetForegroundWindowを要求し、実foregroundで復帰結果を確認する。新しい入力や無関係なforegroundを検知した場合は奪い返さない。フォーカス復帰が必ず成功するという保証はしない。

MainWindowはscope中だけDeactivatedによる自動dismissを抑止し、finallyで解除する。pointer-leave pendingはscope後に通常の判定へ戻す。old ActivationCompletionはキャンセルし、復帰成功時はscope前の入力要素にKeyboard.Focusを戻すが、検索文字列/選択をリセットしない。modal、内部menu、順序drag、非表示、古いvisibility generationでは行わない。前面復帰できない場合に第三窓の入力を奪う無期限retryはしない。

マウス/キーの生成、カーソル/ボタン状態操作、AttachThreadInput、偽造focus通知、OS設定変更、popup破壊、app/class例外は追加していない。sourceには実activation/focusイベントが再発する。stage 19の診断flagsはsource foreground 1 / source focus 2 / owner return 4 / return grant 8 / request 16 / abort on changed input/state 32。C#の診断結果とともに正式配布から除去する。API戻り値とメニュー取消成功は区別する。

純状態試験13/13。新caseはボタン保持/別foreground/標準menu/非表示/古いイベント/別rootを拒否し、同一guardの重複を実行せず次の受理では再実行可能なことを確認する。sourceへの実activation/メニューのlight dismiss/検索/最初のクリックは実機で別途必要。

## 一次資料

- [LowLevelMouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc): 次のフックへの受け渡しと抑止。
- [MouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/mouseproc): GetMessage/PeekMessage段階のマウスメッセージ処理とHC_ACTION。
- [CallWndProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/callwndproc): ウィンドウ手続きに渡る前の受動観測。メッセージの変更はできない。
- [GetMsgProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/getmsgproc): キューから取り出したMSGの観測。試作は変更・消費しない。
- [WM_POINTERUP](https://learn.microsoft.com/en-us/windows/win32/inputmsg/wm-pointerup): pointer通知とmouse通知を同一視せず、部分的な消費で挙動が未定義となる点も踏まえて観測を先行する。
- [EndMenu](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-endmenu): 呼んだスレッドのactive menuを終了する。
- [WM_ENTERMENULOOP](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-entermenuloop): popupメニュー開始の通知。
- [WM_INITMENUPOPUP](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-initmenupopup): メニュー表示前の初期化通知。
- [SetWindowsHookExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw): 他プロセスのフックと32bit・64bitの制約。
- [SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook): out-of-context通知とmessage loop、停止時の解除。
- [Event Constants](https://learn.microsoft.com/en-us/windows/win32/winauto/event-constants): menu表示イベント。各UIのイベント発生は実際の観測で確認する。
- [WinUI focus design](https://github.com/microsoft/microsoft-ui-xaml/blob/main/docs/design-notes/focus.md): windowed popupとlight dismissのfocus依存。taskbar固有の実装仕様とは区別する。
- [SetForegroundWindowと非同期処理](https://devblogs.microsoft.com/oldnewthing/20161118-00/?p=94745): 別queueへの活性化は非同期になり、WM_NULLによるbounded waitと実foreground確認を使う。
- [AllowSetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow): foreground権限の付与と新しい入力による失効。
- [GetLastInputInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo): session内の最終入力時刻。変化を中止条件に使い、単調増加やボタン解放の証拠とは扱わない。
- [UI Automation Menu control type](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-supportmenucontroltype): Menuに必須のcontrol patternはない。
- [入力再送の順序](https://devblogs.microsoft.com/oldnewthing/20121206-00/?p=5903): 物理UPと後挿入DOWNの順序が押しっぱなしを生む例。
- [GetAsyncKeyState](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate): ボタン状態と照会失敗。対象アプリの押下フラグとは区別する。
- [WM_CANCELMODE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-cancelmode): 標準のキャンセル処理の範囲。
- [SendNotifyMessageW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendnotifymessagew): 別スレッドの処理完了を待たない通知。
- [WM_CONTEXTMENU](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-contextmenu): 標準右UPによるメニュー生成。
- [SetCapture](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcapture): foregroundでのキャプチャと入力の受け先。
- [SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow): 前面化の制限と、条件を満たしても拒否される場合。
- [Mouse Input Overview](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-mouse-input): 別スレッドの窓への実クリックによる活性化・capture解除。
- [WM_MOUSEACTIVATE](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate): 非活性窓への通常クリックによるDOWN前の活性化。

## input-o: 入力先と前面ウィンドウを分けた受理

input-nの実機報告では、タスクバー空白の元メニュー抑止と即時検索入力は改善したが、スタートボタンは交互に元メニューが出た。退出で閉じた直後のファイルアイコン上では本体が出ない。ログのseq 2/4/11/17/21はstage 30 detail 1で表示要求前に拒否され、記録された6回のfocus transferはすべて31だった。操作場所と各seqの対応は未確定であり、両症状の原因が確定したとは扱わない。

従来の受理はLEFTDOWNの実配送先PIDとforeground PIDの一致を要求した。右DOWNを受けても前面にならない窓、または本体をHideした直後の前面状態では、正しい入力先の組合せも拒否される。この条件を共通処理で改め、実配送先のHWND/root/PID/TIDと可視rootを取消前後で照合し、foregroundはLEFTDOWN時点から変化していないことを確認する。foregroundが別プロセスまたはNULLでも、それだけでは拒否しない。取消中のforeground変更、対象消滅・変更、capture不一致/解除失敗、ACK失敗は引き続き拒否して入力を通す。

表示ACK後のsource側前面化も、保存したforegroundが変わっていない場合に要求する。foreground PIDによる制限は設けないが、Windowsによる前面化拒否はあり得る。右DOWNの即時通過、5pxの呼び出し候補判定、通常右UP/ドラッグの通過、受理済みwindow入力の対応、遅延メニュー取消・前面往復とWPFのscopeは変更しない。座標往復・SendInput・ボタンUP生成・アプリ別例外は導入しない。

診断限定のtrace kind 20/21/22は、入力先PIDとforeground PIDが異なるときのforeground HWND/PID/source rootを記録する。正式配布へは残さない。純状態遷移には変更がないので既存13ケースは再実行せず、nativeの警告をエラーにするビルドと本体publishを行う。スタートボタンの連続操作と、退出後のファイルアイコン上での呼び出しはユーザーの手動実機確認まで未合格とする。

## input-p: 前面化権限の復路での受け渡し

input-oのユーザー確認では退出後のアイコン呼び出しと静止右クリックはすべてOKだが、タスクバー空白/スタートは不安定だった。保存ログに表示前のstage 30/32失敗はなく、22件のBEGINが成立していた。focus resultは31が5件、27が7件。27は元rootへの前面化と元root内のfocusが確認できた一方、本体への復帰をその場で確認できなかったことを表す。APIの戻り値は未記録なので、拒否理由や非同期復帰まで確定したとは扱わない。操作場所とseqの対応も確定していない。

編集前に対象BridgeHook.cと文書、現在の処理、失敗段階、権限受け渡し変更と受入条件を説明した。既存の元root宛WM_NULL応答待ちの間だけ、menu guardのfocusAttemptedへ一時要求値2を設定する。WH_CALLWNDPROCが実配送先root/PID/TID、callbackのPID/TID、現在のforeground=root、表示中の本体、受理済みguard、物理ボタン全解放を照合し、要求を1へ消費してAllowSetForegroundWindow(ownerPid)を呼ぶ。通常WM_NULLや未受理対象には権限を渡さず、通知/フック結果は元の処理へ通す。要求の終了はatomic CASで保証し、状態lock競合があっても値2を残さない。消去/再armの値0は上書きしない。

元スレッドでの活性化・入力生成は追加しない。元画面が実際に前面になった後に復帰先を許可し直す試作であり、Windowsの前面化拒否を強制的に無効化するものではない。元画面待ち50ms、新しい入力/guard変更/非表示/第三者foregroundでの中止、本体側の限定dismiss抑止、即時検索入力を維持する。判定前の右DOWN、単独右クリック、右ドラッグ、受理済み解放の処理は変更しない。shared ABIのサイズ/バージョンも維持する。

試作限定trace kind 23は元スレッドの権限付与結果、focus resultのbit 64は復路SetForegroundWindowの戻り値。bit 4は従来どおり実foreground一致である。95はAPI成功かつ一致、91はAPI成功だが即時一致なし、27はAPI失敗かつ一致なし。正式版へ診断を残さない。純状態遷移は変更せず再実行しない。ビルド成功と、タスクバーでの元メニュー抑止/本体前面復帰/即時入力の実機合格は区別する。
