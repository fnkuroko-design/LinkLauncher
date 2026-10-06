# LinkLauncher 0.1.0-dev.2 検証記録

日付: 2026-10-07。Windows / .NET 10。dev.1の配布フォルダ、ZIP、タグは保持。

## 変更

- 右Downを対象へ渡して右Upだけ抑止する処理を廃止。認識成立時はDown/Upの両方を抑止する。
- 未認識クリックはDown/Upを再生。横・下方向への逸脱や時間超過では通常の右ドラッグへ引き渡す。
- 再生の部分失敗ではDownの挿入有無を区別し、孤立Upを通さない。自アプリ、上位権限、権限判定不能の対象では認識を開始しない。
- 待機中のMouseMoveは早期に次のフックへ渡す。常時タイマー、ファイル走査、ネット通信を追加していない。
- リンクとカテゴリの操作メニューは右Upで開く。リンク起動は同じ行での左Down/Upと小さい移動距離を条件にする。
- 初期700×440、カテゴリ164幅、行32＋間隔1。カテゴリ欄を閉じると536幅。全展開固定をやめ、開閉状態をセッション中に保持する。
- 既存のNoteを500msホバーへ接続。名前、リンク先、カテゴリ、タグ、メモを表示し、長いメモは180高までプレビューする。
- ContextMenu、項目、ガター、区切り、サブメニュー、ToolTipの背景・文字・枠を動的なテーマ色へ揃える。Windows設定追従を維持。

## 実施した確認

- `dotnet run --project tests/MouseChecks/MouseChecks.csproj -c Release`: 6/6 PASS。上方向、Ctrl右クリック、横移動、下移動、時間超過、解除後の状態を確認。OS入力の注入は行わない。
- `dotnet run --project tests/ThemeChecks/ThemeChecks.csproj`: Light、Dark、Windows設定読取りの3項目PASS。入力欄・メニュー項目・区切りは未表示の論理ツリーで明暗切替を確認し、ContextMenu/ToolTipは各モードで生成した実テンプレートの色を確認。Windows設定や実ユーザーデータを変更しない。
- UI配置、ホバーのDataContext、クリックイベント、入力再生のDown/Up対応を読み取りレビュー。再生Downが0件だった場合の孤立Up経路を修正。
- `git diff --check`: 空白エラーなし。

## 実画面・入力の確認範囲

検証用の30リンクは `artifacts/ui-smoke-dev.2` に隔離。Computer Useでウィンドウ一覧を一度確認した際に、ユーザーのdev.1が常駐していた。単一起動の仕組みにより検証用dev.2は終了したため、dev.1を停止・操作せず、dev.2の実画面操作を見送った。

他アプリ上での物理的な右ジェスチャー、その後のクリック、Explorerの実右ドラッグ、メモの実ホバー、複数DPIの実表示は未確認。6件の状態テストは、Win32フック・SendInputの成功や他アプリのキャプチャ解放を証明するものではない。

## 入力復元の限界

未認識の短い移動は開始点と終点で再生し、中間軌跡や押下時間は再現しない。再生時にカーソルが一度開始点へ移る。押下後に対象ウィンドウや権限が変わる場合など、通常クリックを復元できないことがある。API失敗は日本語で表示する。

[Microsoft SendInput仕様](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)に従い、UIPIによる拒否を戻り値やGetLastErrorだけで確定したとは扱わない。管理者権限の画面ではショートカットを使用する。

## 配布

- `scripts/Publish.ps1`: Release / win-x64 / framework-dependent publish成功。コンパイル警告・エラーなし。
- ZIP: `artifacts/releases/LinkLauncher-v0.1.0-dev.2-win-x64.zip`、151,845 bytes（約148.3 KiB）。展開済みフォルダは324,281 bytes（約316.7 KiB）。
- アプリのVersionは0.1.0-dev.2、FileVersionは0.1.0.2。ProductVersion末尾のGit SHAはビルド時のHEAD（dev.1）であり、この開発ビルドは作業ツリーの変更を含む。完成ソースはローカルタグv0.1.0-dev.2で識別する。正式公開時にはソースをコミットしてからpublishする。
- 必要な共有フレームワークはMicrosoft.NETCore.App 10.0.0とMicrosoft.WindowsDesktop.App 10.0.0。ランタイム本体は同梱しない。
- ZIP直下にexe、dll、deps、runtimeconfig、README、導入HTML、MIT LICENSE、THIRD_PARTY_NOTICES。`licenses/DOTNET-LICENSE.txt`を含む。
- SHA-256記録と再計算値が一致: `7cf764ac16d705fb9616781bb88ddb504a4f0323cb64760d80145cd5fff4a48c`。
- dev.1のZIPを再計算し、従来のSHA-256と一致。旧フォルダと旧タグも保持。

GitHubへの公開は行っていない。
