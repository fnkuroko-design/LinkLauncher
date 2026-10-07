# LinkLauncher v0.1.0 検証記録

日付: 2026-10-07。Windows x64 / .NET SDK 10.0.204。

## 正式版の変更

- 承認済みdev.4の機能を維持し、Versionを0.1.0、FileVersionを0.1.0.0に更新。
- README、配布用の導入HTML、リリースノートに正式版の導入・更新・スタートアップ登録先の更新方法を記載。
- SDKをglobal.jsonで固定し、配布apphostが10.0.8で既存のMITライセンス表示と一致することを確認。
- GitHub Actionsの成果物をZIPとSHA-256ファイルだけに絞り、保存期間を14日間に設定。
- ユーザーlibrary.jsonとバックアップをGitの除外対象に追加。dev.1〜dev.4の成果物・タグは保持。

## 実施した確認

- 既存チェックをそれぞれ1回実行。CoreChecks 7/7、MouseChecks 7/7、StartupChecks 4/4、ThemeChecks 3項目、計21項目PASS。
- StartupChecksは専用の `HKCU\Software\LinkLauncher.Tests\<guid>` キーだけを使用し、終了時にそのキーを削除。実際のWindows Runキーは変更していない。
- 追加NuGetパッケージ、実行時の診断ログ、常時のファイル走査・ネットワーク通信、trackedファイルへのユーザーデータ混入がないことをソースと配布設定で点検。
- `scripts/Publish.ps1` でRelease / win-x64 / framework-dependent publish成功。警告・エラーなし。
- 配布EXEを隔離データで起動し、Computer Useで主画面の `v0.1.0`、暗色表示、検索欄の入力カーソル、設定画面の版表示と保存ボタンを確認。スタートアップのチェック・保存操作はしていない。
- 検証時は隔離データのみ「他の画面をクリックしたら閉じる」を無効化。通常データは変更していない。検証プロセス終了後、正式版EXEを通常データの `--background` で起動。
- dev.1〜dev.4のZIPハッシュが各チェックサムファイルと従来の値に一致することを確認。

実際のサインアウト・再サインイン、他アプリに対する実マウス操作、複数の表示倍率での確認は今回行っていない。マウス操作の確認は状態遷移の自動チェックであり、すべてのアプリとの無干渉を保証するものではない。

## 配布物

- ソースコミット: `190e72341bb1d297b7943555c16efa0735774996`
- タグ: `v0.1.0`（上記ソースコミット）
- ZIP: `artifacts/releases/LinkLauncher-v0.1.0-win-x64.zip`
- ZIP容量: 158,646 bytes（約154.9 KiB）。展開済みフォルダー: 340,184 bytes。
- SHA-256: `3a19aaf8a1b1012c7fa6f3f2d6c65210d6ba85e37ff26d4e22195e6aecfd9d27`
- DLLのProductVersionは `0.1.0+190e72341bb1d297b7943555c16efa0735774996`。タグのソースと一致。
- runtimeconfigはMicrosoft.NETCore.App / Microsoft.WindowsDesktop.App 10.0.0を参照。ランタイム本体は同梱しない。
- ZIPの9ファイルすべてを読み取り、展開済みファイルとSHA-256で一致を確認。ランタイム・library.json・PDBを含まない。
- 同梱: EXE、DLL、deps.json、runtimeconfig.json、README、START_HERE.html、MIT LICENSE、THIRD_PARTY_NOTICES.md、licenses/DOTNET-LICENSE.txt。

## GitHub公開

- [fnkuroko-design/LinkLauncher](https://github.com/fnkuroko-design/LinkLauncher) を公開リポジトリとして作成。既定ブランチはmain、GitHubのライセンス判定はMIT。
- [v0.1.0リリース](https://github.com/fnkuroko-design/LinkLauncher/releases/tag/v0.1.0) を正式版として公開。ZIPとSHA-256ファイルを添付し、最新リリースに設定。
- リモートのv0.1.0タグもソースコミット `190e72341bb1d297b7943555c16efa0735774996` に一致。dev.1〜dev.4タグも保持。
- [GitHub Actions初回ビルド](https://github.com/fnkuroko-design/LinkLauncher/actions/runs/37625265098) が成功。ZIPとチェックサムの成果物を14日間保存。
- 認証を使わず公開URLからZIPをダウンロードし、158,646 bytesおよび上記SHA-256との一致を確認。

公開結果の追記はドキュメントだけのため、再ビルドは行わず `[skip ci]` を付けて記録する。公開済みの配布物・タグは変更しない。
