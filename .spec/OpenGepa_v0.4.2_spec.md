# OpenGepa v0.4.2 Windows Menuツリー展開状態

## 目的

Windows Menuを表示したまま通常ランチャーの編集画面へ項目を登録すると、データ更新によりWindows Menuツリーが再描画され、利用者が開いていたGroupが閉じる問題を解消する。

## 仕様

- 通常ランチャーまたはWebランチャーだけを変更する保存では、Windows Menu設定が変わらない限り、取得済みのWindows Menuランタイムツリーをそのまま引き継ぐ。Windows Menuの再取得やツリーの再構築は行わない。
- Windows Menuの表示設定を変更した保存では、取得済みランタイムツリーを引き継がず、次回表示時に再取得する。
- ［更新］、Start Menuショートカットの作成・改名・削除では、従来どおりWindows Menuを再取得する。
- 実際にWindows Menuを再描画する場合は、WPFのツリー項目コンテナが生成された後に、記録済みIDと一致するGroupを展開する。
- 親Groupを展開してから子Groupを順に復元し、任意の深さの展開状態を維持する。
- 利用者が明示的に［すべて折りたたむ］を実行した場合は、従来どおり全Groupを閉じる。

## 非変更範囲

- Windows Menuの取得、並び順、登録内容、起動、編集権限は変更しない。
- 展開状態はセッション中のUI状態であり、`opengepa.json`やProfileへ保存しない。

## テスト

- Windows Menuを連続して読み込んでもGroupとショートカットのIDが安定する。
- 通常ランチャーへの登録などWindows Menu設定と無関係な保存後も、取得済みWindows Menuランタイムツリーの参照を維持する。
- Windows Menu設定を変更する保存後は、取得済みWindows Menuランタイムツリーを引き継がない。
- Windows Menuを再描画した際、記録済みの親子Groupを展開状態へ復元できる。
