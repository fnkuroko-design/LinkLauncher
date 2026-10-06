# LinkLauncher 0.1.0-dev.4 検証記録

日付: 2026-10-07。Windows / .NET 10。dev.1〜dev.3の成果物とタグを保持。

## 変更

- 設定に「Windowsへのサインイン時に起動する」を追加。現在ユーザーのHKCU Runキーへ、現在のexeを引用した `--background` コマンドを登録する。解除はLinkLauncherの値だけを削除する。
- 現在の登録状態をOSから読み、チェックと保存で更新する。JSONへの設定追加や、起動時の無断登録をしない。移動・版変更後は新exeから設定を保存して登録先を更新する。
- 登録値を変更前に保持し、登録・設定保存が失敗した場合は復元を試み、失敗内容を設定画面に表示する。登録APIはREG_SZを明示し、想定外の値の型を上書きしない。
- バックグラウンド起動は前面アクティブ化を抑止し、既存インスタンスがある場合も画面表示を要求しない。手動の二重起動では既存画面を開く。
- パス・URLの外側の対応する囲みを繰り返し除去。ASCII/全角引用符、曲線引用符、ASCII/全角山括弧、入れ子を処理し、内側の文字は保持する。
- 外部パッケージ、常時タイマー、ファイル走査、ネットワーク通信を追加しない。

## 実施した確認

- CoreChecksを1回実行し7/7 PASS。追加例は引用符・山括弧・入れ子・日本語・UNC・囲まれたURL、URL内部の引用符保持、不一致の囲みと空文字の拒否。
- StartupChecksを1回実行し4/4 PASS。専用の `HKCU\Software\LinkLauncher.Tests\<guid>` キーだけで登録・引用コマンド・復元・解除・他の値の保全・不正型の拒否を確認し、専用キーを削除した。実際のWindows Runキーは変更していない。
- 編集・ドロップ・リンク起動が共通NormalizeTargetを通る接続と、設定保存時の復元経路を読み取り確認。
- [MicrosoftのRunキー仕様](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)に沿い、登録コマンドが260文字を超える場合は説明を表示して拒否する。
- UI確認用Release / win-x64 / framework-dependent publish成功。警告・エラーなし。
- 隔離データで `--background` 起動後にプロセスが継続し表示ウィンドウがないこと、同じ引数の二重起動でも表示されないこと、手動二重起動で既存画面が表示されることをプロセスメタデータで確認。初期の一瞬の表示やログオン直後の状態を測定したものではない。
- Computer Useは設定画面の表示だけを確認。暗色で「Windowsへのサインイン時に起動する」が現在の未登録状態に合わせてOFFで表示されることを確認し、チェック・保存は行っていない。表示確認後、見出しと説明を整理して縦幅を縮めた。

Windowsからサインアウト・再起動する確認は行っていない。実際のスタートアップ設定は既定OFFのまま、ユーザーが設定画面から有効化する。

## 配布

- `scripts/Publish.ps1`: 最終ソースのRelease / win-x64 / framework-dependent publish成功。警告・エラーなし。
- ZIP: `artifacts/releases/LinkLauncher-v0.1.0-dev.4-win-x64.zip`、160,105 bytes（約156.4 KiB）。展開済みフォルダは343,942 bytes（約335.9 KiB）。
- FileVersion 0.1.0.4、ProductVersion 0.1.0-dev.4。Git SHA末尾は`58b4ea5bdd367f2067ff7a67cd58385deb8da6c2`で、ローカルタグ`v0.1.0-dev.4`のソースと一致。
- .NET 10の共有フレームワークを使用。ランタイム本体・library.json・PDBを同梱せず、MIT LICENSE、THIRD_PARTY_NOTICES、.NETライセンス、導入HTMLを含む。
- SHA-256記録と再計算値が一致: `198fa36e4f3c6ef4e1753605741b5b34587945f6486a151a19f700456563eca2`。
- dev.1〜dev.3の旧フォルダを保持し、ZIPハッシュが従来の値と一致することを確認。
- 検証専用プロセスを終了した後、配布exeを通常データで起動し、プロセス継続を確認。

GitHubへの公開は行っていない。
