# LinkLauncher v0.1.2-dev.9 検証記録（開発中）

日付: 2026-10-10。Windows x64 / .NET SDK 10.0.204。

## 原因の実機観測

dev.8常駐中に、デスクトップ空白でユーザーが右＋左を操作した。元アプリのメニューは左を押した時点で開き、解放後の左クリック停止も再現した。

独立した観測ツールは左右のDOWN/UPを変更せず次のフックへ渡した。通過した右DOWNに対して右UPが下流で抑止され、25.778ms後もWindowsの右ボタン非同期状態が0x8000だった。次の通常右クリックのUPが通ると0に戻った。2回目も同様だった。詳細と訂正した受入範囲は [INPUT-DESIGN-dev.9.md](INPUT-DESIGN-dev.9.md) を参照。

観測ツールのビルドは警告・エラー0。180秒で自動終了し、自身のフックを解除した。観測中の常駐版はdev.8（PID 16432）であり、停止・設定変更・ユーザーデータの置き換えは行っていない。入力生成・カーソル自動移動・Computer Useも行っていない。

## 変更と自動確認

通常の右DOWN・MOVE・UPはそのまま通す。input-a〜dの受け窓方式は実機で不合格だった。現在は通常のWH_MOUSEによる後段の通知処理を試作している。Windowsへの物理DOWN/UPを低レベルフックで止めない。eでは外部WM_CANCELMODE通知を廃止し、gではinstaller側に届いた呼び出し候補について、照合済みのsource capture窓だけに同期WM_CANCELMODEを送り解除を再確認する。以下は過去の試作の自動確認であり、新方式の実機成功を示さない。

| 確認 | 結果 | 確認範囲 |
| --- | --- | --- |
| input-aのReleaseビルド | 警告・エラー0 | 初期実装のWPFアプリのコンパイル |
| MouseCancellationChecks | 13/13 PASS | 代替APIによる対象・PID・スレッド・capture照合 |
| input-aのChordReleaseChecks | 10/10 PASS | 初期実装の代替APIによる解放順序・重複・開始失敗・喪失・timeout・破棄・再入・capture要求中のforeground変更 |
| input-dのChordReleaseChecks | 12ケース成功 | native DOWN受領後の開始、非候補時のactivate拒否、解放順序・再押下・重複・失敗・喪失・timeout・破棄・再入。純fakeのみ |
| input-dのRelease / FDD publish | 成功、警告・エラー出力なし | 記録用シンボルを付けた試作アプリのコンパイル |
| input-eのRelease / FDD publish | 最終ビルド成功、警告・エラー出力なし | C#本体、WinAPI専用x64/x86 DLL、x86補助EXE。実起動は下記の記録を参照 |
| input-eのBridgeStateChecks | 7/7成功、1回実行 | 純状態遷移。解放順序・長押し後のUP・ACK前UP・候補拒否・移動半径・target照合・再押下・redirect・世代wrap |
| input-eの補助モジュール依存 | USER32.dll / KERNEL32.dllのみ | 3個ともdumpbinでimport確認、VCランタイム依存なし |
| MouseChecks | 9/9 PASS | 通常の右押下・解放と移動時の候補解除、既存ホイール方式 |
| ActivationCompletionChecks | 4/4 PASS | 解放前後の表示完了通知とキャンセル |

試験は物理入力を生成しない。模擬APIの成功は実Windowsのforeground/captureや他アプリの改善を証明しない。PDF手書き・Codexのソース変更やアプリごとの例外設定は行っていない。

input-dのChordReleaseChecksは1回実行した。12個のチェックが成功したが、末尾の表示は旧分母を使って`PASS 12/11`だったため、表示のみ`PASS 12/12`へ修正した。表示修正後の再実行は行っていない。

