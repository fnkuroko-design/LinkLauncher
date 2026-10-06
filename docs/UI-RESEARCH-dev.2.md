# UI参考調査 dev.2

調査日: 2026-10-06  
対象: 案件・種類の階層に多くのリンクを登録し、主にマウスで選んで開く小型ランチャー。各製品の公式ページ・公式ドキュメントを確認した。画面素材は流用していない。

## dev.1のUI

`src/LinkLauncher/MainWindow.xaml` は初期サイズ1020×710、最小840×540、左ペイン226px。リンク行は上下13pxの余白と行間8pxがあり、高さは約80〜90px。名前・リンク先・カテゴリ階層とタグを3段に表示し、種類バッジ、星、および「⋯」も並べている。カテゴリツリーは全項目を既定で展開する。`LinkRow.Detail` はカテゴリ階層とタグで、登録メモは一覧行に渡していない。

## 公式製品の操作と適合度

| 製品・公式資料 | 確認した操作 | LinkLauncherへの示唆 |
|---|---|---|
| [PowerToys Command Palette 概要](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/overview)・[設定](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/settings) | compact設定では小さな検索箱で開き、結果や入れ子ページが必要な時に広がる。設定で項目をシングルクリック起動にできる。ホーム項目のピン留めや並べ替えは右クリック。 | 常時大きな見出しを出さず、検索と選択結果に合わせて画面を使う参考。全体はキーボード中心なので、マウス主導の階層表示の見本にはしない。 |
| [Flow Launcher 公式利用ヒント](https://github.com/Flow-Launcher/docs/blob/main/usage-tips.md)・[Explorerプラグイン](https://github.com/Flow-Launcher/docs/blob/main/plugin-explorer.md) | 結果を右クリックして追加操作を開く。フォルダを結果から辿って開き、右クリックから Quick Access に登録できる。検索中のフォルダ階層を戻る操作もある。 | 結果行の右クリックメニューと、頻用リンクを少ない操作で見つける方法が参考。検索窓主体のため、案件を常時並べる画面の直接的な見本ではない。 |
| [Listary Search Files](https://help.listary.com/search-file) | 検索候補を選択し、右クリック（または `Ctrl+O` / `→`）で操作を開く。格納フォルダを開く、パスをコピー、Windows標準メニューなどが使える。公式資料は全操作をマウスでも行えると案内。 | マウスだけでも発見可能な結果操作の参考。Windowsファイル検索とExplorer連携に特化し、案件/種類ツリーは対象外。 |
| [Keypirinha First Steps](https://keypirinha.com/first.html) | 検索語を入力して候補を表示し、上下キーで選択してEnterで起動。ファイルを検索欄へドラッグしてパスを入れる、結果を外へドラッグする操作も記載。 | 検索後に余分な情報を絞ったランチャーの参考。公式の基本操作がキーボード中心なので、今回のUI主参考からは外す。 |
| [Quick Access Popup FAQ](https://www.quickaccesspopup.com/frequently-asked-questions/)・[メニュー表示](https://www.quickaccesspopup.com/category/faq/understanding_menu/)・[メニュー編集](https://www.quickaccesspopup.com/category/faq/customizing_menu/) | 中クリック、トレイアイコンなどからポップアップメニューを出し、案件・種類・話題ごとのサブメニューへ進む。メニューごとにアイコンを16〜64pxまたは非表示にでき、項目をまとめて縦長メニューを整理できる。 | マウスで階層を辿る体験と、階層を使って項目を整理する最も近い参考。LinkLauncherには既存のカテゴリツリーを活かし、一覧行からカテゴリ選択へつなぐ形が合う。 |

### QAPの制約

QAP公式FAQは、標準Windowsポップアップメニューでは項目ホバー時のツールチップ、項目右クリック処理、マウスホイールでのスクロールを提供できないと説明している。[メニューの文字サイズ・アイコン・操作制約](https://www.quickaccesspopup.com/category/faq/customizing_menu/)をそのまま模倣せず、階層整理の考え方を参考にする。メモのホバー表示と行の右クリックは、現在のWPF一覧に実装する方が適する。

## dev.2で採用する方針

- 初期サイズは700×440、左ペインは164px。カテゴリの折りたたみ状態に合わせてウィンドウ幅も縮小する。全展開を固定せず、必要な階層だけを表示する。
- リンク一覧は行高32px＋行間1pxを基準にし、名前と小さなカテゴリ名を単行で並べる。案件や種類の階層を一覧の長い説明に重ねず、表示件数を増やす。
- リンク先・タグ・メモは500msホバーで確認できるようにする。長文メモは高さ180pxまでのプレビューで読めるようにし、全文編集は編集画面を使う。空のメモではプレビューを出さない。
- マウスでの選択、カテゴリの開閉、お気に入り、追加・編集と、既存の右クリック操作を中心にする。検索は階層をまたいで探す手段として保つ。
- 見出し、説明文、上端の飾りラベル、検索欄の余白を縮め、一覧とカテゴリ操作を優先する。種類絞り込みなどの補助操作は、省面積と発見しやすさを見ながら最小限にする。

数値はdev.2の採用値。今回の比較からは、PowerToysの必要時に広がるコンパクト表示、QAPのサブメニューによる階層整理、Flow/Listaryの行から開く右クリック操作を採用判断に使った。QAP標準メニューのホバーTooltip制約は、LinkLauncherがWPFの一覧行上に独自Tooltipを出すことで避ける。
