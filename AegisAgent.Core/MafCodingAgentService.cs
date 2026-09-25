using System.ClientModel;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace AegisAgent.Core;

/// <summary>
/// Application-facing Microsoft Agent Framework runtime.
/// UI projects depend on this service instead of constructing MAF agents directly.
/// </summary>
public sealed class MafCodingAgentService
{
    private static readonly JsonSerializerOptions SessionSerializerOptions =
        AgentAbstractionsJsonUtilities.DefaultOptions;

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
        IChatClient chatClient = CreateChatClient(settings);
        return CreateCore(settings, workspace, chatClient);
    }

    public static async Task<MafCodingAgentService> CreateAsync(
        AgentSettings settings,
        CodingWorkspace workspace,
        ChatGptOAuthCredentialStore oauth,
        CancellationToken cancellationToken = default)
    {
        if (!settings.BackendKind.Equals("maf-chatgpt-oauth", StringComparison.OrdinalIgnoreCase))
        {
            return Create(settings, workspace);
        }

        ResponsesClient responsesClient = await oauth.CreateResponsesClientAsync(settings.Model, cancellationToken);
        IChatClient chatClient = responsesClient.AsIChatClient(settings.Model);
        return CreateCore(settings, workspace, chatClient);
    }

    private static MafCodingAgentService CreateCore(
        AgentSettings settings,
        CodingWorkspace workspace,
        IChatClient chatClient)
    {
        AITool[] workspaceTools = workspace.CreateTools();
        AITool[] tools = [..workspaceTools, new HostedWebSearchTool()];
        HarnessAgentOptions harnessOptions = new()
        {
            Name = "Aegis Coding Agent",
            HarnessInstructions = "Act as a careful, repository-aware coding agent. Work in small, verifiable steps.",
            DisableWebSearch = false,
            DisableFileMemory = true,
            MaximumIterationsPerRequest = settings.MaxToolIterations,
            ChatOptions = new ChatOptions
            {
                Instructions = AgentInstructions(workspace.RootPath),
                Tools = workspaceTools,
                // The ChatGPT Codex endpoint rejects max_output_tokens. OpenCode
                // omits it for this backend as well.
                MaxOutputTokens = settings.BackendKind.Equals("maf-chatgpt-oauth", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : settings.MaxOutputTokens,
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

    public async Task<JsonElement?> SerializeSessionAsync(CancellationToken cancellationToken = default)
    {
        if (Session is null)
        {
            return null;
        }

        return await Agent.SerializeSessionAsync(Session, SessionSerializerOptions, cancellationToken);
    }

    public async Task RestoreSessionAsync(
        JsonElement snapshot,
        CancellationToken cancellationToken = default)
    {
        Session = await Agent.DeserializeSessionAsync(snapshot, SessionSerializerOptions, cancellationToken);
    }

    public void RestoreTextHistory(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        if (Session is null)
        {
            return;
        }

        Session.SetInMemoryChatHistory(messages.ToList());
    }

    public async Task<string> RunAsync(
        string prompt,
        Func<AgentResponseUpdate, Task>? updateHandler = null,
        CancellationToken cancellationToken = default)
    {
        if (Session is null)
        {
            await InitializeAsync(cancellationToken);
        }

        StringBuilder output = new();
        try
        {
            await foreach (AgentResponseUpdate update in Agent.RunStreamingAsync(prompt, Session!))
            {
                if (updateHandler is not null)
                {
                    await updateHandler(update);
                }

                output.Append(update.Text);
            }
        }
        catch (ClientResultException exception)
        {
            throw new InvalidOperationException(FormatServiceError(exception), exception);
        }

        return output.ToString().Trim();
    }

    private static string FormatServiceError(ClientResultException exception)
    {
        try
        {
            string detail = exception.GetRawResponse()?.Content.ToString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(detail)
                ? exception.Message
                : $"{exception.Message}\n{detail}";
        }
        catch (InvalidOperationException)
        {
            return exception.Message;
        }
    }

    private static IChatClient CreateChatClient(AgentSettings settings)
    {
        if (settings.BackendKind.Equals("maf-anthropic", StringComparison.OrdinalIgnoreCase))
        {
            ClientOptions anthropicOptions = new()
            {
                ApiKey = settings.ApiKey,
            };
            if (!string.IsNullOrWhiteSpace(settings.BaseUrl))
            {
                anthropicOptions.BaseUrl = settings.BaseUrl;
            }

            return new AnthropicClient(anthropicOptions).AsIChatClient(settings.Model, settings.MaxOutputTokens);
        }

        string apiKey = string.IsNullOrWhiteSpace(settings.ApiKey) ? "local" : settings.ApiKey;

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return new ChatClient(settings.Model, apiKey).AsIChatClient();
        }

        OpenAIClientOptions clientOptions = new()
        {
            Endpoint = new Uri(settings.BaseUrl),
        };

        return new ChatClient(
            model: settings.Model,
            credential: new ApiKeyCredential(apiKey),
            options: clientOptions).AsIChatClient();
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
- For current external information, use the hosted `WebSearch` tool when it is available and include the useful source URLs in the answer.

The user may ask in Japanese or English. Match the user's language.
""";
}
