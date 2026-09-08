# OpenGepa v0.4 仕様書

## 1. 位置付け

本書はOpenGepa v0.4.0で追加または変更する機能の正本である。v0.1、v0.2、v0.3で確定した基本仕様は、本書が明示して変更する箇所を除き継続する。

v0.4.0は、OpenGepaが保持しているランチャー構成、Windows由来の起動対象、起動履歴、使用頻度を、Model Context Protocol（MCP）を通じてローカルAIクライアントから参照・検索・起動できるようにする版である。

あわせて、通常アプリランチャーおよびWebランチャーが保持するタブ、Group、FileItem、DirectoryItem、UrlItemへ `description` を追加する。`description` は項目の用途、使い分け、注意事項などを人間とAIが共有するための備忘録であり、人間とMCPクライアントの双方から参照・編集できる。

OpenGepaのUI、ダイアログ、設定、同梱文書は日本語のままとする。MCPクライアントが `description` を新規作成または更新する場合も、利用者から別の指示がない限り日本語で記述することを推奨する。

v0.4.0のMCP対応はローカル利用を対象とする。インターネットへMCPエンドポイントを公開する機能は提供しない。


## 2. v0.4.0の範囲

v0.4.0で実装する機能は次のとおりとする。

- 通常アプリランチャーおよびWebランチャーの `description`
- 人間による `description` の表示・編集
- MCPクライアントによる `description` の参照・編集
- ローカルMCPサーバー
- MCPからのタブ・項目の一覧取得
- MCPからの項目検索
- MCPからの現在項目の詳細取得
- MCPからの既存項目の起動
- MCPからの起動履歴の参照
- MCPからの使用頻度の参照
- MCP連携の設定
- MCP連携に必要な配布物、README、回帰テスト

次の機能はv0.4.0の対象外とする。

- MCPからのLauncherTab、Group、FileItem、DirectoryItem、UrlItemの新規作成・削除・移動・名前変更
- MCPからのアイコン変更
- MCPからのProfile保存・読込
- MCPからのWindows Menu編集
- MCPからの任意ファイル、任意URL、任意コマンド、任意PowerShellの直接実行
- インターネットへ公開するMCPサーバー
- Streamable HTTPによるMCP公開
- OAuthその他のリモート認証
- MCP Resources
- MCP Prompts
- MCP Apps
- OpenGepa自身が他のMCPサーバーへ接続するMCPクライアント機能
- OpenGepa内部へのLLM組込み
- 日本語／英語その他のUI言語切替

OpenGepaは、従来どおりアプリケーション全体を対象とする汎用Undo／Redo機構を採用しない。


## 3. MCP技術方針

### 3.1 対応プロトコルとSDK

v0.4.0は、実装時点の正式なModel Context Protocol仕様を対象とし、MCP `2026-07-28` を基準とする。

独自にMCPのJSON-RPCプロトコルを実装せず、公式C# SDKの安定版2.xを使用する。NuGetのバージョンは浮動指定にせず、実装時に採用した安定版をプロジェクトファイルへ固定する。

ローカルMCPサーバーの外向きTransportはSTDIOだけを使用する。

### 3.2 MCP実行ファイル

配布ZIPへ `OpenGepa.Mcp.exe` を同梱する。

MCPクライアントは `OpenGepa.Mcp.exe` をSTDIO MCPサーバーとして起動する。

`OpenGepa.Mcp.exe` はOpenGepaの設定ファイルを直接読み書きせず、アプリやURLを直接起動しない。実際の参照、検索、起動、`description` 更新は、実行中の `OpenGepa.exe` に依頼し、OpenGepa本体の既存サービスを通じて行う。

これにより、GUIとMCPが別々に `opengepa.json` などを書き換える構成を禁止する。

### 3.3 OpenGepa本体とのIPC

`OpenGepa.Mcp.exe` と `OpenGepa.exe` の内部通信にはWindowsの名前付きパイプを使用する。

名前付きパイプは現在のWindowsユーザーだけが接続できるよう制限する。

