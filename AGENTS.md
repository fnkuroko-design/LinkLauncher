# LinkLauncher

常に日本語で答える。
実装でサブエージェントを活用する場合はユーザーに明示し、gpt-6-luna / maxを使用する。
実装後の検証は必要最小限とし、Computer Useは最終のUI確認などに限定する。

Windows専用の軽量なリンクランチャー。C# / WPF / .NET 10を使用する。
配布はWindows x64のframework-dependent ZIP。ランタイムは同梱しない。
未導入環境にはMicrosoft公式の.NET 10 Desktop Runtime導入ページを案内する。
常時のファイル走査やネットワーク通信を導入しない。
ユーザーデータはローカルJSON。既存データの置き換え前にバックアップを保持する。
完成した開発版の成果物は保持し、次の作業ではdev番号を進める。
