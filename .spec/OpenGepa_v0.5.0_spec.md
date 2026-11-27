# OpenGepa v0.5.0 自動アップデート

## 目的

GitHub Releasesで公開した、OpenGepaが署名検証できる正式配布物だけを利用者の承認後に適用する。更新時もランチャー設定、アイコン、ショートカット、起動記録、MCP設定を失わず、失敗時は旧版を起動できる状態を維持する。

## 配置

```text
<展開先>/
  OpenGepa.exe                 固定の起動窓口
  OpenGepa.Mcp.exe             固定のSTDIO MCP窓口
  app/versions/<version>/      本体、MCP本体、依存DLL、既定データ
  app/update/                  版切替状態と検証済み一時ファイル
  data/                        利用者データ
```

- 初回のv0.5.0は手動で展開する。v0.4.3以前の平置き配置にある`opengepa*.json`、`icon/`、`iconSet/`、`shortcut/`は、起動窓口が`data/`へコピーして移行する。元のファイルは削除しない。
- 本体は`data/`だけを利用者データとして読み書きする。更新ZIPに`data/`または利用者データ相当のファイルを含めない。
- 起動時に固定窓口が現在版を起動する。更新では新しい版を別ディレクトリへ展開・起動確認してから現在版を切り替え、現在版と直前の正常版を残す。
- MCPクライアントが登録するパスは固定の`OpenGepa.Mcp.exe`とする。MCP窓口は現在版のMCP本体を子プロセスとして起動する。

## 信頼と更新手順

- 正式配布はZIP、`update-manifest.json`、`update-signatures.json`の三点を同じGitHub Releaseへ添付する。
- manifestには製品名、リポジトリ、版、対象ランタイム、ZIP名、サイズ、SHA-256を記録する。RSA-PSS/SHA-256の署名を公開鍵で検証できる場合だけ更新候補とする。
- 公開鍵は本体と起動窓口に埋め込む。秘密鍵、PFX、パスワードはリポジトリ、ZIP、ログへ含めない。
- 更新確認はGitHubのlatest releaseだけを使用し、下書き・プレリリース・現行版以下・想定外のアセット・不正なリダイレクトを拒否する。
- 利用者が更新を承認した時だけダウンロードを開始する。ダウンロード後に再検証し、OpenGepaとMCPを通常終了してから適用する。失敗時は版切替せず、旧版を起動する。

## UI

- 設定の一般タブに［アプリの更新を確認］を置く。更新可能なら版番号とリリースノートを示して確認を求める。
- 署名済みポータブル版以外、または初回移行前の平置き版では、自動更新を利用できない理由と手動更新の必要を表示する。

## ビルド

- 通常の`build.ps1`はローカル動作確認用のZIPを生成する。
- `build.ps1 -Distribution Release -SigningCertificate <thumbprint>`は、CurrentUser/MyのRSA秘密鍵で署名済み三点を生成する。
- リリース時は生成物を同じGitHub Releaseへ添付する。署名に使用する証明書はOpenGepa Update ReleaseとしてCurrentUser/Myに保持する。

## 非変更範囲

- 更新確認・適用はMCP経由では実行できない。
- Profile形式、通常のランチャー機能、Windows Menuやストアアプリの環境由来データの扱いは変更しない。