### 3.3.1 配置単位のOpenGepaインスタンス識別

OpenGepaは、配置ディレクトリごとに独立したアプリケーションインスタンスとして扱う。

配置識別子は、正規化した `OpenGepa.exe` の配置ディレクトリから一方向ハッシュで生成する。絶対パスそのものを名前へ埋め込まない。正規化では大文字小文字の差異を吸収し、解決できる再解析ポイントは解決した最終配置を用いる。

OpenGepa本体の単一起動Mutex、既存インスタンスを表示するための通知イベント、およびMCP名前付きパイプは、同じ配置識別子から名前を生成する。

- 同じ配置ディレクトリから起動したOpenGepaは一つだけ動作する
- 異なる配置ディレクトリに置かれたOpenGepaは、同じWindowsユーザー上でも同時に動作できる
- `OpenGepa.Mcp.exe` は自身と同じ配置ディレクトリの `OpenGepa.exe` だけへ接続する
- 別配置の既存OpenGepaへ接続またはフォールバックしてはならない

このため、名前付きパイプも配置識別子を含め、別ディレクトリのOpenGepa同士が衝突しないようにする。

`OpenGepa.Mcp.exe` 起動時に同じ配置ディレクトリの `OpenGepa.exe` が動作していない場合は、隣接する `OpenGepa.exe` を通常起動し、MCP用IPCの準備完了を一定時間待つ。既定の待機上限は10秒とする。

本体を起動できても［MCP連携を有効にする］がOFFの場合、`OpenGepa.Mcp.exe` は設定を変更せず `disabled` を返す。MCP実行ファイルがMCP連携を自動的に有効化してはならない。

起動またはIPC接続に失敗した場合、MCPサーバーは明示的なエラーを返し、設定ファイルを直接操作する代替経路へフォールバックしてはならない。

### 3.4 標準入出力

`OpenGepa.Mcp.exe` の標準出力はMCPプロトコル専用とする。

診断メッセージ、例外、デバッグ出力を標準出力へ書いてはならない。診断情報は標準エラーまたはOpenGepa管理ログへ出力する。

### 3.5 OpenGepa本体を正本とする

MCP経由の操作も、GUIから行う操作と同じOpenGepa本体のサービスを通す。

- 項目解決
- 起動
- 起動履歴・使用頻度の記録
- `description` 更新
- 設定保存
- 利用可否判定
- Windows主要操作の安全性判定

MCP専用に同じロジックを複製してはならない。

複数のMCPクライアントから同時に要求が来た場合も、保存を伴う変更は既存の単一ライターキューと原子的保存規則に従う。


## 4. description

### 4.1 対象

次のOpenGepa管理オブジェクトは任意の `description` を持てる。

- 通常アプリランチャーのLauncherTab
- WebランチャーのLauncherTab
- Group
- FileItem
- DirectoryItem
- UrlItem

`SeparatorItem` は `description` を持たない。

Windows Menu、ストアアプリ、Windows主要操作、起動履歴、使用頻度など、Windows環境またはOpenGepaの固定カタログから生成されるシステムタブおよびその生成項目には、v0.4.0では利用者編集可能な `description` を追加しない。

Windows主要操作は、必要に応じて固定カタログ側にAI参照用の日本語説明を持てる。これは利用者編集可能な `description` とは別物とする。

### 4.2 内容

`description` はプレーンテキストとする。

- 改行を許可する
- 最大4,000 UTF-16 code units（.NETの `string.Length`）
- 保存時に先頭と末尾の空白を除去する
- 空文字または空白だけの場合は未設定として扱う
- Markdownとしての解釈を前提にしない
- 実行パス、URL、起動引数その他の起動情報として使用しない
- `description` の内容によってOpenGepaの権限、起動可否、MCP公開可否を変化させない

`description` は人間およびAIのための補助情報であり、起動処理そのものには影響しない。

### 4.3 言語

`description` は任意のUnicode文字列を保存できる。