読み取りレビューで、capture取得後にもforegroundを再照合する条件を追加した。元アプリで始まった右押下の後続UPの配送と、ランチャー本体の表示中のcapture維持は実機確認が必要である。capture喪失や2秒のWindowsタイマーによる中断では、Windowsの解放を通すことを優先するため、その後のメニュー抑止を保証しない。タイマーはUIメッセージの処理に依存し、UI停止時の厳密な時間上限ではない。input-dではフック内の前面化要求を廃止し、受け取り窓のWndProcでnative DOWN到着後にcaptureを取得する。呼び出しの反応は実機で確認する。

## 実機確認が残る範囲

1. デスクトップ空白で右＋左を1回操作し、呼び出せること・元のメニューが開かないこと・両ボタン解放後にランチャーと元画面で左クリックを使えることを確認する。
2. Codexで送信しない選択テキストに対し、移動なしの通常右クリックでメニューが出ることを確認する。
3. PDF手書き0.6.8の破棄できる資料で、静止右押下による円の即時表示と、短い右ドラッグ開始の引っかかり・位置の跳びを別々に確認する。
4. 必要な場合のみ、確認用ファイルの右ドラッグで通常メニューを確認し、移動・コピーを実行せずEscで閉じる。

最初に1が改善したことを確認してから残りへ進む。実機確認前のため、修正完了・全アプリ互換とは報告しない。

### 試作の実機結果（未解決）

