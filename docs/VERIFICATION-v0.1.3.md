# v0.1.3 検証記録

日付: 2026-10-13。完成済みv0.1.3-dev.12の機能コードを維持し、Versionを0.1.3、FileVersionを0.1.3.0へ更新しました。README、起動案内、正式版リリースノートを整備し、旧版の成果物・ユーザーデータを保持します。

## 引き継ぐ検証と範囲

- [dev.10](VERIFICATION-dev.10.md)のアイコン取得・キャッシュ・UIの確認。
- [dev.11](VERIFICATION-dev.11.md)のReviewChecks 5/5、CoreChecks 7/7、日本語パスでのnative build。保存失敗・正常バックアップ保持・再起動復旧、カテゴリ参照、編集入力保持、リンク先正規化を確認しています。
- [dev.12](VERIFICATION-dev.12.md)のNativeHandshakeChecks 13/13、BridgeStateChecks 13/13、ActivationChecks 4/4、警告0・エラー0のbuild。前述のチェックは開発版での結果で、正式版の版番号変更後に再実行したものではありません。
- 指定されたgpt-6-astra / highによる全体レビューと修正差分の静的再確認。入力差分の追加指摘は修正し、最終再確認で新たな確定指摘なしとの結果です。未知の不具合がない保証ではありません。

今回の正式化で機能コードは変更していません。既存の試験を繰り返さず、正式版publishと配布内容・ライセンス・ハッシュ・公開ダウンロードを確認します。入力生成、カーソル移動、設定保存、スタートアップ先変更、Computer Useは行いません。

入力タイムアウトの試験は、自身の不可視HWND_MESSAGEへの独自WM_APP通知と処理カウンターを使います。実入力・実GUIの前面表示・元メニュー取消しの合格とは分けます。通常の右DOWNの通知遅延、右ドラッグの開始、解放時のメニュー表示について、新たな実機合格を主張しません。スタートボタンでまれに元メニューが残る既知の制約を引き継ぎます。詳細な旧版の手動確認は[正式版v0.1.2](VERIFICATION-v0.1.2.md)に保持します。

## 正式版の配布・公開確認

- 配布ソース: `3b3bdc7220de0f020c88b49a7884c81f5ad81188`。dev.12完了後の機能ソースの差分はVersion/FileVersionのみ。入力補助DLLのSHA-256はdev.12と一致しました。
- Release / win-x64 / framework-dependent publish成功。FileVersion `0.1.3.0`、ProductVersion `0.1.3+3b3bdc7220de0f020c88b49a7884c81f5ad81188`。
- ZIP: `LinkLauncher-v0.1.3-win-x64.zip`、194,787 bytes、展開後419,103 bytes。
- SHA-256: `66508630fb5a1def6f8ebd35e211ad6c5440c4bf6c4d0578faa0bd9357920e8c`。sidecarと一致。
- 全10ファイルが `LinkLauncher/` 配下。ZIPのCRC、期待するファイル一覧、配布フォルダーと新規展開先の全ハッシュ一致を確認しました。ライセンス文書を含み、ランタイム・ユーザーデータ・PDB・ログ・調査ツールを含めません。
- .NET 10 Core/Desktopの共有フレームワークを参照。入力補助DLLはx64、importsはUSER32/KERNEL32のみ。診断共有セクションやロガーを含めません。
- 既存15個のZIPのハッシュが開始前と一致しました。正式版v0.1.2、dev.12を含め、既存成果物を上書きしていません。

登録データ `library.json` のハッシュは開始前と一致しました。確認時点ではdev.11（PID 34004）が常駐しており、ユーザーのトレイ終了連絡を待っています。旧版を強制停止せず、正式版EXEの起動はまだ行っていません。公開・配布の確認を、実EXEの起動および実入力の確認と分けます。旧版終了後に正式版を起動する場合も、設定・スタートアップ先は変更せず、実入力の再試験は行いません。

## GitHub公開

- 公開リポジトリ `fnkuroko-design/LinkLauncher` のmainへ反映し、正式タグ `v0.1.3` が配布ソース `3b3bdc7220de0f020c88b49a7884c81f5ad81188` を指すことを確認しました。
- [GitHub ActionsのWindows x64 ZIPビルド](https://github.com/fnkuroko-design/LinkLauncher/actions/runs/38314701279)が成功。対象コミットは `37006cf51aaee0e005b1831d256c35a48e6dab0c`（配布確認記録を含む）です。ActionsのZIPはローカル検証済みの公開添付ZIPと別のビルド成果物です。
- [v0.1.3正式リリース](https://github.com/fnkuroko-design/LinkLauncher/releases/tag/v0.1.3)を最新として公開しました。Release ID `411032731`、draft・prereleaseともfalse。ZIP（194,787 bytes）とSHA-256ファイル（99 bytes）の2点を添付しました。
- ドラフトの添付を認証付きで再取得した後、公開添付を認証なしで再取得し、両ファイルのサイズ・SHA-256がローカルと一致することを確認しました。GitHubのasset digestも一致しています。
- 既存v0.1.2の公開添付のID・サイズ・digest・更新日時は不変です。旧版の公開物・ローカル成果物を置き換えていません。

ローカルの監査記録は `artifacts/checks/release-v0.1.3-20261013-070535/` に保持し、公開ソースや配布ZIPへ含めません。