OpenGepaの標準運用では日本語を推奨する。MCPクライアント向けのtool descriptionにも、「利用者から別の指定がない限り、`description` の新規作成・更新は日本語で行う」ことを記載する。

これは推奨規則であり、OpenGepa側で言語判定や翻訳を強制しない。

### 4.4 人間による表示・編集

LauncherTab、Group、FileItem、DirectoryItem、UrlItemの編集UIに `description` の表示・編集手段を追加する。

TreeViewの通常表示へ `description` 本文を常時表示しない。

右クリックメニューまたは既存編集画面から［説明を表示・編集］を開き、複数行テキストとして確認・編集できるようにする。

F2による名前変更は従来どおり表示名だけを対象とし、`description` は変更しない。

### 4.5 保存とProfile

`description` は各LauncherTabまたはLauncherNode自身のデータとして `opengepa.json` へ保存する。

LauncherTabまたはLauncherNodeを移動・名前変更しても `description` を維持する。

LauncherTabを複製した場合は、そのLauncherTabおよび配下Nodeの `description` も複製する。

通常アプリランチャーおよびWebランチャーの `description` はProfileへ含める。

v0.4.0では、`opengepa.json` とProfileの保存形式について、v0.3.0で確定している `formatVersion` を確認し、それぞれ必要な形式の `formatVersion` を1段階更新する。v0.3.0以前のデータは `description` 未設定として互換的に読み込む。

### 4.6 検索

人間がランチャー上部で行う従来の検索は、これまでどおり表示名を対象とし、`description` を検索対象に追加しない。

MCPの検索は、表示名に加えて `description` を検索対象にする。

これにより、人間の検索挙動を変更せず、AIは「何のために使う項目か」という意味情報を利用できる。


## 5. MCP設定

設定ウインドウへ［MCP］タブを追加する。

少なくとも次の設定を持つ。

- ［MCP連携を有効にする］
- ［MCPから項目を起動する］
- ［MCPから説明を編集する］

［MCP連携を有効にする］の初期値はOFFとする。

後者2項目の初期値はONとするが、［MCP連携を有効にする］がOFFの場合は効果を持たない。

これにより、利用者はMCPを完全に無効化できるほか、MCPを読み取り専用として使用することもできる。

設定画面には、現在の配布ディレクトリにある `OpenGepa.Mcp.exe` のフルパスを表示し、パスをクリップボードへコピーできる操作を置く。

MCPのためにTCPポート番号、URL、ファイアウォール例外を設定させない。


## 6. MCP上の対象識別

### 6.1 基本方針

MCPクライアントへは、対象を表す不透明な `ref` を返す。`ref` はOpenGepa本体がMCPセッション中に発行する、推測困難なランダム値とする。文字列へLauncherNode ID、AUMID、Preset ID、ファイルパスその他の内部識別子をエンコードしてはならない。

OpenGepa本体は、セッションごとに `ref` と現在の対象識別子の対応をメモリ上の発行テーブルとして保持する。発行テーブルに存在しない、別セッションで発行された、または改変された `ref` は `invalid_ref` として拒否し、その内容から対象を推測して解決してはならない。

MCPクライアントは `ref` の文字列形式を解析または自作してはならない。`ref` は `list_tabs`、`browse_items`、`search_items`、`get_recent_items`、`get_frequent_items` などから取得し、同じセッションの `get_item`、`launch_item`、`update_descriptions` へそのまま渡す。

OpenGepa内部では次の既存識別子へ解決する。

- 通常アプリランチャー／WebランチャーのLauncherTab: LauncherTab ID
- Group、FileItem、DirectoryItem、UrlItem: LauncherNode ID
- Windows Menu: 参照元とStart Menu内相対パス
- ストアアプリ: AUMID
- Windows主要操作: Preset ID

MCP用 `ref` は永続データの新しい正本にしない。

`ref` は、MCPセッションの終了、本体の再起動、対象の削除、またはWindows Menuなどの再生成による対象識別子の消失で失効する。LauncherNodeの名前変更、`description` 変更、親Group変更、所属LauncherTab変更など、既存の安定IDを維持する変更だけを理由に失効させない。