- input-a: 試作EXEの実起動をPID 23944 / 試作パスで確認。ユーザーによるデスクトップ空白の右＋左ではランチャーが表示されなかった。
- input-b-probe: 実起動をPID 30092 / 調査用試作パスで確認。ユーザー報告では最初の1回だけ表示され、以降は表示されなかった。改善済みとは扱わない。
- input-bはEXEを直接起動した状態で、記録用環境変数が適用されておらずログを取得できなかった。診断シンボル自体はDLLに含まれることを確認したが、実行時設定と分けて扱う。入力試験の失敗を診断ツールのビルド成功で置き換えない。
- 表示されない段階の切り分けが済むまで、PDF手書き・Codex等の追加の手動操作は依頼しない。
- input-c-probe: rootが記録設定を付けて`--background`で起動し、PID 16532 / 試作パスとログ開始を確認した。ユーザーの確認では2回とも表示されず、元の右メニューが開いた。
- cのログではRightThenLeftとフック登録が成立し、複数の右DOWN・左DOWNを受信していた。呼び出し判定と対象照合はtrueだったが、受け取り窓のforeground要求後も元のHWNDのままで、capture前に不成立となった。所要時間は約3〜8msであり、この再現での未表示原因はforeground拒否。フック時間切れ・設定未適用とは区別した。
- 次は、候補の物理LEFTDOWNを低レベルフックで通し、直下の受け取り窓の通常WM_MOUSEACTIVATE→WM_LBUTTONDOWNでforeground/captureを確立する試作を行う。自身のnativeDOWN到達を確認してからランチャーを呼ぶ。同一DOWNのヒットテスト先を変えられることは公式資料では保証されておらず、デスクトップ空白の最小試作から確認する。
- input-d-probe: `artifacts/previews/LinkLauncher-v0.1.2-dev.9-input-d-probe/LinkLauncher/`に4個の試作バイナリをpublishした。基準コミットは`5c6db96b27e16b6fcb666dcacc45315fe99db7b6`で、未コミットの変更を含む。実際のソース3ファイルのコピーとSHA-256、バイナリのSHA-256を試作フォルダー外側の`source-snapshot/`と`build-info.json`へ保存した。
- ユーザーがinput-cを終了したことと残存プロセスなしを確認後、rootがinput-dを記録設定付き`--background`で起動した。PID 10076、試作EXEのパス、RightThenLeftのフック登録と記録開始を確認した。
- input-dのユーザー確認は2回とも表示されず、元の右メニューが出た。左クリック停止の有無はこの回答だけでは確定していない。
- dのログは2回とも候補・対象照合・受け窓の準備がtrueだったが、受け窓のWM_MOUSEACTIVATE/WM_LBUTTONDOWNは記録されず、WaitingForNativeDownのまま準備タイマーで中断した。元の物理LEFTDOWNを通す途中で窓を表示する方法では、この再現の配送先は切り替わらなかったと推定する。未達をtimestamp不一致・capture取得失敗と混同しない。input-dも不合格で、正式配布には使わない。
- input-e-native-probe: `artifacts/previews/LinkLauncher-v0.1.2-dev.9-input-e-native-probe/LinkLauncher/`に7個の試作バイナリをpublishした。3個の補助バイナリ合計は22,528 bytes。ソース・バイナリのSHA-256とソースコピーを試作フォルダーの外側へ保持する。ビルド中にパス引数・ハッシュ取得・子プロセス待機・共有構造体のalignment・CRTを使わないコピー処理・x86 export/entryを修正した。最終ビルドが成功するまでの失敗は実機検証に数えない。
- input-eのWH_MOUSEの配送、元アプリでのcapture解除、UI ACKの時間関係は純状態試験の対象外。下記の実機結果は不合格で、修正完了・正式配布とは扱わない。
- 2026-10-11: ユーザーがinput-dを終了した連絡後、残存する本体・補助プロセスがないことを確認した。input-eの7バイナリのSHA-256が`build-info.json`と一致した後、rootが記録設定付き`--background`で起動した。本体PID 3832、x86補助PID 10216（親3832）、両EXEの試作パスを確認した。起動ログは`nativeStatus=7`で、x64/x86フック登録と有効化を確認した。これは登録・起動の証拠であり、実際の入力配送や呼び出し成功の証拠ではない。
- この起動の記録先は`artifacts/previews/LinkLauncher-v0.1.2-dev.9-input-e-native-probe/observations/20261011-002431-4b4f3fe9015d41e38462f01d008d64e1/`。`launch-info.json`に起動パス・PID・ソースコミットを保存した。`library.json`のSHA-256は起動前後で一致し、設定保存・スタートアップ登録変更・入力生成・カーソル自動移動は行っていない。
- input-eのユーザー確認は2回とも表示されず、元の右メニューが出た。解放後の左クリック停止の有無はこの回答だけでは確定していない。追加のPDF手書き・Codexでの操作は依頼していない。
- eのログにはC#の低レベルフックの左右DOWNがあり、nativeの候補成立・候補拒否・UI通知のstage記録は一件もない。nativeStatus=7を実際の仲介成功と混同しない。現行のstageはコールバック入口を記録していないため、未到達と入口後の早期returnはまだ区別できない。
- 読み取り専用でDLLのPEセクションを確認した。両DLLの`.LLCFG`はREAD/WRITE/SHAREDで存在し、実行中の本体・補助DLLの同セクションにowner PID 3832とHWND 0x690D8Aを確認した。本体・補助・ExplorerのSessionIdは1、integrity RIDは0x2000（Medium）で一致した。ExplorerのExtensionPoint/Signature/ImageLoad mitigation flagsはそれぞれ0。この時点のExplorerのモジュール一覧にhook DLLはなかった。これらは入力対象PID/TIDの確定やフック未到達の断定にはならない。
- 次のinput-fは入力方式を変える版ではなく、`BRIDGE_INPUT_PROBE`限定の共有カウンターで入口と早期returnを切り分ける診断試作。負のhook codeは即座に通し、観測のための入力生成・カーソル移動・ファイルI/O・同期UI送信をコールバックへ追加しない。正式配布物には診断カウンターとセクションを含めない。
- input-f-entry-probeをソース`4f95266598ab382583ecc09a05b0004518e08e03`からpublishした。警告・エラー出力なし。両DLLの`.LLPRB`は132 bytesでREAD/WRITE/SHARED、依存はUSER32/KERNEL32のみ。7バイナリのハッシュとソースコピーを試作外側に記録した。状態遷移を変更していないため純状態試験は再実行していない。読み取りスクリプトの構文エラーは0。実行時の読み取りは起動後に別途確認した。
- ユーザーがinput-eを終了した後、残存プロセスなしを確認し、rootがinput-fを記録付きで起動した。本体PID 3256、補助PID 9488、status=7。ユーザーデータの起動前後のSHA-256は一致した。通常のCodex/Chromeでの操作中に、共有取得・無効状態・target照会・lockの失敗は0だったが、対象PIDと実行PIDの不一致による`contextSkipped`が増えた。
- ユーザーにデスクトップ空白へポインターを置くだけの確認を1回依頼した。クリック・右＋左は不要とした。直後のsnapshotは、対象Explorer PID 11528/TID 11532に対し、x64実行PID 3256・x86実行PID 9488、両方の`contextSkipped=3919`。x64のsource-context受付は2のまま、x86は0、DOWN・candidate・BEGIN・ACKは全て0だった。この観測では、installer側に届く正常な通知をPID/TID一致ガードで除外していたことが分かった。eの失敗したDOWNそのものの再記録ではないが、同じデスクトップ領域での経路を確認した。
- fの記録先は`artifacts/previews/LinkLauncher-v0.1.2-dev.9-input-f-entry-probe/observations/20261011-004053-6c9e3c0612854d26b610b45e6cfa1561/`。`snapshot-after-desktop-pointer.json`を保持する。OSはAMD64、Explorer/Codex/Chrome/本体はネイティブ64bit、補助は32bitであり、ARM64との相違が原因ではない。

