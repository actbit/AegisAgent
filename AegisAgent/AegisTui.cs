using System.Text;
using AegisAgent.Core;
using Spectre.Console;

namespace AegisAgent;

internal sealed class AegisTui
{
    private readonly AgentSettings settings;
    private readonly ProviderRegistry providerRegistry;
    private readonly CodingWorkspace workspace;
    private readonly MafCodingAgentService? mafService;
    private readonly CodexAppServerClient? codex;
    private readonly List<ChatEntry> history = [];
    private int turnNumber;

    public AegisTui(
        AgentSettings settings,
        ProviderRegistry providerRegistry,
        CodingWorkspace workspace,
        MafCodingAgentService? mafService,
        CodexAppServerClient? codex)
    {
        this.settings = settings;
        this.providerRegistry = providerRegistry;
        this.workspace = workspace;
        this.mafService = mafService;
        this.codex = codex;
    }

    public async Task RunAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;
        TryClear();
        RenderWelcome();

        while (true)
        {
            RenderDashboard();
            string input;
            try
            {
                input = ReadInput();
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase) || input.Equals("/quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (await HandleCommandAsync(input.Trim()))
            {
                continue;
            }

            await RunTurnAsync(input.Trim());
        }

        AnsiConsole.MarkupLine("[grey]Aegis を終了しました。[/]");
    }

    private async Task RunTurnAsync(string input)
    {
        turnNumber++;
        history.Add(new ChatEntry("user", input));
        AnsiConsole.MarkupLine($"[grey]Turn {turnNumber}: agent is working...[/]");

        try
        {
            string answer;
            if (codex is not null)
            {
                string progress = "starting";
                answer = await codex.RunTurnAsync(input, value => progress = value);
                if (!string.IsNullOrWhiteSpace(progress))
                {
                    AnsiConsole.MarkupLine($"[grey]Codex app-server: {Markup.Escape(progress)}[/]");
                }
            }
            else if (mafService is not null)
            {
                answer = await mafService.RunAsync(input);
            }
            else
            {
                RenderPanel("Provider setup", "No active provider is configured. Use /provider add or /auth openai.", Color.Yellow);
                return;
            }

            history.Add(new ChatEntry("assistant", answer));
            RenderPanel("assistant", string.IsNullOrWhiteSpace(answer) ? "(no text response)" : answer, Color.Green);
        }
        catch (Exception exception)
        {
            RenderPanel("error", exception.Message, Color.Red);
        }
    }

    private async Task<bool> HandleCommandAsync(string input)
    {
        if (input.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            RenderHelp();
            return true;
        }

        if (input.Equals("/clear", StringComparison.OrdinalIgnoreCase))
        {
            if (mafService is not null)
            {
                await mafService.ResetSessionAsync();
            }

            history.Clear();
            turnNumber = 0;
            TryClear();
            RenderWelcome();
            return true;
        }

        if (input.Equals("/status", StringComparison.OrdinalIgnoreCase))
        {
            RenderPanel("git status", workspace.GetGitStatus(), Color.Grey);
            return true;
        }

        if (input.Equals("/workspace", StringComparison.OrdinalIgnoreCase))
        {
            RenderPanel("workspace", workspace.RootPath, Color.Grey);
            return true;
        }

        if (input.Equals("/auth openai", StringComparison.OrdinalIgnoreCase))
        {
            await LoginOpenAiAsync();
            return true;
        }

        if (input.StartsWith("/provider", StringComparison.OrdinalIgnoreCase))
        {
            await HandleProviderCommandAsync(input);
            return true;
        }

        return false;
    }