### 6.2 利用不可

`ref` が示す対象を現在解決できない場合、起動可能な別対象を推測して代用してはならない。

`get_item` または `launch_item` は `not_found` または `unavailable` を返す。

FileItemの既存ドライブ補正など、OpenGepa本体の通常起動処理が内部的に行う既存の修復処理はそのまま利用できる。


## 7. MCP Tools

v0.4.0ではToolsだけを公開し、Resources、Prompts、MCP Appsは公開しない。

Tool名は次を正本とする。

### 7.0 ページング

`browse_items` と `search_items` の `cursor` は、不透明なセッション内トークンとする。OpenGepa本体は発行時に検索条件、並び順、対象コレクションのgenerationを記録する。異なる検索条件へ渡したcursor、別セッションのcursor、改変されたcursor、または対象一覧の変化で失効したcursorは `invalid_cursor` として拒否する。cursorの内容から対象や検索条件を推測してはならない。

### 7.1 `list_tabs`

OpenGepaの現在の縦タブ一覧を返す。

入力:

- `include_hidden`: boolean、既定 `false`

主な出力:

- `ref`
- `name`
- `tab_type`
- `visible`
- `order`
- `description`
- `describable`
- `browsable`

通常アプリランチャー／Webランチャーでは利用者の `description` を返す。

システムタブでは `description` はOpenGepa固定の説明を返してよいが、`describable` は `false` とする。

### 7.2 `browse_items`

指定したタブまたはGroupの直下を一覧する。

入力:

- `parent_ref`
- `limit`: 既定50、最大100
- `cursor`: 任意

主な出力:

- `ref`
- `kind`
- `name`
- `description`
- `available`
- `launchable`
- `describable`
- `has_children`
- `next_cursor`

通常アプリランチャー、Webランチャー、Windows Menu、ストアアプリ、Windows主要操作を参照できる。

起動履歴と使用頻度は専用Toolで参照する。

### 7.3 `search_items`

現在のOpenGepaから対象を検索する。

入力:

- `query`: 検索文字列
- `include_hidden`: boolean、既定 `false`
- `scopes`: 任意。通常ランチャー、Webランチャー、Windows Menu、ストアアプリ、主要操作を絞り込める
- `limit`: 既定30、最大100
- `cursor`: 任意

検索対象:

- 表示名
- 通常／Webランチャーの `description`
- Windows主要操作の固定説明
- Windows MenuでOpenGepaが安全に取得できる表示情報
- ストアアプリの表示名

検索結果の優先順位は、表示名の完全一致、表示名の前方一致、表示名の部分一致、`description` の部分一致を基本とする。

v0.4.0ではベクトル検索、Embedding、外部AI APIを使用しない。

### 7.4 `get_item`

現在の対象の詳細を返す。

入力:

- `ref`
- `include_target`: boolean、既定 `false`

通常出力:

- `ref`
- `kind`
- `name`
- `description`
- `tab_name`
- `available`
- `launchable`
- `describable`

`include_target=false` の場合、ローカルファイルのフルパスやURLなどの実起動先は返さない。

`include_target=true` の場合だけ、OpenGepaが現在保持するFileItem、DirectoryItem、UrlItemの対象情報を、次の形の `target` として追加してよい。

```json
{
  "target": {
    "kind": "file",
    "value": "C:\\..."
  }
}
```

URLの場合は `kind` を `url` とする。Windows内部の実装情報や、OpenGepaが通常表示しない秘密情報を新たに探索して返してはならない。

### 7.5 `launch_item`

既存のOpenGepa項目を起動する。

入力:

- `ref`

このToolは［MCPから項目を起動する］がOFFの場合は実行しない。

起動可能対象:

- 通常アプリランチャーのFileItem
- 通常アプリランチャーのDirectoryItem
- 通常アプリランチャーのUrlItem
- WebランチャーのUrlItem
- Windows Menuの現在存在するショートカット
- ストアアプリ
- MCP公開を許可したWindows主要操作Preset

