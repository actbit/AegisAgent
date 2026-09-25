using AegisAgent;
using AegisAgent.Core;
using Spectre.Console;

DotEnv.Load(Directory.GetCurrentDirectory());

if (CliOptions.IsHelpRequested(args))
{
    CliOptions.PrintHelp();
    return;
}

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (ArgumentException exception)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
    Environment.ExitCode = 2;
    return;
}

ProviderRegistry providerRegistry = ProviderRegistry.Load();
ProviderProfile? profile = providerRegistry.Resolve(options.Provider);
AgentSettings settings = AgentSettings.Load(options.Model, options.BaseUrl, profile);
string workspaceRoot = Path.GetFullPath(options.Workspace ?? Directory.GetCurrentDirectory());

if (!Directory.Exists(workspaceRoot))
{
    AnsiConsole.MarkupLine($"[red]Workspace directory was not found: {Markup.Escape(workspaceRoot)}[/]");
    Environment.ExitCode = 2;
    return;
}

CodingWorkspace workspace = new(workspaceRoot, options.AutoApprove);

if (settings.BackendKind.Equals("codex-app-server", StringComparison.OrdinalIgnoreCase))
{
    await using CodexAppServerClient codex = new(workspaceRoot, settings.Model, AskCodexApprovalAsync);
    try
    {
        await codex.StartAsync();
        await new AegisTui(settings, providerRegistry, workspace, null, codex).RunAsync();
    }
    catch (Exception exception)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
        Environment.ExitCode = 2;
    }

    return;
}

if (string.IsNullOrWhiteSpace(settings.ApiKey))
{
    AnsiConsole.MarkupLine("[yellow]API キーが未設定です。まず TUI で /provider add または /auth openai を実行してください。[/]");
    await new AegisTui(settings, providerRegistry, workspace, null, null).RunAsync();
    return;
}

MafCodingAgentService mafService = MafCodingAgentService.Create(settings, workspace);
await mafService.InitializeAsync();
await new AegisTui(settings, providerRegistry, workspace, mafService, null).RunAsync();

static async Task<string> AskCodexApprovalAsync(string action)
{
    bool accepted = AnsiConsole.Confirm($"[yellow]許可しますか？[/] {Markup.Escape(action)}", false);
    await Task.CompletedTask;
    return accepted ? "accept" : "decline";
}
