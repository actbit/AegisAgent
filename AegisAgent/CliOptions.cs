namespace AegisAgent;

internal sealed class CliOptions
{
    public string? Workspace { get; private set; }

    public string? Model { get; private set; }

    public string? BaseUrl { get; private set; }

    public string? Provider { get; private set; }

    public bool AutoApprove { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        CliOptions options = new()
        {
            AutoApprove = Environment.GetEnvironmentVariable("AEGIS_AUTO_APPROVE") is "1" or "true" or "yes",
        };

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--workspace" when index + 1 < args.Length:
                    options.Workspace = args[++index];
                    break;
                case "--model" when index + 1 < args.Length:
                    options.Model = args[++index];
                    break;
                case "--base-url" when index + 1 < args.Length:
                    options.BaseUrl = args[++index];
                    break;
                case "--provider" when index + 1 < args.Length:
                    options.Provider = args[++index];
                    break;
                case "--auto-approve":
                    options.AutoApprove = true;
                    break;
                case "--help":
                case "-h":
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete option: {args[index]}");
            }
        }

        return options;
    }

    public static bool IsHelpRequested(string[] args) => args.Any(argument => argument is "--help" or "-h");

    public static void PrintHelp()
    {
        Console.WriteLine("Aegis Coding Agent");
        Console.WriteLine();
        Console.WriteLine("Usage: dotnet run -- [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --workspace <path>     Repository/workspace root (default: current directory)");
        Console.WriteLine("  --model <name>         Model name (default: OPENAI_MODEL or gpt-4.1-mini)");
        Console.WriteLine("  --base-url <url>       OpenAI-compatible endpoint base URL");
        Console.WriteLine("  --provider <name>      Saved provider profile name");
        Console.WriteLine("  --auto-approve         Skip confirmation for file edits and commands");
        Console.WriteLine("  -h, --help             Show this help");
        Console.WriteLine();
        Console.WriteLine("Environment:");
        Console.WriteLine("  OPENAI_API_KEY / DEEPSEEK_API_KEY");
        Console.WriteLine("  OPENAI_BASE_URL=https://api.deepseek.com/v1");
        Console.WriteLine("  OPENAI_MODEL=deepseek-chat");
    }

    public static void PrintInteractiveHelp()
    {
        Console.WriteLine("/help       コマンド一覧");
        Console.WriteLine("/status     git status");
        Console.WriteLine("/workspace  作業ルート");
        Console.WriteLine("/clear      会話をクリア");
        Console.WriteLine("/exit       終了");
        Console.WriteLine("通常の入力はエージェントへの依頼です。計画後に『実行に進んで』と入力すると作業を継続できます。");
    }
}
