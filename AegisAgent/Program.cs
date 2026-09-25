using System.Text;
using AegisAgent;
using AegisAgent.Core;
using Spectre.Console;

ConfigureConsoleEncoding();
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
ChatGptOAuthCredentialStore oauth = new();

if (settings.BackendKind.Equals("maf-chatgpt-oauth", StringComparison.OrdinalIgnoreCase))
{
    MafCodingAgentService? oauthService = null;
    try
    {
        if (oauth.HasStoredCredential)
        {
            oauthService = await MafCodingAgentService.CreateAsync(settings, workspace, oauth);
            await oauthService.InitializeAsync();
        }
    }
    catch (Exception exception)
    {
        AnsiConsole.MarkupLine($"[yellow]保存済み ChatGPT OAuth を読み込めませんでした: {Markup.Escape(exception.Message)}[/]");
    }

    await new AegisTui(settings, providerRegistry, workspace, oauthService, oauth).RunAsync();
    return;
}

if (string.IsNullOrWhiteSpace(settings.ApiKey) &&
    !settings.BackendKind.Equals("maf-local", StringComparison.OrdinalIgnoreCase))
{
    AnsiConsole.MarkupLine("[yellow]API キーが未設定です。まず TUI で /provider add または /auth openai を実行してください。[/]");
    await new AegisTui(settings, providerRegistry, workspace, null, oauth).RunAsync();
    return;
}

MafCodingAgentService mafService = MafCodingAgentService.Create(settings, workspace);
await mafService.InitializeAsync();
await new AegisTui(settings, providerRegistry, workspace, mafService, oauth).RunAsync();

static void ConfigureConsoleEncoding()
{
    try
    {
        UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = utf8;
        Console.OutputEncoding = utf8;
    }
    catch (IOException)
    {
        // Some redirected or legacy hosts do not allow changing the console code page.
    }
}
