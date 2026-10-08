# LinkLauncher v0.1.1 検証記録

日付: 2026-10-08。Windows x64 / .NET SDK 10.0.204。

## 正式版の変更

- ユーザーが確定したdev.6の機能を維持し、Versionを0.1.1、FileVersionを0.1.1.0へ更新。
- `scripts/Publish.ps1` のステージ内に `LinkLauncher/` を作成してpublish・説明・ライセンスをまとめ、ステージ全体を圧縮する。ZIP名は従来のバージョン付き形式を維持する。
- READMEと導入HTMLの解凍・起動・更新案内を `LinkLauncher/LinkLauncher.exe` に合わせる。同名成果物の上書き禁止と固有ステージの安全な後始末を維持する。
- 完成済みdev.1〜dev.6と正式版v0.1.0を保持する。

## 確認範囲

本作業は配布構成・版表示・説明だけの変更のため、機能チェックやComputer Useを繰り返さず、最終publishとZIPの展開・内容・ハッシュを確認する。

機能面ではdev.5のCoreChecks 7、MouseChecks 7、OrderChecks 5、dev.6のMouseChecks 7、ActivationCompletionChecks 4、EditorDropChecks 4が通過している。dev.6のユーザー確認を本正式版の受入として扱う。

実マウス入力・全アプリ・複数DPI環境の網羅確認は行っていない。dev.6のフォーム内ドロップ確認は画面を表示しないWPFイベントの検証で、実際のエクスプローラーからのドラッグ操作とは区別する。詳細は[dev.6検証記録](VERIFICATION-dev.6.md)を参照。

## 配布物

- 配布ソース: `f52e3d91ba470164e61571caea8ad63bd43ae12a`。
- `scripts/Publish.ps1` によるRelease / win-x64 / framework-dependent publishが成功。EXEのFileVersionは `0.1.1.0`、DLLのProductVersionは `0.1.1+f52e3d91ba470164e61571caea8ad63bd43ae12a`。
- ZIP: `LinkLauncher-v0.1.1-win-x64.zip`、170,968 bytes。展開後のファイル合計は370,073 bytes。
- SHA-256: `146466dbddcc9bcc93b19e8e7a7c3469ee1b3f280d34a047df9f2a04f3ed0f90`。同梱の `.zip.sha256` と一致。
- ZIPの9ファイルすべてが `LinkLauncher/` 配下にあり、解凍先の直下はそのフォルダ1つだけになる。新しい検証用ディレクトリへ展開し、全ファイルのSHA-256が圧縮前の配布フォルダと一致することを確認した。
- 内容はEXE、DLL、deps/runtimeconfig JSON、README、導入HTML、MIT LICENSE、第三者通知、.NETライセンスのみ。ユーザーデータ・PDB・ランタイム本体を含まない。
- runtimeconfigは `Microsoft.NETCore.App` と `Microsoft.WindowsDesktop.App` の10.0.0を参照する。
- 既存の7つのZIP（dev.1〜dev.6、v0.1.0）のSHA-256が変更前の記録と一致し、完成済み成果物を保持している。