    private async Task HandleProviderCommandAsync(string input)
    {
        string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string subcommand = parts.Length > 1 ? parts[1].ToLowerInvariant() : "list";

        switch (subcommand)
        {
            case "list":
                RenderProviders();
                break;
            case "add":
                await AddProviderAsync();
                break;
            case "use" when parts.Length > 2:
                if (providerRegistry.SetActive(string.Join(' ', parts.Skip(2))))
                {
                    RenderPanel("provider", "保存しました。次回起動から選択したプロファイルを使います。", Color.Green);
                }
                else
                {
                    RenderPanel("provider", "プロファイルが見つかりません。/provider list で確認してください。", Color.Red);
                }

                break;
            case "remove" when parts.Length > 2:
                if (providerRegistry.Remove(string.Join(' ', parts.Skip(2))))
                {
                    RenderPanel("provider", "プロファイルを削除しました。", Color.Green);
                }

                break;
            default:
                RenderPanel("provider", "Usage: /provider list | add | use <name> | remove <name>", Color.Yellow);
                break;
        }
    }

    private async Task AddProviderAsync()
    {
        string type = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("登録するプロバイダーを選択")
                .AddChoices("OpenAI API", "DeepSeek", "OpenAI-compatible", "OpenAI ChatGPT OAuth (Codex app-server)"));
        string name = AnsiConsole.Ask<string>("プロファイル名:");

        if (type.StartsWith("OpenAI ChatGPT", StringComparison.Ordinal))
        {
            providerRegistry.Upsert(name, "openai-chatgpt-oauth", "gpt-6-sol", null, null, null);
            providerRegistry.SetActive(name);
            RenderPanel("provider", "OAuth プロファイルを保存しました。/auth openai でログインしてから再起動してください。", Color.Green);
            return;
        }

        string defaultModel = type switch
        {
            "DeepSeek" => "deepseek-chat",
            "OpenAI API" => "gpt-4.1-mini",
            _ => "gpt-4.1-mini",
        };
        string defaultBaseUrl = type switch
        {
            "DeepSeek" => "https://api.deepseek.com/v1",
            "OpenAI API" => "",
            _ => "https://example.com/v1",
        };
        string model = AnsiConsole.Ask("モデル名:", defaultModel);
        string baseUrl = AnsiConsole.Ask("Base URL（OpenAI は空欄）:", defaultBaseUrl);
        string apiKeyEnv = AnsiConsole.Ask("API キーを読む環境変数名（空欄なら暗号化保存）:", "");
        string? apiKey = string.IsNullOrWhiteSpace(apiKeyEnv)
            ? AnsiConsole.Prompt(new TextPrompt<string>("API キー:").Secret())
            : null;

        try
        {
            providerRegistry.Upsert(name, type == "DeepSeek" ? "deepseek" : "openai-compatible", model, baseUrl, apiKey, apiKeyEnv);
            providerRegistry.SetActive(name);
            RenderPanel("provider", $"{name} を保存しました。次回起動から利用します。", Color.Green);
        }
        catch (Exception exception)
        {
            RenderPanel("provider", exception.Message, Color.Red);
        }