Group、SeparatorItem、LauncherTab、履歴行、頻度行そのものは起動しない。

MCPは任意のファイルパス、任意URL、任意コマンド、任意PowerShellを引数として渡して起動できない。

成功判定はv0.3.0の起動記録と同じく、OpenGepaがWindowsへ起動要求を正常に受け渡せたことを指す。

MCPから成功起動した場合も、既存の単一起動記録サービスを通じて履歴と使用頻度へ一度だけ記録する。［起動記録に残さない］に指定された項目はMCPから起動しても記録しない。

主な結果:

- `launched`
- `unavailable`
- `not_allowed`
- `disabled`
- `failed`

### 7.6 `get_recent_items`

v0.3.0の起動記録から、最近の成功起動を新しい順に返す。

入力:

- `limit`: 既定20、最大100

履歴へ保存されている元の成功起動記録を基準とし、現在解決可能かどうかも返す。

主な出力:

- `ref`
- `name`
- `description`
- `launched_at`
- `available`

現在解決できない対象は起動用 `ref` を返さず、最後に記録した名前を返してよい。

起動記録から除外された項目は、そもそもこのToolの対象にならない。

### 7.7 `get_frequent_items`

v0.3.0の使用頻度集計を返す。

入力:

- `period`: `30d` または `all`、既定 `30d`
- `limit`: 既定20、最大100

現在解決できる項目だけを、v0.3.0の使用頻度タブと同じ並び順で返す。

主な出力:

- `ref`
- `name`
- `description`
- `count`
- `last_launched_at`

### 7.8 `update_descriptions`

通常アプリランチャー／WebランチャーのLauncherTab、Group、FileItem、DirectoryItem、UrlItemの `description` を更新する。

このToolは［MCPから説明を編集する］がOFFの場合は実行しない。

入力:

- `updates`: 1件以上100件以下の配列
  - `ref`
  - `description`: string または null

`description` がnull、空文字、空白だけの場合は説明を未設定へ戻す。

全件を先に検証し、一件でも更新不可、対象不存在、文字数超過その他のエラーがある場合は何も変更しない。

全件の検証に成功した場合だけ、メモリ上の変更と `opengepa.json` の保存を一つの原子的な変更として行う。

保存に失敗した場合は、UI上の `description` も更新前へ戻す。

MCPクライアント向けのTool説明には、利用者から別の指示がない限り日本語で記述することを明記する。


## 8. Windows主要操作のMCP公開

Windows主要操作の固定カタログへ、MCPから実行可能かを示す固定属性を追加する。属性名は実装上 `AllowMcp` などとしてよい。

v0.4.0の各Presetの値は次を正本とする。実装者が名前や処理内容から自動判定してはならない。将来Presetを追加する場合も、同じくPresetごとに明示する。