### input-gのビルド・起動（2026-10-11）

- ソース`1d8e3f3c6d81bf0cd96b3f01eab8bc858ec3fb30`からRelease / win-x64 / framework-dependentでpublishした。初回のSHORT定数変換によるビルドエラーを修正し、最終ビルドは警告・エラー出力なし。純状態遷移は変更していないため7ケースの再実行は行っていない。
- 試作は`artifacts/previews/LinkLauncher-v0.1.2-dev.9-input-g-single-hook-probe/`。アプリのフォルダーはC#本体4ファイルとx64 DLL（12,288 bytes）の計5ファイルのみ。x86 DLL・補助EXE・ランタイム・PDBは含めない。開発用スクリプトでは歴史的なx86モジュールもビルドするが、gの配布・起動対象はx64 DLL一本である。
- 5バイナリのSHA-256、17ソースファイルのコピーとハッシュ、ソースコミット、読み取りスクリプトのハッシュを試作フォルダー外側の`build-info.json`と`source-snapshot/`へ保存した。x64 DLLのimportはUSER32.dll / KERNEL32.dllのみ。
- ユーザーがinput-fをトレイから終了した後、残存する本体・補助プロセスなしと5バイナリのハッシュ一致を確認し、rootがgを記録付きで起動した。本体PID 11852、試作EXEのパス、`nativeStatus=5`を確認した。補助プロセスはない。これは起動・登録の証拠であり、右＋左の呼び出し成功を示さない。
- 起動記録は`observations/20261011-010149-371c7b6ab3bd4d3b9ddf810315d32548/`。読み取り専用の`Read-Probe.ps1`で実コールバックの到達と通常のLEFTDOWN/UPを確認し、`snapshot-startup.json`に保存した。共有取得・target照会・lockの失敗は0だった。右＋左の実機受入はユーザーの回答待ち。
- `library.json`の起動前後のSHA-256は`0f5b6664ce1f7f2ab81caa138671589aeca6de47ca715ce50f5094e59f522549`で一致した。設定保存・スタートアップ登録変更・入力生成・カーソル自動移動・Computer Useは行っていない。

### input-gのユーザー確認（受入未達）

