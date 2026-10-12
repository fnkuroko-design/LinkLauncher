# v0.1.3-dev.12 検証記録

日付: 2026-10-13。Windows x64 / .NET 10。全体レビューで挙がったタイムアウト競合を、実入力を使わず再現し修正した記録です。

## 再現と原因

独立したプロセス内に不可視のHWND_MESSAGEを作り、別スレッドへ独自のWM_APP通知をSendMessageTimeoutで送ります。旧方式の判定と実際のBridgeStateを使い、処理開始後に200ms遅らせると、75msの期限を超えて送信側が入力を通す一方、受信側の処理カウンターは1回進みました。速い応答では消費されました。

これはWindows APIと入力判断の組み合わせの再現です。本体のランチャー表示、元アプリへの実クリック、実マウスでの症状を再現したものではありません。

## 修正

BridgeRequest.c/hに、要求番号と受理・取消し状態を持つ8バイト整列の64bit CAS判定を追加しました。UIは固定した表示・非表示の操作を実行する前に受理を確定します。送信側は同期返信ではなく共通判定で消費・通過を決めます。取消しが先ならUIは何も実行しません。受理済みの入力判断は応答遅延や返信失敗で戻りません。

状態更新に使うロックは非ブロッキングのままです。更新が持ち越された場合、入力通知のロック取得成功時に、本体と試験が共有するBridgeRequestFinalizeで確定してから次の入力を扱います。共有ABIはversion 4 / 144 bytes。C#のexport binding、x64/x86 native buildを合わせて更新しました。配布は従来どおりx64のみです。

## 試験とレビュー

- NativeHandshakeChecks 13/13成功。旧方式の対照、速い応答、受理後の遅い表示・非表示、受理前の取消し、返信失敗、早期両ボタン解放、重複・古い要求・符号境界の要求番号を確認しました。
- 遅延確定後の次の右DOWN、解放、取消し後の再呼び出し、早期解放後の完了が一度だけになることを、本体と同じfinalizerで確認しました。
- 既存BridgeStateChecks 13/13、ActivationChecks 4/4成功。
- Release build成功。native x64/x86 DLL・x86 helperおよびC#をビルドし、警告0・エラー0。
- 指定済みのgpt-6-astra / highレビュアーが静的再確認。最初の差分では、通知冒頭の独立したロック取得だけでは遅延確定を取りこぼすP2指摘がありました。各イベントの同じロック内へ確定処理を統一し、追加試験後の再レビューで解消済み、新たな確定指摘なしとの結果でした。レビュアーは編集・実行・入力操作を行っていません。

記録は `artifacts/checks/dev12-handshake-20261013-024525/`。旧方式の再現は `legacy-result.txt`、修正後は `fixed-final-result.txt`、既存試験は `state-result.txt` と `activation-result.txt`、最終buildは `build-final-result.txt` に保持しています。

## 検証範囲

ハーネスはフックを登録せず、独自WM_APP通知のみを自身の不可視ウィンドウへ送ります。入力生成、WM_MOUSE送信、カーソル移動、フォーカス・キャプチャ操作、本体Startup、ユーザーデータ操作は行いません。処理カウンターは表示・非表示の代替で、実GUIの保証ではありません。

PDF手書き、Codex、デスクトップ、タスクバーなどの実入力の再確認は未実施です。通常の右DOWNの通知遅延、右ドラッグの引っかかり、解放時のメニュー表示に対し新たな実機合格を主張しません。前面復帰や元メニュー取消しも自動ハーネスの範囲外です。常駐dev.11の停止・切替、設定保存、スタートアップ変更、Computer Useは行っていません。

## 配布

- 配布ソース: `fbdbfe2b943dbe318f558643f61e6cb0bc302c94`。Release / win-x64 / framework-dependent publish成功。FileVersion `0.1.3.12`、ProductVersion `0.1.3-dev.12+fbdbfe2b943dbe318f558643f61e6cb0bc302c94`。
- ZIP: `artifacts/releases/LinkLauncher-v0.1.3-dev.12-win-x64.zip`、195,143 bytes、展開後420,214 bytes。
- SHA-256: `dbbb16f9a756e1df99af0f6617b64e9bf1a47ef2da68f8b6c22db20d025195b9`。sidecarと一致。
- 全10ファイルが `LinkLauncher/` 配下。CRC、配布フォルダーと新規展開先の全ハッシュ一致を確認しました。共有フレームワークは.NET 10 Core/Desktop。ランタイム・ユーザーデータ・PDB・ログ・試験データを含めず、ライセンス文書を同梱します。
- native DLLはx64、importsはUSER32/KERNEL32のみ。診断共有セクション・ロガーを含めません。
- 既存14個のZIPのハッシュが開始前と一致し、登録データ `library.json` のハッシュも一致しました。確認後もdev.11（PID 34004）が同じ配布フォルダーから常駐しています。dev.12の実EXEの起動と実入力試験は未実施です。

配布確認は `package-result.json`、保持確認は `preservation-result.json` に記録しています。正式版v0.1.2と完成済みdev.11までの成果物を保持し、GitHubへのpush・公開は行っていません。

dev.12を手動確認する場合は、常駐dev.11をトレイの「終了」で閉じてから、`artifacts/releases/LinkLauncher-v0.1.3-dev.12-win-x64/LinkLauncher/LinkLauncher.exe` を起動します。戻す場合はdev.12をトレイから終了し、保持済みdev.11の同名EXEを起動できます。比較中は設定を保存せず、スタートアップ先を変更しません。連続の右＋左呼び出し、両解放後の最初の左クリックと検索入力、静止した通常の右クリック・右ドラッグを少数確認する範囲が候補です。