| Preset ID | 表示名 | MCP |
| --- | --- | --- |
| `settings` | 設定 | 許可 |
| `search` | 検索 | 許可 |
| `run` | ファイル名を指定して実行 | 禁止 |
| `explorer` | エクスプローラー | 許可 |
| `desktop` | デスクトップ | 許可 |
| `documents` | ドキュメント | 許可 |
| `pictures` | ピクチャ | 許可 |
| `music` | ミュージック | 許可 |
| `recent` | 最近使った項目 | 許可 |
| `this-pc` | PC | 許可 |
| `explorer-options` | エクスプローラーのオプション | 禁止 |
| `recycle-bin` | ゴミ箱を開く | 許可 |
| `installed-apps` | インストールされているアプリ | 許可 |
| `default-apps` | 既定のアプリ | 許可 |
| `programs-features` | プログラムと機能 | 禁止 |
| `windows-features` | Windows の機能 | 禁止 |
| `microsoft-store` | Microsoft Store | 許可 |
| `nvidia-control-panel` | NVIDIA コントロール パネル | 許可 |
| `amd-software` | AMD Software: Adrenalin Edition | 許可 |
| `intel-graphics-command-center` | Intel Graphics Command Center | 許可 |
| `intel-arc-control` | Intel Arc Control | 許可 |
| `system` | システム | 許可 |
| `system-properties` | システムのプロパティ | 禁止 |
| `power-options` | 電源オプション | 禁止 |
| `mobility-center` | モビリティ センター | 禁止 |
| `device-manager` | デバイス マネージャー | 禁止 |
| `disk-management` | ディスクの管理 | 禁止 |
| `computer-management` | コンピューターの管理 | 禁止 |
| `mouse-settings` | マウスの設定 | 許可 |
| `display-settings` | ディスプレイの設定 | 許可 |
| `bluetooth-settings` | Bluetooth とデバイス | 許可 |
| `printers-settings` | プリンターとスキャナー | 許可 |
| `network-connections` | ネットワーク接続 | 禁止 |
| `network-sharing-center` | ネットワークと共有センター | 禁止 |
| `internet-options` | インターネットのプロパティ | 禁止 |
| `remote-desktop` | リモート デスクトップ | 禁止 |
| `event-viewer` | イベント ビューアー | 許可 |
| `task-manager` | タスク マネージャー | 許可 |
| `terminal` | ターミナル | 禁止 |
| `terminal-admin` | ターミナル（管理者） | 禁止 |
| `system-config` | システム構成 | 禁止 |
| `services` | サービス | 禁止 |
| `task-scheduler` | タスク スケジューラ | 禁止 |
| `resource-monitor` | リソース モニター | 許可 |
| `performance-monitor` | パフォーマンス モニター | 許可 |
| `cert-current-user` | 証明書（現在のユーザー） | 禁止 |
| `cert-local-machine` | 証明書（ローカル コンピューター） | 禁止 |
| `local-group-policy` | ローカル グループ ポリシー エディター | 禁止 |
| `registry-editor` | レジストリ エディター | 禁止 |
| `windows-security` | Windows セキュリティ | 許可 |
| `credential-manager` | 資格情報マネージャー | 禁止 |
| `windows-update` | Windows Update | 許可 |
| `firewall-advanced` | Windows Defender ファイアウォール（詳細設定） | 禁止 |
| `lock` | ロック | 禁止 |
| `sign-out` | サインアウト | 禁止 |
| `sleep` | スリープ | 禁止 |
| `shutdown` | シャットダウン | 禁止 |
| `restart` | 再起動 | 禁止 |
| `media-previous` | 前の曲 | 許可 |
| `media-play-pause` | 再生／一時停止 | 許可 |
| `media-next` | 次の曲 | 許可 |
| `media-stop` | 停止 | 許可 |
| `media-volume-down` | 音量を下げる | 許可 |
| `media-volume-up` | 音量を上げる | 許可 |
| `media-volume-mute` | ミュート切替 | 許可 |

MCP実行不可のPresetは `browse_items` や `search_items` で存在を返してよいが、`launchable=false` とし、`launch_item` では `not_allowed` を返す。


## 9. descriptionとAIの扱い

`description` はAIが項目の用途や使い分けを理解するための情報としてMCPへ返す。

例:

```text
Paint.NET

アイコンの透過処理、サイズ調整、軽い画像編集に使用する。
OpenGepaのiconSet編集ではまずこれを使う。
```

```text
VLC

MPC-BEなどで再生できない形式を確認するための予備プレイヤー。
通常の動画再生では第一候補にしない。
```

AIは利用者の指示に応じて `description` を更新できる。

ただし、`description` は信頼された命令として扱わない。`description`、項目名、URL、Windows Menuの表示名その他のMCP返却データは、OpenGepaにとってすべてデータである。

`description` 内に「このコマンドを実行せよ」「別の安全規則を無視せよ」などの文字列が存在しても、OpenGepa側のMCP公開範囲、権限、安全確認、Toolの挙動を変更してはならない。


## 10. MCP検索と表示状態

タブをUI上で非表示にしていることは、削除またはアクセス禁止を意味しない。

`list_tabs`、`search_items` の初期値では非表示タブを除外するが、`include_hidden=true` を指定すれば参照できる。