- ユーザー報告は、デスクトップで2回とも表示され、解放後の左クリックはランチャー・元画面の両方で使えた。一方、左を押した時点で元の右メニューも開き、デスクトップを左クリックするまで残った。呼び出し・左クリック停止は改善したが、メニュー抑止の受入条件は未達。
- 保存した`input-at-user-result.log`では複数のBEGIN/ACK/COMPLETEが成立し、完了直後と後続確認の非同期右・左ボタン状態は両方0だった。これを元アプリの内部状態やメニュー抑止の成功とは扱わない。source-context（stage 41 detail 1）での受理だった。ログには別の候補でcapture解除失敗（stage 32 detail 3）もあり、全ての操作が成功したとは報告しない。
- 起動前と起動直後はユーザーデータのハッシュが一致したが、操作後の`library.json`は`d440e36535843cb36a769c1af9040650df675f4d94c6c822f148a7df47e8ff38`となり、同一ではない。ツールによるデータの置換・設定保存は行っていない。アプリの通常利用中の更新と区別し、操作後も変わっていないとは記録しない。
- ユーザーが検証アプリを終了したうえで、LinkLauncherなしのデスクトップで右保持→左クリックを試し、左押下で右クリック完了・メニュー表示になる通常挙動を確認した。rootは本体・補助プロセスが残っていないことを確認した。これはユーザーの比較観測であり、物理キー状態や生成されたWindowsメッセージの種類をrootが直接確認した証拠ではない。

### input-hの最小限の順序観測（実機観測済み）

- 元メニューが左押下で出るため、右UP抑止だけで解決すると扱わない。gのsource側ReleaseCaptureをWM_CANCELMODEに変える案は、この時点では実装していない。通知順序を確定してから変更を判断する。
- 編集対象は`BridgeHook.c`の`BRIDGE_INPUT_PROBE`限定の観測、`DesktopIntegration.cs`のログ表示、開発記録。gの物理入力通過・候補・ACK・解放処理は変更しない。
- 試作だけにWH_CALLWNDPROCを追加し、全ての通知を次のフックへ渡す。直近の右DOWNの対象PID/TIDかつ3秒以内のR/L通知、WM_CONTEXTMENU、WM_CANCELMODE、WM_CAPTURECHANGED、メニュー開始・終了・初期化だけを非同期記録する。対象のウィンドウ手続きを変更・subclass化しない。捕捉解除の前後、HC_NOREMOVEでの押下照会、HC_ACTIONの押下受理・UP処理結果を同じGetTickCount時刻とともに記録する。
- stage 54は受動観測フックの登録結果。追加の`0x803B`通知のkind 1はWndProc直前、2はHC_NOREMOVE、3は押下候補、4/5は既存のcapture解除前後、6はUP処理。kind 6のdetailの下位8bitはdecision（0通過、2消費、3完了）、bit 8はtarget一致、bit 9はsource-context。ログの受信時刻だけで送信元の順序を判断せず、記録したtickを併用する。
- 受入は観測の到達と順序の判別。これは修正版の実機合格ではない。全ての診断ソース・フック・ログを正式配布物から除去する。
- ソース`376bdfb9708f94da753b22405ebe1514a3bf209c`からpublish成功、警告・エラー出力なし。5バイナリと19ソースのコピー・ハッシュを試作外側に保持した。`.LLPRB`は132 bytes、`.LLTRC`は12 bytesで、両方SharedReadWrite。状態遷移を変えていないため純状態試験は再実行していない。
- 残存プロセスなしを確認後、rootが記録付きでinput-hを起動した。本体PID 8952、status=5、受動観測hookのstage 54 detail 1を確認した。起動前後のユーザーデータハッシュはgの操作後の値と一致した。記録先は`artifacts/previews/LinkLauncher-v0.1.2-dev.9-input-h-menu-order-probe/observations/20261011-011835-e7b17b1e2ad448549d9cbe1193524028/`。
- ユーザーにデスクトップ空白で1回だけ手動右＋左を依頼し、ランチャーと元メニューの両方が出た。`input-at-user-result.log`ではsource PID 11528の左押下受理とcapture解除前後がtick 35567984。WM_CAPTURECHANGEDも同tick、WM_CONTEXTMENUが35568031（47ms後）、WM_ENTERMENULOOP/WM_INITMENUPOPUPが35568062だった。その後の左UPが35568281、右UPが35568343。両UPは対象一致の消費/完了となり、完了直後と後続確認の非同期左右状態は0だった。
- この再現では元メニューは物理UPより先に生成された。捕捉解除後の右クリック完了扱いが原因である可能性が高いが、元アプリ内部の呼出しスタックまでは観測していない。WndProc直前のWM_CONTEXTMENUを取れたため、表示失敗やUPの単純な通過と区別できた。追加の実入力生成、カーソル移動、Computer Useは行っていない。

