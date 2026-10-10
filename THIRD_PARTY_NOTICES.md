# Third-party notices

LinkLauncherの独自コードと独自アイコンは、同梱のLICENSEに記載したMITライセンスで公開します。
現時点で追加のNuGetパッケージ・第三者画像・Webフォントは使用していません。

入力仲介の `LinkLauncher.MouseHook.x64.dll`、`LinkLauncher.MouseHook.x86.dll`、
`LinkLauncher.MouseHookHost.x86.exe` は本リポジトリの独自コードです。同じMITライセンスを適用します。
Windows APIだけを使用し、VCランタイムは同梱しません。

## 実行基盤

| 名前 | 用途 | ライセンス・参照先 |
| --- | --- | --- |
| .NET 10 | アプリ実行基盤、標準ライブラリ | MIT: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |
| Windows Presentation Foundation | Windows UI | MIT: https://github.com/dotnet/wpf/blob/main/LICENSE.TXT |
| Windows Forms | Windowsタスクトレイ | MIT: https://github.com/dotnet/winforms/blob/main/LICENSE.TXT |

初版の小容量パッケージはframework-dependent配布です。.NETのランタイム自体は同梱しません。
将来self-contained配布する場合は、ランタイムのLICENSE・THIRD-PARTY-NOTICES等を含めて配布し、
公開前に実際の配布フォルダと依存パッケージを再確認します。

## .NET apphost

LinkLauncher.exeには、.NET SDKが生成する標準のネイティブ起動部分（apphost）が含まれます。
この部品の著作権表示とMITライセンス全文を [licenses/DOTNET-LICENSE.txt](licenses/DOTNET-LICENSE.txt) に同梱しています。
取得元: https://github.com/dotnet/runtime/blob/v10.0.8/LICENSE.TXT
パッケージのライセンス: https://www.nuget.org/packages/Microsoft.NETCore.App.Host.win-x64/10.0.8
SDKを更新した配布時はapphostのバージョンと表示を確認します。

## OS提供のリソース

Windows API、インストール済みシステムフォント（Yu Gothic UI / Segoe UI）、
Windowsが提供するシステムアイコンを呼び出して使用します。フォントファイルは同梱しません。
アプリアイコン・画面内の記号は独自の図形です。

## サンプルリンク

GitHub、Microsoft LearnなどへのURLはリンク先のコンテンツを同梱するものではありません。
サービス名の使用は登録例の説明目的です。各サービスとの提携を示すものではありません。
