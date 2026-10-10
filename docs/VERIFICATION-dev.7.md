# LinkLauncher v0.1.2-dev.7 検証記録

日付: 2026-10-10。Windows x64 / .NET SDK 10.0.204。

## 原因と修正

- ユーザーから、サブディスプレイではアプリ内クリックで画面が消えると報告された。
- 起動中の正式版v0.1.1（`C:\APP\LinkLauncher\LinkLauncher.exe`、FileVersion 0.1.1.0）のDPIコンテキストを読み取りで確認し、SystemAware / 144dpiだった。環境には144dpiの画面2枚と96dpiの画面1枚がある。
- 低レベルフックの座標は[モニター対応の画面座標](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-msllhookstruct)であるのに、従来は呼び出し側のDPIコンテキストに依存するWindowFromPointで照会していた。誤ったPIDを返すと外側押下フラグが立ち、実際には前面にあるアプリも非表示になる。
- [Microsoft推奨のmanifest設定](https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process)でPerMonitorV2を起動時に指定する。WPF初期化後のSetHighDpiModeを削除し、WPF起動には使われていないApplicationHighDpiModeプロパティも削除した。
- 外側クリックとジェスチャー抑止・復元対象のプロセス照会をDesktopHitTestへまとめ、[WindowFromPhysicalPoint](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-windowfromphysicalpoint)を使う。現在のカーソル取得もGetPhysicalCursorPosへ統一した。
- 照会中だけスレッドのDPIコンテキストをPerMonitorV2に変更し、finallyで元へ戻す。追加チェックでは、PMv2で作成したテスト窓を同じスレッドからSystemAwareで照会すると物理APIだけでも不一致になったため、API名だけでなく呼び出しコンテキストも揃えた。
- WPFは起動前のDPI設定を必要とするため、WinFormsのmanifest DPI設定を避ける警告WFO0003だけをcsprojで抑制する。設定方法自体をWinFormsの遅延APIへ戻さない。
- 固定・モーダル・メニュー・並び替え中の非表示抑制は維持し、データ形式、常時走査、通信、外部依存を変更しない。

## 確認範囲

検証は変更箇所に限定する。自前の一時テストウィンドウで実モニターのDPI・座標照会を確認し、他アプリへのマウス入力・カーソル移動・ユーザーデータの操作を行わない。

実マウスのジェスチャーから表示・クリック・非表示までを通した操作は、自動テストの座標判定と区別する。正式版と完成済み開発版の成果物は保持する。

## 実施した確認

- DesktopHitTestChecks: 起動時のPerMonitorV2設定、および実3画面 × 3つの呼び出しコンテキスト（PerMonitorV2 / SystemAware / Unaware）の内外座標判定がPASS。各画面に作成した自前のWin32ウィンドウで、内側は自PID、外側は別PIDまたは0になること、照会後のDPIコンテキストが維持されることを確認した。
- 同じテストのSystemAware呼び出しでは、従来のWindowFromPointが3画面中2画面で自ウィンドウを誤判定した。テスト窓・座標は変更せず、新方式は全画面で正しいPIDを返した。
- テストは独自クラスの一時ウィンドウだけを作成し、終了時に破棄・クラス解除する。カーソル移動・SendInput・他アプリの前面化や終了は行っていない。
- MouseChecks: 7/7 PASS。既存のボタン組み合わせ、通常クリック・ドラッグ復元、設定変更時のキャンセルを確認した。
- 初回ビルドで、カーソル取得APIを変更した際の残ったGetCursorPos呼び出し1箇所が検出された。GetPhysicalCursorPosへ統一し、上記のテストビルド・実行が成功した。
- Computer Useは行っていない。WPF画面の見た目と実マウス操作による受入は、上記のnative座標判定とは別の確認範囲として残す。