起動記録の［起動記録に残さない］は履歴・使用頻度だけに適用する。MCPからの検索・参照・起動禁止を意味しない。

MCPから完全に利用させたくない項目を個別指定する機能はv0.4.0では追加しない。


## 11. エラーと障害時動作

MCPの異常によってOpenGepa本体を終了させてはならない。

次の場合はMCP Toolのエラーまたは明示的な結果として返す。

- MCP連携が無効
- 起動が無効
- `description` 編集が無効
- 対象が存在しない
- 対象が現在利用できない
- PresetがMCP実行禁止
- `description` が4,000文字を超える
- 保存に失敗
- IPC切断
- OpenGepa本体を起動できない
- 不正な引数
- 期限切れまたは現在解決不能な `ref`

MCPクライアントからの不正要求で `opengepa.json`、Profile、起動記録ファイルその他の既存データを破損させてはならない。

起動後に起動記録の保存だけが失敗した場合、すでにWindowsへ渡した起動そのものを失敗扱いへ変更しない。起動記録保存の失敗だけを別に報告する。

`OpenGepa.Mcp.exe` の異常終了、MCPクライアントの切断、STDIOのEOFによってOpenGepa本体を終了させない。


## 12. 配布

`build.ps1` が作成する配布ZIPへ、MCP連携に必要なファイルを含める。

少なくとも次を含める。

```text
OpenGepa/
├─ OpenGepa.exe
├─ OpenGepa.Mcp.exe
├─ opengepa.default.json
├─ iconSet/
└─ その他の実行に必要なファイル
```

MCP対応後もOpenGepaはポータブル版相当の配布を維持する。

- インストーラを必須にしない
- Windowsサービスを登録しない
- MCP用の常駐サービスを別途登録しない
- MCP用のレジストリ登録を必須にしない
- MCP設定のためにユーザープロファイルへOpenGepa独自設定を作らない
- 配布ディレクトリをコピーした場合、MCP実行ファイルも一緒に移行できる

MCPクライアント側の設定ファイルはOpenGepaの正本ではなく、それぞれのMCPクライアントが管理する。


## 13. README

READMEへ［MCP連携］を追加する。

少なくとも次を説明する。

- MCP連携は初期状態でOFF
- 設定画面から有効化する
- MCPサーバー実行ファイルは `OpenGepa.Mcp.exe`
- STDIO型のローカルMCPサーバーである
- MCPクライアントには `OpenGepa.Mcp.exe` のフルパスを登録する
- `OpenGepa.exe` が起動していない場合はMCP実行ファイルが起動を試みる
- MCPから任意コマンドを実行する機能はない
- MCPから変更できるOpenGepaデータはv0.4.0では `description` に限定する
- 電源・セッション系PresetはMCPから実行できない
- `description` は特に指定がなければ日本語で書くことを推奨する


## 14. テスト

少なくとも次の回帰テストを追加する。

### 14.1 description

- 既存v0.3データを読み込むと `description` 未設定で動作する
- LauncherTabの `description` を保存・再読込できる
- Group、FileItem、DirectoryItem、UrlItemの `description` を保存・再読込できる
- 改名しても `description` が残る
- 親Groupを変更しても `description` が残る
- LauncherTab複製で `description` も複製される
- Profile保存・読込で `description` が移行される
- 4,000文字を超える `description` を拒否する
- 空白だけの `description` を未設定として扱う
- SeparatorItemに `description` を設定できない

### 14.2 MCP基本

- MCP無効時にToolを実行できない
- MCP実行ファイルが本体を起動しても、MCP無効時に設定を変更せず `disabled` を返す
- MCP有効時に `list_tabs` が現在状態を返す
- `browse_items` が階層を返す
- `search_items` が表示名で検索できる
- `search_items` が `description` で検索できる
- 人間の通常検索は `description` に一致しても結果へ出さない
- `get_item` の `include_target=false` で実起動先を返さない
- `browse_items` と `get_item` の通常応答が実起動先を返さない
- `get_item` は `include_target=true` の場合だけ、型付きの `target` を返す
- `ref` を偽造・改変した場合に対象を推測しない
- 別MCPセッションで発行された `ref` を拒否する
- MCPセッション終了、本体再起動、対象削除、再生成で `ref` が失効する
- 名前変更、description変更、親Group変更、所属LauncherTab変更では `ref` を維持する
- 改変、条件不一致、一覧更新後の `cursor` を `invalid_cursor` として拒否する

