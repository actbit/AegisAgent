# AegisAgent

Microsoft Agent Framework の `HarnessAgent` を使った、DeepSeek Agent 風のローカル Coding Agent です。Spectre.Console ベースのリッチな TUI から、リポジトリの調査、計画、編集、検証までをマルチターンで実行します。

## 構成

```text
AegisAgent/          TUI、CLIオプション、画面描画、入力処理
AegisAgent.Core/     MAF実行、workspaceツール、プロバイダー、OAuth/app-server、設定
```

将来の GUI は `AegisAgent.Core` を参照してUIだけを置き換えられる構成です。現時点では、Shared と Services を別アセンブリに分けず、再利用単位を1つの Core ライブラリにまとめています。

## 特徴

- Microsoft Agent Framework Harness の plan / todo / mode / session
- Spectre.Console のダッシュボード、履歴、パネル、確認プロンプト
- workspace 内に限定したファイル一覧、読み込み、検索、編集、git status、コマンド実行
- 編集とコマンド実行の承認ゲート（`--auto-approve` で省略可能）
- TUI からのプロバイダー登録・切替・削除
- OpenAI API、DeepSeek、その他の OpenAI 互換 API
- Windows DPAPI によるプロバイダー API キーのユーザー単位暗号化保存
- ChatGPT サブスクリプション向けの OpenAI OAuth（公式 Codex app-server 経由）

## 起動

### API キーで使う

PowerShell:

```powershell
$env:OPENAI_API_KEY = "your-api-key"
$env:OPENAI_MODEL = "gpt-4.1-mini"
dotnet run --project .\AegisAgent -- --workspace .
```

DeepSeek の場合:

```powershell
$env:DEEPSEEK_API_KEY = "your-deepseek-key"
$env:OPENAI_BASE_URL = "https://api.deepseek.com/v1"
$env:OPENAI_MODEL = "deepseek-chat"
dotnet run --project .\AegisAgent -- --workspace .
```

`.env` も利用できます。`.env.example` をコピーして値を設定してください。秘密情報はコミットしないでください。

### TUI でプロバイダーを登録する

起動後、次を入力します。

```text
/provider add
```

ウィザードで OpenAI API、DeepSeek、OpenAI-compatible、ChatGPT OAuth を選べます。API キーは次のどちらかで保存できます。

- 環境変数名を登録する（推奨。実際のキーは環境変数から読む）
- Windows ではキーを現在のユーザーの DPAPI で暗号化保存する

登録済みプロファイルは `%LOCALAPPDATA%\AegisAgent\providers.json` に保存されます。OAuth プロファイルは秘密トークンをこのファイルには保存せず、Codex app-server が管理します。

## ChatGPT サブスクリプション OAuth

ChatGPT のサブスクリプションを使う場合は、TUI で次を実行します。

```text
/auth openai
```

この機能は通常の OpenAI API キー方式ではなく、公式 Codex app-server の managed ChatGPT login を使用します。ブラウザーが開くので、ChatGPT にログインして認証を完了してください。成功後は `openai-chatgpt` プロファイルが有効になり、次回起動時から Codex app-server backend が選ばれます。

事前に Codex CLI をインストールし、`codex` または `codex.cmd` が PATH にある必要があります。別の実行ファイルを使う場合は `AEGIS_CODEX_COMMAND` で指定できます。

OAuth backend は Microsoft Agent Framework の API-key `IChatClient` とは別経路です。MAF Harness のツール実行体験は維持しつつ、ChatGPT アカウント認証、承認、ストリーミングは公式 app-server に委譲します。

## TUI コマンド

```text
/help                 コマンド一覧
/provider list        登録済みプロバイダー一覧
/provider add         プロバイダー登録ウィザード
/provider use <name>  次回起動のプロバイダー切替
/provider remove <name>
/auth openai          ChatGPT OAuth ログイン
/status               git status
/workspace            作業ルート
/clear                会話セッションをクリア
/exit                 終了
```

上記以外の入力は Coding Agent への依頼として処理されます。エージェントは必要に応じて計画、todo、ファイル調査、編集、検証を行います。

## CLI オプション

```text
--workspace <path>    作業ルート（既定: カレントディレクトリ）
--provider <name>     登録済みプロバイダーを一時選択
--model <name>        モデルを上書き
--base-url <url>      OpenAI 互換 endpoint を上書き
--auto-approve        編集・コマンド実行の確認を省略
```

## 設定環境変数

| 変数 | デフォルト | 説明 |
| --- | --- | --- |
| `OPENAI_API_KEY` | なし | OpenAI または OpenAI 互換 API のキー |
| `DEEPSEEK_API_KEY` | なし | DeepSeek のキー |
| `OPENAI_BASE_URL` | OpenAI | OpenAI 互換 endpoint |
| `OPENAI_MODEL` | `gpt-4.1-mini` | モデル名 |
| `AEGIS_MAX_TOOL_ITERATIONS` | `20` | 1 依頼あたりのツール反復回数 |
| `AEGIS_MAX_OUTPUT_TOKENS` | `8192` | 1 応答の最大出力トークン |
| `AEGIS_CODEX_COMMAND` | `codex` / `codex.cmd` | OAuth 用 app-server 実行ファイル |

## 開発者向け

```powershell
dotnet build .\AegisAgent.slnx --no-restore
dotnet run --project .\AegisAgent -- --help
```

`MafCodingAgentService` が MAF Harness、ChatClient、セッションを管理します。`CodingWorkspace` はパスを workspace 配下に限定し、編集とコマンド実行を確認ゲートの後に行います。TUIはサービスの構築やモデル呼び出しを直接担当しません。
