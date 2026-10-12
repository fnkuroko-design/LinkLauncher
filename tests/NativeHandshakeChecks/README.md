# 入力を生成しないネイティブ応答境界の試験

MSVC x64とPowerShellを使います。結果の上書きを避けるため、新しい出力ディレクトリを指定してください。

```powershell
./tests/NativeHandshakeChecks/Run.ps1 -OutputDirectory ./artifacts/checks/handshake-new
```

独立したコンソールEXEにproductionのBridgeState.cとBridgeRequest.cをリンクします。別スレッドの不可視HWND_MESSAGEへ独自WM_APPを送り、SendMessageTimeoutの実際の応答期限を確認します。フックDLLや本体Appは起動しません。物理入力の生成、WM_MOUSEの送信、カーソル移動、フォーカス・キャプチャ操作、実ユーザーデータの読み書きはありません。

legacy-slow-controlは意図的に旧方式の競合を示す対照です。表示の代わりにカウンターを使い、旧方式では処理1回と元入力の通過、修正方式では処理1回と消費、取消しが先なら処理0回と通過を確認します。遅延確定の試験も本体と同じfinalizerを使います。実マウス・元メニュー・WPFの前面表示を確認する試験ではありません。

`-Suite State` は既存の純粋な状態機械試験を同じビルド方法で実行します。試験EXE・ログは配布物に含めません。