### 14.3 起動

- MCP起動許可OFFでは `launch_item` を拒否する
- FileItemを既存起動処理で起動する
- DirectoryItemを既存起動処理で起動する
- UrlItemを既存起動処理で起動する
- Windows Menuを既存起動処理で起動する
- ストアアプリを既存起動処理で起動する
- 許可されたPresetを起動できる
- メディアコントロールの許可Presetを実行できる
- Presetカタログの全件が仕様の `AllowMcp` 値と一致する
- ファイル名を指定して実行、Terminal、Terminal（管理者）、レジストリ エディター、ローカル グループ ポリシー エディターをMCPから実行できない
- ロック、サインアウト、スリープ、シャットダウン、再起動をMCPから実行できない
- MCPから一度成功起動した時、履歴と使用頻度へ一度だけ反映される
- 起動記録除外項目はMCPから起動しても履歴・頻度へ記録されない
- 任意パス、任意URL、任意コマンドを `launch_item` へ渡して実行できない

### 14.4 descriptionのMCP更新

- 説明編集許可OFFでは `update_descriptions` を拒否する
- 1件更新できる
- 複数件を一度の保存で更新できる
- 一件でも不正なら全件変更しない
- 保存失敗時にメモリとUIも変更前を維持する
- 更新後のUIへ直ちに反映される
- システムタブやSeparatorItemの説明を更新できない

### 14.5 IPCとSTDIO

- `OpenGepa.Mcp.exe` が設定ファイルを直接変更しない
- OpenGepa本体が未起動の場合に起動を試みる
- 本体起動失敗時に明示的なエラーを返す
- IPC切断でOpenGepa本体を終了しない
- MCPクライアント終了でOpenGepa本体を終了しない
- MCPの標準出力へ診断ログを混入させない
- 同じ配置ディレクトリのOpenGepaは一つだけ起動し、既存インスタンスを表示できる
- 同じWindowsユーザーの別ディレクトリにあるOpenGepaは同時に起動でき、Mutex、表示通知、IPCが衝突しない
- MCP実行ファイルが別配置のOpenGepaへ接続またはフォールバックしない
- 複数MCP接続から同時に `description` 更新しても保存ファイルを破損しない


## 15. v0.4.0完成条件

- v0.1～v0.3の既存データと機能を壊さずに起動できる
- 通常アプリランチャー／WebランチャーのLauncherTab、Group、FileItem、DirectoryItem、UrlItemが `description` を保持できる
- 人間が `description` を表示・編集できる
- `description` がProfileで移行できる
- `OpenGepa.Mcp.exe` がSTDIO MCPサーバーとして動作する
- MCP処理の正本が常にOpenGepa本体であり、MCP実行ファイルが設定ファイルを直接更新しない
- MCPからタブ・項目を一覧、検索、詳細取得できる
- MCP検索が `description` を利用できる
- MCPから起動履歴と使用頻度を参照できる
- MCPから既存の許可対象を起動できる
- MCPから一度の成功起動が履歴・頻度へ一度だけ記録される
- MCPから電源・セッション系Presetを実行できない
- MCPから任意ファイル、任意URL、任意コマンド、任意PowerShellを実行できない
- MCPから `description` を原子的に更新できる
- MCP連携を完全OFF、起動OFF、説明編集OFFへ切り替えられる
- MCP用のネットワーク待受け、Windowsサービス、レジストリ登録を追加しない
- `build.ps1` がMCP対応を含むポータブル配布ZIPを生成できる
- READMEにMCP連携手順を追加する
- 新規・変更した規則に回帰テストを追加し、既存テストを含めて成功する
