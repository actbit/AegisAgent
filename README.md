# AegisAgent

Microsoft Agent Framework の `HarnessAgent` を使った、ローカル実行にも対応する Coding Agent です。Spectre.Console ベースのリッチな TUI から、リポジトリの調査、計画、編集、検証までをマルチターンで実行します。

## 構成

```text
AegisAgent/          TUI、CLIオプション、画面描画、入力処理
AegisAgent.Core/     MAF実行、workspaceツール、プロバイダー、OAuth/app-server、設定
```

将来の GUI は `AegisAgent.Core` を参照してUIだけを置き換えられる構成です。現時点では、Shared と Services を別アセンブリに分けず、再利用単位を1つの Core ライブラリにまとめています。

## 特徴

- Microsoft Agent Framework Harness の plan / todo / mode / session
- Spectre.Console のダッシュボード、履歴、パネル、確認プロンプト
- 実行中の Tool Call / Tool Result のリアルタイム表示、クリックによる個別の展開・折りたたみ、`/tools` 一覧
- workspace 内に限定したファイル一覧、読み込み、検索、編集、git status、コマンド実行
- 編集とコマンド実行の承認ゲート（`--auto-approve` で省略可能）
- TUI からのプロバイダー登録・切替・削除
- OpenAI API、DeepSeek、Anthropic Claude、OpenRouter
- Ollama、llama.cpp、vLLM、LM Studio などのローカル/OpenAI 互換推論サーバー
- Windows DPAPI によるプロバイダー API キーのユーザー単位暗号化保存
- ChatGPT サブスクリプション向けの OpenAI OAuth（OpenCode 互換の PKCE 経由。Codex CLI 不要）

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

ウィザードで OpenAI API、DeepSeek、Anthropic Claude、Ollama、llama.cpp、vLLM、LM Studio、OpenRouter、OpenAI-compatible、ChatGPT OAuth を選べます。ローカルプロバイダーは API キーなしで登録できます。API キーが必要なプロバイダーは次のどちらかで保存できます。

- 環境変数名を登録する（推奨。実際のキーは環境変数から読む）
- Windows ではキーを現在のユーザーの DPAPI で暗号化保存する

登録済みプロファイルは `%LOCALAPPDATA%\AegisAgent\providers.json` に保存されます。OAuth の refresh token はプロバイダー一覧とは分離し、Windows DPAPI で現在のユーザーに紐づけて保存します。

## ChatGPT サブスクリプション OAuth

ChatGPT のサブスクリプションを使う場合は、TUI で次を実行します。

```text
/auth openai
```

この機能は通常の OpenAI API キー方式とは別に、OpenCode が採用している ChatGPT OAuth の PKCE ブラウザフローを使います。ブラウザーが開くので、ChatGPT にログインして認証を完了してください。成功後は `openai-chatgpt` プロファイルが有効になり、次回起動から Codex CLI / app-server なしで ChatGPT Codex endpoint に接続します。

OAuth の認証情報は `http://localhost:1455/auth/callback` で受け取り、access token の期限が近づくと refresh token で更新します。Microsoft Agent Framework の Harness と `ResponsesClient` を組み合わせるため、API キー方式と同じ workspace tools、承認ゲート、セッションを利用できます。

## TUI コマンド

```text
/help                 コマンド一覧
/toolcalls            Tool Call 表示状態
/toolcalls collapse   Tool Call / Result を折りたたむ
/toolcalls expand     Tool Call / Result を詳細表示
/tools                利用可能な Tool と説明
/provider list        登録済みプロバイダー一覧
/provider add         プロバイダー登録ウィザード
/provider use <name>  次回起動のプロバイダー切替
/provider remove <name>
/auth openai          ChatGPT OAuth ログイン
/model list           モデル候補一覧
/model use <model>    モデル切替（/model <model> も可）
/history              現在の履歴を表示
/history list         同じ workspace の履歴一覧
/history new          新しい履歴を開始
/history use           一覧から履歴を選択
/history use <id>      ID で履歴を切り替え
/history delete <id>  履歴を削除
/history clear        現在の workspace/provider の履歴を全削除
/status               git status
/workspace            作業ルート
/clear                会話セッションをクリア
/exit                 終了
```

詳細表示中の Tool Call / Tool Result パネルをクリックすると、その Tool Call だけ展開・折りたたみを切り替えられます。端末がマウスイベントに対応していない場合は `/toolcalls collapse` または `/toolcalls expand` を使ってください。

上記以外の入力は Coding Agent への依頼として処理されます。エージェントは必要に応じて計画、todo、ファイル調査、編集、検証を行います。

入力欄で `/` を押すと入力欄の下にコマンド候補が表示されます。矢印キーで候補を選び、Tab で補完できます。`/model list` は OpenAI 互換 endpoint または Anthropic endpoint からモデル一覧を取得し、`/model use ` の候補にも反映します。候補にないモデル ID もそのまま指定できます。

会話履歴は `%LOCALAPPDATA%\AegisAgent\history.jsonl` に保存され、workspace・provider・conversation session ごとに分離されます。同じフォルダでも `/history new` で複数の履歴を持てます。Agent のセッション状態も `history-sessions.json` に保存され、再起動後に直近の履歴を再開します。

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
| `ANTHROPIC_API_KEY` | なし | Anthropic Claude のキー |
| `OPENROUTER_API_KEY` | なし | OpenRouter のキー |
| `OPENAI_BASE_URL` | OpenAI | OpenAI 互換 endpoint |
| `OPENAI_MODEL` | `gpt-4.1-mini` | モデル名 |
| `AEGIS_MAX_TOOL_ITERATIONS` | `20` | 1 依頼あたりのツール反復回数 |
| `AEGIS_MAX_OUTPUT_TOKENS` | `8192` | 1 応答の最大出力トークン |
| `AEGIS_CODEX_COMMAND` | `codex` | 互換用 app-server backend を明示的に使う場合の実行ファイル |

## 開発者向け

```powershell
dotnet build .\AegisAgent.slnx --no-restore
dotnet run --project .\AegisAgent -- --help
```

`MafCodingAgentService` が MAF Harness、ChatClient、セッションを管理します。`CodingWorkspace` はパスを workspace 配下に限定し、編集とコマンド実行を確認ゲートの後に行います。TUIはサービスの構築やモデル呼び出しを直接担当しません。