        await Task.CompletedTask;
    }

    private async Task LoginOpenAiAsync()
    {
        if (codex is not null)
        {
            try
            {
                bool success = await codex.LoginWithChatGptAsync();
                RenderPanel("OpenAI OAuth", success ? "ChatGPT OAuth に成功しました。" : "OAuth に失敗しました。", success ? Color.Green : Color.Red);
            }
            catch (Exception exception)
            {
                RenderPanel("OpenAI OAuth", exception.Message, Color.Red);
            }

            return;
        }

        await using CodexAppServerClient temporary = new(
            workspace.RootPath,
            "gpt-6-sol",
            PromptApprovalAsync);
        try
        {
            await temporary.StartAsync();
            bool success = await temporary.LoginWithChatGptAsync();
            if (success)
            {
                providerRegistry.Upsert("openai-chatgpt", "openai-chatgpt-oauth", "gpt-6-sol", null, null, null);
                providerRegistry.SetActive("openai-chatgpt");
            }

            RenderPanel("OpenAI OAuth", success ? "ChatGPT OAuth に成功しました。次回起動ではこのプロバイダーを使います。" : "OAuth に失敗しました。", success ? Color.Green : Color.Red);
        }
        catch (Exception exception)
        {
            RenderPanel("OpenAI OAuth", exception.Message + "（Codex CLI / app-server が必要です）", Color.Red);
        }
    }

    private Task<string> PromptApprovalAsync(string action)
    {
        bool accepted = AnsiConsole.Confirm($"[yellow]許可しますか？[/] {Markup.Escape(action)}", false);
        return Task.FromResult(accepted ? "accept" : "decline");
    }

    private static string ReadInput()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? "/exit";
        }

        return AnsiConsole.Ask<string>("[bold deepskyblue1]aegis[/]> ");
    }

    private static void TryClear()
    {
        if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
        {
            AnsiConsole.Clear();
        }
    }

    private void RenderWelcome()
    {
        AnsiConsole.Write(new FigletText("AEGIS").Centered().Color(Color.DeepSkyBlue1));
        AnsiConsole.Write(new Panel(new Markup("[bold]Microsoft Agent Framework Coding TUI[/]\nPlan · Todo · Tools · Verify"))
            .Border(BoxBorder.Rounded)
            .Header("[deepskyblue1]Aegis Coding Agent[/]"));
        AnsiConsole.MarkupLine("[grey]依頼を入力。/help、/provider、/auth openai、/exit が使えます。[/]");
    }

    private void RenderDashboard()
    {
        Table table = new Table().NoBorder().AddColumn(new TableColumn("").Width(18)).AddColumn(new TableColumn(""));
        table.AddRow("Provider", Markup.Escape(settings.ProviderName));
        table.AddRow("Backend", Markup.Escape(settings.BackendKind));
        table.AddRow("Model", Markup.Escape(settings.Model));
        table.AddRow("Workspace", Markup.Escape(workspace.RootPath));
        table.AddRow("Turns", turnNumber.ToString());
        if (codex?.AccountSummary is not null)
        {
            table.AddRow("Account", Markup.Escape(codex.AccountSummary));
        }

        Grid grid = new Grid().AddColumn().AddColumn();
        grid.AddRow(new Panel(table).Header("[bold]session[/]").Border(BoxBorder.Rounded), new Panel(new Text(RecentHistory())).Header("[bold]conversation[/]").Border(BoxBorder.Rounded));
        AnsiConsole.Write(grid);
    }

    private string RecentHistory()
    {
        if (history.Count == 0)
        {
            return "(no messages yet)";
        }

        return string.Join("\n", history.TakeLast(6).Select(entry => $"{entry.Role}: {entry.Text.Replace('\n', ' ')[..Math.Min(entry.Text.Replace('\n', ' ').Length, 90)]}"));
    }

    private void RenderProviders()
    {
        Table table = new Table().Border(TableBorder.Rounded).AddColumn("Active").AddColumn("Name").AddColumn("Kind").AddColumn("Model");
        foreach (ProviderProfile profile in providerRegistry.Profiles)
        {
            table.AddRow(
                profile.Name.Equals(providerRegistry.ActiveProvider, StringComparison.OrdinalIgnoreCase) ? "*" : "",
                Markup.Escape(profile.Name),
                Markup.Escape(profile.Kind),
                Markup.Escape(profile.Model));
        }

        if (providerRegistry.Profiles.Count == 0)
        {
            RenderPanel("providers", "登録済みプロファイルはありません。/provider add または /auth openai を使ってください。", Color.Yellow);
        }
        else
        {
            AnsiConsole.Write(table);
        }
    }

    private void RenderHelp()
    {
        AnsiConsole.Write(new Panel(new Markup(
            "/help                 この画面\n" +
            "/provider list        登録済みプロバイダー\n" +
            "/provider add         プロバイダー登録ウィザード\n" +
            "/provider use <name>  次回起動のプロバイダー切替\n" +
            "/auth openai          ChatGPT OAuth ログイン\n" +
            "/status               git status\n" +
            "/clear                会話セッションをクリア\n" +
            "/exit                 終了"))
            .Header("[bold]commands[/]")
            .Border(BoxBorder.Rounded));
    }

    private static void RenderPanel(string title, string text, Color color)
    {
        AnsiConsole.Write(new Panel(new Text(text)).Header($"[{color.ToMarkup()}]{Markup.Escape(title)}[/]").Border(BoxBorder.Rounded));
    }

    private sealed record ChatEntry(string Role, string Text);
}
