using System.ClientModel;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace AegisAgent.Core;

/// <summary>
/// Application-facing Microsoft Agent Framework runtime.
/// UI projects depend on this service instead of constructing MAF agents directly.
/// </summary>
public sealed class MafCodingAgentService
{
    private MafCodingAgentService(
        AgentSettings settings,
        CodingWorkspace workspace,
        AIAgent agent,
        IReadOnlyCollection<AITool> tools)
    {
        Settings = settings;
        Workspace = workspace;
        Agent = agent;
        Tools = tools;
    }

    public AgentSettings Settings { get; }

    public CodingWorkspace Workspace { get; }

    public AIAgent Agent { get; }

    public IReadOnlyCollection<AITool> Tools { get; }

    public AgentSession? Session { get; private set; }

    public static MafCodingAgentService Create(AgentSettings settings, CodingWorkspace workspace)
    {
        ChatClient openAiClient = CreateChatClient(settings);
        IChatClient chatClient = openAiClient.AsIChatClient();
        AITool[] tools = workspace.CreateTools();
        HarnessAgentOptions harnessOptions = new()
        {
            Name = "Aegis Coding Agent",
            HarnessInstructions = "Act as a careful, repository-aware coding agent. Work in small, verifiable steps.",
            DisableWebSearch = true,
            DisableFileMemory = true,
            MaximumIterationsPerRequest = settings.MaxToolIterations,
            ChatOptions = new ChatOptions
            {
                Instructions = AgentInstructions(workspace.RootPath),
                Tools = tools,
                MaxOutputTokens = settings.MaxOutputTokens,
            },
        };

        AIAgent agent = chatClient.AsHarnessAgent(harnessOptions);
        return new MafCodingAgentService(settings, workspace, agent, tools);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Session = await Agent.CreateSessionAsync(cancellationToken);
    }

    public async Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        Session = await Agent.CreateSessionAsync(cancellationToken);
    }

    public async Task<string> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (Session is null)
        {
            await InitializeAsync(cancellationToken);
        }

        StringBuilder output = new();
        await foreach (AgentResponseUpdate update in Agent.RunStreamingAsync(prompt, Session!))
        {
            output.Append(update.ToString());
        }

        return output.ToString().Trim();
    }

    private static ChatClient CreateChatClient(AgentSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return new ChatClient(settings.Model, settings.ApiKey);
        }

        OpenAIClientOptions clientOptions = new()
        {
            Endpoint = new Uri(settings.BaseUrl),
        };

        return new ChatClient(
            model: settings.Model,
            credential: new ApiKeyCredential(settings.ApiKey),
            options: clientOptions);
    }

    private static string AgentInstructions(string workspaceRoot) => $"""
You are Aegis, an expert coding agent operating in the repository at `{workspaceRoot}`.

Your job is to solve software tasks end-to-end, similar to a terminal coding agent:
1. Understand the request and inspect the repository before editing.
2. In plan mode, explain a concise plan and keep it synchronized with todos.
3. In execute mode, make focused changes using the workspace tools. Never invent file contents you have not inspected.
4. Prefer the smallest correct change. Preserve existing conventions and user changes.
5. Run the most relevant build, tests, formatter, or focused verification after edits.
6. If verification fails, diagnose the failure, fix it, and verify again when safe.
7. End with a concise summary of changes, verification results, and any remaining user action.

Workspace and safety rules:
- All paths are relative to the workspace unless a tool says otherwise. Never access paths outside the workspace.
- Read/search before modifying. Use `replace_in_file` for precise edits and `write_file` only for new or wholly rewritten files.
- Do not delete, reset, clean, overwrite unrelated changes, rotate secrets, or modify generated output.
- Do not place API keys or other secrets in source files.
- Ask the user when requirements are ambiguous or a destructive action is genuinely required.
- Use `run_command` for focused, repository-local verification; keep commands short and explain why you need them.

The user may ask in Japanese or English. Match the user's language.
""";
}
