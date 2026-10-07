# LinkLauncher

案件や資料の種類ごとに、ファイル・フォルダー・Webページを整理して開く Windows 用リンクランチャーです。初回正式版は **v0.1.0** です。

[最新の正式版をダウンロード](https://github.com/fnkuroko-design/LinkLauncher/releases/latest)

## 主な機能

- カテゴリを階層化してリンクを整理し、名前・リンク先・タグ・カテゴリ名を検索できます。検索は選択中の一覧を対象にし、カテゴリでは子カテゴリも含みます。
- お気に入りと最近使ったリンクを表示できます。ファイル・フォルダー・URLのドラッグ＆ドロップ登録や、JSONの読み込み・書き出しにも対応します。
- Ctrl + Alt + Space または設定したマウス操作ですぐ呼び出せます。既定のマウス操作は、ホイールボタンを押したまま右ボタンを押す方法です。組み合わせは2つ目のボタンを押すと作動し、長押しは不要です。
- ライト・ダーク表示はWindowsの「アプリのモード」に連動します。タスクトレイに常駐し、Windowsへのサインイン時の起動は設定から有効にできます（既定はオフ）。
- 貼り付けたパスやURLを囲む対応した引用符・山括弧は自動で取り除きます。

## ダウンロードと起動

1. [正式版のリリースページ](https://github.com/fnkuroko-design/LinkLauncher/releases/latest)から Windows x64 ZIP をダウンロードします。
2. ZIPの全ファイルをフォルダーに展開し、その中の `LinkLauncher.exe` を実行します。
3. .NET 10 Desktop Runtime（Windows x64）がない場合は、標準の案内に従い、[Microsoft公式ダウンロードページ](https://dotnet.microsoft.com/ja-jp/download/dotnet/10.0)から「.NET Desktop Runtime 10」のWindows x64版を導入して、もう一度起動してください。

配布ZIPはframework-dependent形式です。.NETランタイムは同梱されず、アプリが自動でダウンロードやインストールを行うこともありません。

## 更新

1. 起動中の旧版をタスクトレイのメニューから「終了」します。
2. 新しいZIPの全ファイルを、新しいフォルダーに展開します。
3. 新しいフォルダーの `LinkLauncher.exe` を起動します。

登録内容とアプリ内の設定は `%APPDATA%\LinkLauncher\library.json` に保存されるため、アプリを新しいフォルダーから起動しても引き継がれます。旧版で「Windowsへのサインイン時に起動する」を有効にしていた場合は、新版を起動して設定を開き、「保存」を一度押してください。スタートアップの登録先が新版の場所に更新されます。

## 保存先と注意事項

保存先のJSONはリンク先のパスやURLを含みます。保存時には既存データのバックアップを保持します。JSONを共有するときは内容を確認してください。

マウス呼び出しは管理者権限で動く画面などでは認識されない場合があります。また、マウス操作は他のアプリの入力と重なることがあります。単ボタンを使う設定では、そのボタン本来の機能が他のアプリで使えなくなります。呼び出せない場合はショートカットを使ってください。

## ライセンス

独自コードと独自アイコンは [MIT License](LICENSE) です。実行基盤などのライセンス表示は同梱の [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) をご覧ください。

## 開発者向け

ソースからビルドまたは配布ZIPを作成するには .NET SDK **10.0.204** が必要です。

```powershell
./scripts/Publish.ps1
```

`global.json` でSDKを固定しています。SDKを更新するときは、配布EXEに含まれる.NET apphostのバージョンとライセンス表示も確認してください。

成果物は `artifacts/releases/LinkLauncher-v<Version>-win-x64/` と、同名のZIP・SHA-256ファイルです。既存の同名成果物は上書きしません。GitHub Actionsはmainへのpush、pull request、手動実行でビルドし、ZIPとSHA-256ファイルを14日間保存します。正式な配布物はGitHub Releasesで公開します（自動公開はしません）。

[v0.1.0リリースノート](https://github.com/fnkuroko-design/LinkLauncher/blob/main/docs/RELEASE-v0.1.0.md)と[検証記録](https://github.com/fnkuroko-design/LinkLauncher/blob/main/docs/VERIFICATION-v0.1.0.md)に、配布内容と確認範囲を記載しています。