### input-iの操作取消し（実装済み・実機確認待ち）

- 変更対象は`BridgeHook.c`のsource-contextでのcapture処理。照合した捕捉ウィンドウへのWM_CANCELMODEで操作自体の取消しを伝え、GetCaptureがNULLになったことを確認する。ReleaseCaptureだけの直接呼出しは廃止する。installer側の既存WM_CANCELMODE処理、物理入力通過、候補・ACK・UP処理は変更しない。
- 捕捉なしの場合は従来どおり通り、PID/TID/root不一致、取消通知の失敗、残存capture、時間切れは候補を拒否してLEFTDOWNを通す。対象外のウィンドウ、全プロセス、メニュー全般への取消通知は送らない。same-threadの同期処理に厳密な75ms上限を保証せず、アプリ独自状態の普遍的な取消しも保証しない。
- まずデスクトップの1回の呼び出しで元メニューの抑止を確認する。成功後に繰返し・解放後左クリックと、Codex通常メニュー/PDF手書き右DOWN・右ドラッグの少数確認に進む。診断は引き続き正式配布へ含めない。

## 試作の比較と復元

2版の同時起動は単一起動制御でできない。旧dev.8をトレイから終了し、dev.9の試作用EXEを起動する。比較中は設定の保存を行わず、スタートアップ登録先を変更しない。元へ戻す場合はdev.9をトレイから終了し、保持した `artifacts/releases/LinkLauncher-v0.1.2-dev.8-win-x64/LinkLauncher/LinkLauncher.exe` を起動する。

ユーザーデータの形式・設定の値は変更していない。完成済み正式版・dev.8以前の成果物を保持する。調査用ツール・ログは配布アプリに含めない。正式ZIPとGitHub公開は実機確認後に別途扱う。

## 試作成果物

- ソース: `2dd61d7f27a4b154eea32edcb02f2b2828cf0197`。ブランチ: `codex/dev9-release-state`。
- 試作フォルダー: `artifacts/previews/LinkLauncher-v0.1.2-dev.9-input-a/LinkLauncher/`。Release / win-x64 / framework-dependent publishが成功した。
- FileVersion: `0.1.2.9`。ProductVersion: `0.1.2-dev.9+2dd61d7f27a4b154eea32edcb02f2b2828cf0197`。
- ライセンス・起動案内を含む期待する9ファイルのみ、合計393,027 bytes。ランタイム本体・PDB・調査用ツール・ログ・ユーザーデータを含まない。各ファイルのSHA-256は試作フォルダーの外側の `build-info.json` に記録した。
- runtimeconfigは.NETCore.AppとWindowsDesktop.Appの10.0.0共有フレームワークを参照する。
- 保持したdev.8 ZIPのSHA-256は `2ea8c356e1601f1b6269331075461764a708099a2facd4db633f32d87faaeac2` と一致した。
- 試作の実起動は確認したが、実機の表示試験は不合格。最終配布ZIPは未作成。GitHubへのpush・公開は行っていない。
