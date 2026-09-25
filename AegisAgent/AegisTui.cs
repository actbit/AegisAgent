using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using AegisAgent.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Spectre.Console;

namespace AegisAgent;

internal sealed class AegisTui
{
    private AgentSettings settings;
    private readonly ProviderRegistry providerRegistry;
    private readonly CodingWorkspace workspace;
    private MafCodingAgentService? mafService;
    private readonly ChatGptOAuthCredentialStore? oauth;
    private readonly ConversationHistoryStore historyStore;
    private readonly List<ChatEntry> history = [];
    private readonly Dictionary<string, ToolCallView> activeToolCalls = new(StringComparer.Ordinal);
    private readonly List<ToolCallView> toolCallViews = [];
    private IReadOnlyList<string> discoveredModels = [];
    private string sessionId;
    private int turnNumber;
    private bool toolCallsCollapsed;
    private string? lastAnswer;
    private bool needsConversationViewport;
    private int conversationScrollOffset;
    private int maximumConversationScrollOffset;
    private static uint? originalConsoleInputMode;

    public AegisTui(
        AgentSettings settings,
        ProviderRegistry providerRegistry,
        CodingWorkspace workspace,
        MafCodingAgentService? mafService,
        ChatGptOAuthCredentialStore? oauth)
    {
        this.settings = settings;
        this.providerRegistry = providerRegistry;
        this.workspace = workspace;
        this.mafService = mafService;
        this.oauth = oauth;
        historyStore = new ConversationHistoryStore();
        sessionId = historyStore.GetLatestSessionId(workspace.RootPath, settings.ProviderName)
            ?? Guid.NewGuid().ToString("N");
        LoadCurrentHistory();
    }

    public async Task RunAsync()
    {
        try
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // Some redirected or legacy hosts do not allow changing the console code page.
        }

        bool mouseReportingEnabled = EnableMouseReporting();
        try
        {
            TryClear();
            RenderWelcome();
            await RestoreSelectedAgentSessionAsync();

            while (true)
            {
                if (needsConversationViewport)
                {
                    // Tool output can be much taller than the terminal. Redraw it
                    // into a bounded viewport before accepting the next command.
                    // This also keeps mouse coordinates relative to the visible screen.
                    needsConversationViewport = false;
                    RenderConversationViewport();
                }
                else
                {
                    RenderDashboard();
                }

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
        finally
        {
            if (mouseReportingEnabled)
            {
                DisableMouseReporting();
            }
        }
    }

    private async Task RunTurnAsync(string input)
    {
        turnNumber++;
        history.Add(new ChatEntry("user", input));
        historyStore.Append(workspace.RootPath, settings.ProviderName, settings.Model, sessionId, "user", input);
        activeToolCalls.Clear();
        toolCallViews.Clear();
        lastAnswer = null;
        conversationScrollOffset = 0;
        maximumConversationScrollOffset = 0;
        AnsiConsole.Write(new Rule($"[deepskyblue1]Turn {turnNumber}[/]").LeftJustified());
        AnsiConsole.MarkupLine("[grey]依頼を処理中です。Tool Call は下に表示されます。[/]");

        try
        {
            string answer;
            MafCodingAgentService? service = mafService;
            if (service is not null)
            {
                answer = await service.RunAsync(input, HandleAgentUpdateAsync);
            }
            else
            {
                RenderPanel("Provider setup", "No active provider is configured. Use /provider add or /auth openai.", Color.Yellow);
                return;
            }

            history.Add(new ChatEntry("assistant", answer));
            historyStore.Append(workspace.RootPath, settings.ProviderName, settings.Model, sessionId, "assistant", answer);
            lastAnswer = answer;
            try
            {
                JsonElement? snapshot = await service.SerializeSessionAsync();
                if (snapshot.HasValue)
                {
                    historyStore.SaveSessionSnapshot(workspace.RootPath, settings.ProviderName, sessionId, snapshot.Value);
                }
            }
            catch (Exception exception)
            {
                AnsiConsole.MarkupLine($"[yellow]Agent セッションの履歴保存をスキップしました: {Markup.Escape(exception.Message)}[/]");
            }

            RenderPanel("回答", string.IsNullOrWhiteSpace(answer) ? "(テキスト応答なし)" : answer, Color.Green);
            needsConversationViewport = true;
        }
        catch (Exception exception)
        {
            RenderPanel("エラー", exception.Message, Color.Red);
            needsConversationViewport = true;
        }
    }

    private Task HandleAgentUpdateAsync(AgentResponseUpdate update)
    {
        foreach (AIContent content in update.Contents)
        {
            switch (content)
            {
                case FunctionCallContent functionCall:
                    RenderToolCall(functionCall);
                    break;
                case FunctionResultContent functionResult:
                    RenderToolResult(functionResult);
                    break;
                case WebSearchToolCallContent webSearchCall:
                    RenderWebSearchCall(webSearchCall);
                    break;
                case WebSearchToolResultContent webSearchResult:
                    RenderWebSearchResult(webSearchResult);
                    break;
            }
        }

        return Task.CompletedTask;
    }

    private void LoadCurrentHistory()
    {
        history.Clear();
        IReadOnlyList<ConversationHistoryEntry> entries = historyStore.LoadRecent(
            workspace.RootPath,
            settings.ProviderName,
            maximumEntries: 200,
            sessionId: sessionId);
        history.AddRange(entries.Select(entry => new ChatEntry(entry.Role, entry.Text)));
        turnNumber = entries.Count(entry => entry.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
    }

    private async Task RestoreSelectedAgentSessionAsync()
    {
        if (mafService is null)
        {
            return;
        }

        JsonElement? snapshot = historyStore.LoadSessionSnapshot(
            workspace.RootPath,
            settings.ProviderName,
            sessionId);
        if (!snapshot.HasValue)
        {
            mafService.RestoreTextHistory(ToChatMessages(history));
            return;
        }

        try
        {
            await mafService.RestoreSessionAsync(snapshot.Value);
        }
        catch (Exception exception)
        {
            RenderPanel("履歴", $"保存済み Agent セッションを復元できませんでした。新しいセッションで開始します。\n{exception.Message}", Color.Yellow);
            await mafService.ResetSessionAsync();
        }
    }

    private async Task StartNewHistoryAsync()
    {
        sessionId = Guid.NewGuid().ToString("N");
        history.Clear();
        turnNumber = 0;
        activeToolCalls.Clear();
        toolCallViews.Clear();
        lastAnswer = null;
        if (mafService is not null)
        {
            await mafService.ResetSessionAsync();
        }

        RenderPanel("履歴", "新しい履歴を開始しました。最初の依頼が履歴名になります。", Color.Green);
    }

    private async Task UseHistoryAsync(string requestedId)
    {
        string? resolvedId = historyStore.ResolveSessionId(workspace.RootPath, settings.ProviderName, requestedId);
        if (resolvedId is null)
        {
            RenderPanel("履歴", "履歴 ID が見つからないか、候補が複数あります。/history list で確認してください。", Color.Red);
            return;
        }

        sessionId = resolvedId;
        LoadCurrentHistory();
        activeToolCalls.Clear();
        toolCallViews.Clear();
        lastAnswer = null;
        if (mafService is not null)
        {
            JsonElement? snapshot = historyStore.LoadSessionSnapshot(workspace.RootPath, settings.ProviderName, sessionId);
            if (snapshot.HasValue)
            {
                try
                {
                    await mafService.RestoreSessionAsync(snapshot.Value);
                }
                catch (Exception exception)
                {
                    await mafService.ResetSessionAsync();
                    RenderPanel("履歴", $"Agent セッションを復元できなかったため、新しい Agent セッションで表示履歴を開きました。\n{exception.Message}", Color.Yellow);
                }
            }
            else
            {
                await mafService.ResetSessionAsync();
                mafService.RestoreTextHistory(ToChatMessages(history));
            }
        }

        ConversationHistorySession? session = historyStore.ListSessions(workspace.RootPath, settings.ProviderName)
            .FirstOrDefault(item => item.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase));
        RenderPanel("履歴", $"履歴を切り替えました。\n{session?.Title ?? sessionId[..Math.Min(8, sessionId.Length)]}", Color.Green);
    }

    private async Task DeleteHistoryAsync(string requestedId)
    {
        string? resolvedId = historyStore.ResolveSessionId(workspace.RootPath, settings.ProviderName, requestedId);
        if (resolvedId is null)
        {
            RenderPanel("履歴", "履歴 ID が見つからないか、候補が複数あります。/history list で確認してください。", Color.Red);
            return;
        }

        bool deleted = historyStore.DeleteSession(workspace.RootPath, settings.ProviderName, resolvedId);
        if (resolvedId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
        {
            sessionId = historyStore.GetLatestSessionId(workspace.RootPath, settings.ProviderName)
                ?? Guid.NewGuid().ToString("N");
            LoadCurrentHistory();
            toolCallViews.Clear();
            lastAnswer = null;
            if (mafService is not null)
            {
                await mafService.ResetSessionAsync();
            }
        }

        RenderPanel("履歴", deleted ? "履歴を削除しました。" : "削除する履歴がありません。", deleted ? Color.Green : Color.Yellow);
    }

    private void RenderToolCall(FunctionCallContent functionCall)
    {
        string callId = string.IsNullOrWhiteSpace(functionCall.CallId) ? "(idなし)" : functionCall.CallId;
        string name = string.IsNullOrWhiteSpace(functionCall.Name) ? "unknown_tool" : functionCall.Name;
        string arguments = functionCall.Arguments is null || functionCall.Arguments.Count == 0
            ? "(引数なし)"
            : JsonSerializer.Serialize(functionCall.Arguments, new JsonSerializerOptions { WriteIndented = true });
        ToolCallView view = GetOrCreateToolCallView(callId, name);
        view.Arguments = arguments;
        RenderToolCallPanel(view);
    }

    private void RenderToolResult(FunctionResultContent functionResult)
    {
        string callId = string.IsNullOrWhiteSpace(functionResult.CallId) ? "(idなし)" : functionResult.CallId;
        ToolCallView view = GetOrCreateToolCallView(callId, "tool");
        string result = functionResult.Exception is null
            ? functionResult.Result?.ToString() ?? "(結果なし)"
            : $"{functionResult.Exception.GetType().Name}: {functionResult.Exception.Message}";
        view.Result = result;
        view.HasResult = true;
        view.Succeeded = functionResult.Exception is null;
        RenderToolResultPanel(view);
    }

    private void RenderWebSearchCall(WebSearchToolCallContent webSearchCall)
    {
        string callId = string.IsNullOrWhiteSpace(webSearchCall.CallId) ? "(idなし)" : webSearchCall.CallId;
        ToolCallView view = GetOrCreateToolCallView(callId, "WebSearch");
        if (webSearchCall.Queries is not { Count: > 0 })
        {
            return;
        }

        view.Arguments = string.Join(Environment.NewLine, webSearchCall.Queries);
        RenderToolCallPanel(view);
    }

    private void RenderWebSearchResult(WebSearchToolResultContent webSearchResult)
    {
        string callId = string.IsNullOrWhiteSpace(webSearchResult.CallId) ? "(idなし)" : webSearchResult.CallId;
        ToolCallView view = GetOrCreateToolCallView(callId, "WebSearch");
        view.Result = webSearchResult.Outputs is { Count: > 0 }
            ? string.Join(
                Environment.NewLine,
                webSearchResult.Outputs.Select(output => output is UriContent uri
                    ? uri.Uri.ToString()
                    : output.ToString()))
            : "(検索結果なし)";
        view.HasResult = true;
        view.Succeeded = true;
        RenderToolResultPanel(view);
    }

    private ToolCallView GetOrCreateToolCallView(string callId, string name)
    {
        if (activeToolCalls.TryGetValue(callId, out ToolCallView? existing))
        {
            return existing;
        }

        ToolCallView view = new(callId, name);
        activeToolCalls[callId] = view;
        toolCallViews.Add(view);
        return view;
    }

    private void ResetToolCallDisplayOverrides()
    {
        foreach (ToolCallView view in toolCallViews)
        {
            view.ExpandedOverride = null;
        }
    }

    private void RenderToolCallPanel(ToolCallView view)
    {
        view.CallStartRow = CurrentConsoleRow();
        if (!IsToolCallExpanded(view))
        {
            string summary = $"▶ {view.Name}\nCall ID: {view.CallId}\n引数: 折りたたみ中（クリックで展開）";
            AnsiConsole.Write(new Panel(new Text(summary))
                .Header($"[cyan]Tool Call · collapsed[/] [bold]{Markup.Escape(view.Name)}[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Cyan1));
        }
        else
        {
            string body = $"Tool: {view.Name}\nCall ID: {view.CallId}\n\nArguments:\n{Truncate(view.Arguments, 5_000)}";
            AnsiConsole.Write(new Panel(new Text(body))
                .Header($"[cyan]Tool Call[/] [bold]{Markup.Escape(view.Name)}[/] [grey](クリックで折りたたみ)[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Cyan1));
        }

        view.CallEndRow = CurrentConsoleRow();
    }

    private void RenderToolResultPanel(ToolCallView view)
    {
        Color color = view.Succeeded ? Color.Green : Color.Red;
        view.ResultStartRow = CurrentConsoleRow();

        if (!IsToolCallExpanded(view))
        {
            string status = view.Succeeded ? "完了" : "失敗";
            string summary = $"{(view.Succeeded ? "✓" : "✗")} {view.Name}\nCall ID: {view.CallId}\n結果: {status}（クリックで展開）";
            AnsiConsole.Write(new Panel(new Text(summary))
                .Header($"[{color.ToMarkup()}]Tool Result · collapsed[/] [bold]{Markup.Escape(view.Name)}[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(color));
        }
        else
        {
            string body = $"Tool: {view.Name}\nCall ID: {view.CallId}\n\n{Truncate(view.Result ?? "(結果なし)", 5_000)}";
            AnsiConsole.Write(new Panel(new Text(body))
                .Header($"[{color.ToMarkup()}]Tool Result[/] [bold]{Markup.Escape(view.Name)}[/] [grey](クリックで折りたたみ)[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(color));
        }

        view.ResultEndRow = CurrentConsoleRow();
    }

    private bool IsToolCallExpanded(ToolCallView view) =>
        view.ExpandedOverride ?? !toolCallsCollapsed;

    private void RenderToolCallViews()
    {
        foreach (ToolCallView view in toolCallViews)
        {
            RenderToolCallPanel(view);
            if (view.HasResult)
            {
                RenderToolResultPanel(view);
            }
        }
    }

    private void RenderConversationViewport()
    {
        TryClear();
        RenderCompactHeader();

        foreach (ToolCallView view in toolCallViews)
        {
            view.VisibleStartRow = -1;
            view.VisibleEndRow = -1;
        }

        IReadOnlyList<ViewportLine> lines = BuildConversationLines();
        int contentStartRow = CurrentConsoleRow();
        int availableRows = Math.Max(1, GetConsoleHeight() - contentStartRow - 3);
        maximumConversationScrollOffset = Math.Max(0, lines.Count - availableRows);
        conversationScrollOffset = Math.Clamp(
            conversationScrollOffset,
            0,
            maximumConversationScrollOffset);

        int firstLine = Math.Max(0, lines.Count - availableRows - conversationScrollOffset);
        int lastLine = Math.Min(lines.Count, firstLine + availableRows);
        for (int index = firstLine; index < lastLine; index++)
        {
            ViewportLine line = lines[index];
            int visibleRow = contentStartRow + (index - firstLine);
            if (line.View is not null)
            {
                line.View.VisibleStartRow = line.View.VisibleStartRow > 0
                    ? Math.Min(line.View.VisibleStartRow, visibleRow)
                    : visibleRow;
                line.View.VisibleEndRow = visibleRow;
            }

            Console.Write(line.Text);
            Console.Write("\r\n");
        }

        string scrollHint = maximumConversationScrollOffset == 0
            ? "Tool Call / Result はクリックで個別に展開・折りたたみできます。"
            : $"↑/↓ または PageUp/PageDown でスクロール · {conversationScrollOffset}/{maximumConversationScrollOffset} · クリックでTool Callを開閉";
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(scrollHint)}[/]");
    }

    private void RenderCompactHeader()
    {
        ProviderProfile? profile = providerRegistry.Resolve();
        string provider = profile?.Name ?? settings.ProviderName;
        AnsiConsole.MarkupLine(
            $"[deepskyblue1][bold]AEGIS[/][/] [grey]Turn {turnNumber} · {Markup.Escape(provider)} · {Markup.Escape(settings.Model)}[/]");
    }

    private IReadOnlyList<ViewportLine> BuildConversationLines()
    {
        List<ViewportLine> lines = [];

        // Keep the transcript in chronological order. The current turn's
        // assistant message is rendered below its Tool Calls so it is not
        // duplicated when it is already present in the persisted history.
        IEnumerable<ChatEntry> transcript = history;
        if (lastAnswer is not null &&
            history.LastOrDefault()?.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) == true &&
            string.Equals(history[^1].Text, lastAnswer, StringComparison.Ordinal))
        {
            transcript = history.Take(history.Count - 1);
        }

        foreach (ChatEntry entry in transcript)
        {
            string title = entry.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                ? "User"
                : "Assistant";
            AddViewportBox(lines, title, entry.Text, view: null);
        }

        foreach (ToolCallView view in toolCallViews)
        {
            string callBody = IsToolCallExpanded(view)
                ? $"Tool: {view.Name}\nCall ID: {view.CallId}\n\nArguments:\n{view.Arguments}"
                : $"▶ {view.Name}\nCall ID: {view.CallId}\n引数: 折りたたみ中（クリックで展開）";
            AddViewportBox(lines, $"Tool Call · {view.Name}", callBody, view);

            if (view.HasResult)
            {
                string status = view.Succeeded ? "完了" : "失敗";
                string resultBody = IsToolCallExpanded(view)
                    ? $"Tool: {view.Name}\nCall ID: {view.CallId}\n状態: {status}\n\n{view.Result ?? "(結果なし)"}"
                    : $"{(view.Succeeded ? "✓" : "✗")} {view.Name}\nCall ID: {view.CallId}\n結果: {status}（クリックで展開）";
                AddViewportBox(lines, $"Tool Result · {view.Name}", resultBody, view);
            }
        }

        if (lastAnswer is not null)
        {
            AddViewportBox(
                lines,
                "回答",
                string.IsNullOrWhiteSpace(lastAnswer) ? "(テキスト応答なし)" : lastAnswer,
                view: null);
        }

        if (lines.Count == 0)
        {
            lines.Add(new ViewportLine("(会話はまだありません)", null));
        }

        return lines;
    }

    private static void AddViewportBox(
        ICollection<ViewportLine> destination,
        string title,
        string body,
        ToolCallView? view)
    {
        // Leave one column empty so the terminal does not auto-wrap the right
        // border onto an extra row and invalidate mouse hit testing.
        int width = Math.Max(20, Math.Min(Math.Max(20, GetConsoleWidth() - 1), 120));
        int contentWidth = width - 4;
        string safeTitle = ClipViewportText($" {title} ", width - 3);
        destination.Add(new ViewportLine(
            $"┌─{safeTitle}{new string('─', Math.Max(0, width - 3 - safeTitle.Length))}┐",
            view));

        foreach (string rawLine in WrapViewportText(body, contentWidth))
        {
            string content = ClipViewportText(rawLine, contentWidth);
            destination.Add(new ViewportLine(
                $"│ {content.PadRight(contentWidth)} │",
                view));
        }

        destination.Add(new ViewportLine($"└{new string('─', width - 2)}┘", view));
    }

    private static IReadOnlyList<string> WrapViewportText(string text, int width)
    {
        List<string> lines = [];
        string normalized = text.Replace('\r', ' ').Replace('\t', ' ');
        foreach (string rawLine in normalized.Split('\n'))
        {
            if (rawLine.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            for (int offset = 0; offset < rawLine.Length; offset += width)
            {
                lines.Add(rawLine.Substring(offset, Math.Min(width, rawLine.Length - offset)));
            }
        }

        return lines;
    }

    private static string ClipViewportText(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private static int GetConsoleWidth()
    {
        try
        {
            return Math.Max(20, Console.WindowWidth);
        }
        catch (IOException)
        {
            return 80;
        }
    }

    private static int GetConsoleHeight()
    {
        try
        {
            return Math.Max(8, Console.WindowHeight);
        }
        catch (IOException)
        {
            return 24;
        }
    }

    private async Task<bool> HandleCommandAsync(string input)
    {
        if (input.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            RenderHelp();
            return true;
        }

        if (input.Equals("/toolcalls", StringComparison.OrdinalIgnoreCase))
        {
            RenderToolCallMode();
            return true;
        }

        if (input.Equals("/toolcalls collapse", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("/tools collapse", StringComparison.OrdinalIgnoreCase))
        {
            toolCallsCollapsed = true;
            ResetToolCallDisplayOverrides();
            RenderPanel("Tool Call", "以降の Tool Call / Result を折りたたんで表示します。/toolcalls expand で展開表示に戻せます。", Color.Cyan1);
            return true;
        }

        if (input.Equals("/toolcalls expand", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("/tools expand", StringComparison.OrdinalIgnoreCase))
        {
            toolCallsCollapsed = false;
            ResetToolCallDisplayOverrides();
            RenderPanel("Tool Call", "以降の Tool Call / Result を詳細表示します。", Color.Cyan1);
            return true;
        }

        if (input.Equals("/toolcalls toggle", StringComparison.OrdinalIgnoreCase))
        {
            toolCallsCollapsed = !toolCallsCollapsed;
            RenderToolCallMode();
            return true;
        }

        if (input.Equals("/tools", StringComparison.OrdinalIgnoreCase))
        {
            RenderTools();
            return true;
        }

        if (input.Equals("/clear", StringComparison.OrdinalIgnoreCase))
        {
            if (mafService is not null)
            {
                await mafService.ResetSessionAsync();
            }

            sessionId = Guid.NewGuid().ToString("N");
            history.Clear();
            turnNumber = 0;
            activeToolCalls.Clear();
            toolCallViews.Clear();
            lastAnswer = null;
            TryClear();
            RenderWelcome();
            RenderPanel("履歴", "新しい履歴を開始しました。以前の履歴は /history list から選べます。", Color.Green);
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

        if (input.Equals("/history", StringComparison.OrdinalIgnoreCase))
        {
            RenderHistory();
            return true;
        }

        if (input.Equals("/history list", StringComparison.OrdinalIgnoreCase))
        {
            RenderHistorySessions();
            return true;
        }

        if (input.Equals("/history new", StringComparison.OrdinalIgnoreCase))
        {
            await StartNewHistoryAsync();
            return true;
        }

        if (input.Equals("/history use", StringComparison.OrdinalIgnoreCase))
        {
            await SelectHistoryAsync();
            return true;
        }

        if (input.Equals("/history clear", StringComparison.OrdinalIgnoreCase))
        {
            bool cleared = historyStore.Clear(workspace.RootPath, settings.ProviderName);
            sessionId = Guid.NewGuid().ToString("N");
            history.Clear();
            turnNumber = 0;
            activeToolCalls.Clear();
            toolCallViews.Clear();
            lastAnswer = null;
            if (mafService is not null)
            {
                await mafService.ResetSessionAsync();
            }

            RenderPanel("履歴", cleared ? "この workspace の全履歴を削除し、新しい履歴を開始しました。" : "削除する保存履歴はありません。新しい履歴を開始しました。", Color.Green);
            return true;
        }

        if (input.StartsWith("/history use ", StringComparison.OrdinalIgnoreCase))
        {
            await UseHistoryAsync(input["/history use ".Length..].Trim());
            return true;
        }

        if (input.StartsWith("/history delete ", StringComparison.OrdinalIgnoreCase))
        {
            await DeleteHistoryAsync(input["/history delete ".Length..].Trim());
            return true;
        }

        if (input.Equals("/model", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("/model list", StringComparison.OrdinalIgnoreCase))
        {
            await RenderModelsAsync();
            return true;
        }

        if (input.StartsWith("/model ", StringComparison.OrdinalIgnoreCase))
        {
            string modelCommand = input["/model ".Length..].Trim();
            string model = modelCommand.StartsWith("use ", StringComparison.OrdinalIgnoreCase)
                ? modelCommand["use ".Length..].Trim()
                : modelCommand;
            if (string.IsNullOrWhiteSpace(model))
            {
                RenderPanel("model", "Usage: /model list | /model use <model> | /model <model>", Color.Yellow);
            }
            else
            {
                await ChangeModelAsync(model);
            }

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

    private async Task SelectHistoryAsync()
    {
        IReadOnlyList<ConversationHistorySession> sessions = historyStore.ListSessions(
            workspace.RootPath,
            settings.ProviderName);
        if (sessions.Count == 0)
        {
            RenderPanel("履歴", "選択できる履歴がありません。先に会話を実行してください。", Color.Yellow);
            return;
        }

        if (Console.IsInputRedirected)
        {
            RenderHistorySessions();
            RenderPanel("履歴", "/history use <id> で履歴 ID を指定してください。", Color.Yellow);
            return;
        }

        Dictionary<string, string> choiceToSessionId = new(StringComparer.Ordinal);
        foreach (ConversationHistorySession session in sessions)
        {
            string shortId = session.SessionId[..Math.Min(8, session.SessionId.Length)];
            string title = session.Title
                .Replace('[', '（')
                .Replace(']', '）')
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            string choice = $"{(session.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase) ? "*" : " ")} {shortId}  {title}";
            choiceToSessionId[choice] = session.SessionId;
        }

        string selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]開く履歴を選択してください[/]")
                .PageSize(Math.Max(3, Math.Min(12, choiceToSessionId.Count)))
                .MoreChoicesText("[grey]上下キーで移動、Enter で決定[/]")
                .AddChoices(choiceToSessionId.Keys));
        await UseHistoryAsync(choiceToSessionId[selected]);
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
                .AddChoices(
                    "OpenAI API",
                    "DeepSeek",
                    "Anthropic Claude",
                    "Ollama (ローカル)",
                    "llama.cpp (ローカル)",
                    "vLLM (ローカル)",
                    "LM Studio (ローカル)",
                    "OpenRouter (OSSモデル)",
                    "OpenAI-compatible",
                    "OpenAI ChatGPT OAuth (OpenCode-style PKCE)"));
        string name = AnsiConsole.Ask<string>("プロファイル名:");

        if (type.StartsWith("OpenAI ChatGPT", StringComparison.Ordinal))
        {
            providerRegistry.Upsert(name, ProviderKinds.ChatGptOAuth, ChatGptOAuthCredentialStore.DefaultModel, null, null, null);
            providerRegistry.SetActive(name);
            RenderPanel("provider", "OAuth プロファイルを保存しました。/auth openai でログインしてください。", Color.Green);
            return;
        }

        string kind = type switch
        {
            "OpenAI API" => ProviderKinds.OpenAi,
            "DeepSeek" => ProviderKinds.DeepSeek,
            "Anthropic Claude" => ProviderKinds.Anthropic,
            "Ollama (ローカル)" => ProviderKinds.Ollama,
            "llama.cpp (ローカル)" => ProviderKinds.LlamaCpp,
            "vLLM (ローカル)" => ProviderKinds.Vllm,
            "LM Studio (ローカル)" => ProviderKinds.LmStudio,
            "OpenRouter (OSSモデル)" => ProviderKinds.OpenRouter,
            _ => ProviderKinds.OpenAiCompatible,
        };
        string defaultModel = type switch
        {
            _ when kind == ProviderKinds.DeepSeek => ProviderKinds.DefaultModel(kind),
            _ when kind == ProviderKinds.Anthropic => ProviderKinds.DefaultModel(kind),
            _ when ProviderKinds.IsLocal(kind) => ProviderKinds.DefaultModel(kind),
            _ when kind == ProviderKinds.OpenRouter => ProviderKinds.DefaultModel(kind),
            _ => ProviderKinds.DefaultModel(ProviderKinds.OpenAi),
        };
        string defaultBaseUrl = kind switch
        {
            ProviderKinds.OpenAi => "",
            _ => ProviderKinds.DefaultBaseUrl(kind) ?? "http://localhost:8000/v1",
        };
        string model = AnsiConsole.Ask("モデル名:", defaultModel);
        string baseUrl = AnsiConsole.Ask("Base URL（既定値のまま Enter で利用）:", defaultBaseUrl);
        bool localProvider = ProviderKinds.IsLocal(kind);
        string apiKeyEnv = localProvider
            ? string.Empty
            : AnsiConsole.Ask("API キーを読む環境変数名（空欄なら暗号化保存）:", kind == ProviderKinds.Anthropic ? "ANTHROPIC_API_KEY" : "");
        string? apiKey = localProvider || !string.IsNullOrWhiteSpace(apiKeyEnv)
            ? null
            : AnsiConsole.Prompt(new TextPrompt<string>("API キー:").Secret());

        try
        {
            providerRegistry.Upsert(name, kind, model, baseUrl, apiKey, apiKeyEnv);
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
        if (oauth is null)
        {
            RenderPanel("OpenAI OAuth", "ChatGPT OAuth プロファイルを選択してから実行してください。", Color.Yellow);
            return;
        }

        try
        {
            bool success = await oauth.LoginAsync();
            if (success)
            {
                ProviderProfile? active = providerRegistry.Resolve();
                string providerName = active?.Kind.Equals("openai-chatgpt-oauth", StringComparison.OrdinalIgnoreCase) == true
                    ? active.Name
                    : "openai-chatgpt";
                string model = active?.Kind.Equals("openai-chatgpt-oauth", StringComparison.OrdinalIgnoreCase) == true
                    ? active.Model
                    : ChatGptOAuthCredentialStore.DefaultModel;
                providerRegistry.Upsert(providerName, "openai-chatgpt-oauth", model, null, null, null);
                providerRegistry.SetActive(providerName);
                ProviderProfile profile = providerRegistry.Resolve(providerName)!;
                settings = AgentSettings.Load(settings.Model, null, profile);
                mafService = await MafCodingAgentService.CreateAsync(settings, workspace, oauth);
                await mafService.InitializeAsync();
            }

            RenderPanel("OpenAI OAuth", success ? "ChatGPT OAuth に成功しました。Codex CLI なしで接続します。" : "OAuth に失敗しました。", success ? Color.Green : Color.Red);
        }
        catch (Exception exception)
        {
            RenderPanel("OpenAI OAuth", exception.Message, Color.Red);
        }
    }

    private async Task ChangeModelAsync(string model)
    {
        ProviderProfile? profile = providerRegistry.Resolve();
        if (profile is not null)
        {
            providerRegistry.SetModel(profile.Name, model);
        }

        settings = settings with { Model = model };
        try
        {
            if (settings.BackendKind.Equals("maf-chatgpt-oauth", StringComparison.OrdinalIgnoreCase))
            {
                mafService = oauth?.HasStoredCredential == true
                    ? await MafCodingAgentService.CreateAsync(settings, workspace, oauth)
                    : null;
            }
            else if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                mafService = MafCodingAgentService.Create(settings, workspace);
            }

            if (mafService is not null)
            {
                await mafService.InitializeAsync();
            }

            string persistence = profile is null ? "現在のセッションのみ" : "保存済みプロファイルにも反映";
            RenderPanel("model", $"モデルを {model} に変更しました（{persistence}）。", Color.Green);
        }
        catch (Exception exception)
        {
            RenderPanel("model", exception.Message, Color.Red);
        }
    }

    private async Task RenderModelsAsync()
    {
        ProviderProfile? profile = providerRegistry.Resolve();
        try
        {
            IReadOnlyList<string> discovered = await ModelCatalog.DiscoverAsync(settings);
            if (discovered.Count > 0)
            {
                discoveredModels = discovered;
                AnsiConsole.MarkupLine($"[grey]{discovered.Count} モデルを endpoint から取得しました。[/]");
            }
        }
        catch (Exception exception)
        {
            AnsiConsole.MarkupLine($"[yellow]モデル一覧の取得に失敗しました: {Markup.Escape(exception.Message)}[/]");
        }

        IReadOnlyList<string> models = ModelCatalog.GetSuggestions(profile, settings.Model, discoveredModels);
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Active")
            .AddColumn("Model")
            .AddColumn("操作");

        foreach (string model in models)
        {
            table.AddRow(
                model.Equals(settings.Model, StringComparison.OrdinalIgnoreCase) ? "*" : "",
                Markup.Escape(model),
                Markup.Escape($"/model use {model}"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]候補外のモデルも /model use <model> で指定できます。[/]");
    }

    private void RenderHistory()
    {
        IReadOnlyList<ConversationHistoryEntry> entries = historyStore.LoadRecent(
            workspace.RootPath,
            settings.ProviderName,
            maximumEntries: 40,
            sessionId: sessionId);
        if (entries.Count == 0)
        {
            RenderPanel("履歴", "この履歴には保存されたメッセージがありません。/history list で履歴一覧を確認できます。", Color.Yellow);
            return;
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("時刻")
            .AddColumn("Role")
            .AddColumn("Model")
            .AddColumn("内容");
        foreach (ConversationHistoryEntry entry in entries)
        {
            string text = entry.Text.Replace('\n', ' ');
            text = text[..Math.Min(text.Length, 100)];
            table.AddRow(
                entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                Markup.Escape(entry.Role),
                Markup.Escape(entry.Model),
                Markup.Escape(text));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]/history list: 一覧    /history new: 新規    /history use <id>: 切替[/]");
    }

    private void RenderHistorySessions()
    {
        IReadOnlyList<ConversationHistorySession> sessions = historyStore.ListSessions(
            workspace.RootPath,
            settings.ProviderName);
        if (sessions.Count == 0)
        {
            RenderPanel("履歴一覧", "この workspace・provider に保存された履歴はありません。/history new で新規作成できます。", Color.Yellow);
            return;
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Active")
            .AddColumn("ID")
            .AddColumn("最終更新")
            .AddColumn("件数")
            .AddColumn("Model")
            .AddColumn("履歴名");
        foreach (ConversationHistorySession session in sessions)
        {
            table.AddRow(
                session.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase) ? "*" : "",
                Markup.Escape(session.SessionId[..Math.Min(8, session.SessionId.Length)]),
                session.LastActivity.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                session.MessageCount.ToString(),
                Markup.Escape(session.Model),
                Markup.Escape(session.Title));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]/history use <ID>: 開く    /history new: 新規    /history delete <ID>: 削除    /history clear: 全削除[/]");
    }

    private IReadOnlyList<string> GetInputSuggestions(string input)
    {
        if (!input.StartsWith("/", StringComparison.Ordinal))
        {
            return [];
        }

        if (input.StartsWith("/model use ", StringComparison.OrdinalIgnoreCase))
        {
            string prefix = input["/model use ".Length..];
            return GetModelSuggestions()
                .Where(model => model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(model => $"/model use {model}")
                .Take(8)
                .ToArray();
        }

        string? historyCommand = input.StartsWith("/history use ", StringComparison.OrdinalIgnoreCase)
            ? "/history use "
            : input.StartsWith("/history delete ", StringComparison.OrdinalIgnoreCase)
                ? "/history delete "
                : null;
        if (historyCommand is not null)
        {
            string prefix = input[historyCommand.Length..].Trim();
            return historyStore.ListSessions(workspace.RootPath, settings.ProviderName)
                .Select(session => session.SessionId)
                .Where(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(id => historyCommand + id)
                .Take(8)
                .ToArray();
        }

        string[] commands =
        [
            "/help",
            "/toolcalls",
            "/toolcalls collapse",
            "/toolcalls expand",
            "/tools",
            "/model list",
            "/model use ",
            "/provider list",
            "/provider add",
            "/provider use ",
            "/auth openai",
            "/history",
            "/history list",
            "/history new",
            "/history use ",
            "/history delete ",
            "/history clear",
            "/status",
            "/workspace",
            "/clear",
            "/exit",
        ];

        return commands
            .Where(command => command.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToArray();
    }

    private IReadOnlyList<string> GetModelSuggestions()
    {
        return ModelCatalog.GetSuggestions(providerRegistry.Resolve(), settings.Model, discoveredModels);
    }

    private string ReadInput()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? "/exit";
        }

        const string prompt = "aegis> ";
        StringBuilder buffer = new();
        StringBuilder? pendingEscapeSequence = null;
        int selectedSuggestion = -1;
        int renderedSuggestionLines = 0;
        bool previousTreatControlCAsInput = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;

        try
        {
            while (true)
            {
                IReadOnlyList<string> suggestions = GetInputSuggestions(buffer.ToString());
                if (selectedSuggestion >= suggestions.Count)
                {
                    selectedSuggestion = suggestions.Count - 1;
                }

                renderedSuggestionLines = RenderInputLine(
                    prompt,
                    buffer.ToString(),
                    suggestions,
                    selectedSuggestion,
                    renderedSuggestionLines);
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                if (pendingEscapeSequence is not null)
                {
                    EscapeSequenceState sequenceState = ConsumeEscapeSequence(
                        pendingEscapeSequence,
                        key,
                        out EscapeAction escapeAction);
                    if (sequenceState == EscapeSequenceState.Pending)
                    {
                        continue;
                    }

                    pendingEscapeSequence = null;
                    if (sequenceState == EscapeSequenceState.Complete)
                    {
                        if (escapeAction.MouseEvent.HasValue)
                        {
                            MouseEvent mouseEvent = escapeAction.MouseEvent.Value;
                            if (mouseEvent.IsWheel)
                            {
                                ScrollConversation(
                                    mouseEvent.WheelDirection > 0 ? 3 : -3,
                                    prompt,
                                    buffer.ToString(),
                                    suggestions,
                                    selectedSuggestion,
                                    ref renderedSuggestionLines);
                            }
                            else if (mouseEvent.IsLeftClick)
                            {
                                ToggleToolCallAtRow(
                                    mouseEvent.Row,
                                    prompt,
                                    buffer.ToString(),
                                    suggestions,
                                    selectedSuggestion,
                                    ref renderedSuggestionLines);
                            }
                        }
                        else
                        {
                            HandleEscapeAction(
                                escapeAction.Kind,
                                prompt,
                                buffer.ToString(),
                                suggestions,
                                ref selectedSuggestion,
                                ref renderedSuggestionLines);
                        }

                        continue;
                    }

                    // An unrecognized escape sequence behaves like Escape.
                    buffer.Clear();
                    selectedSuggestion = -1;
                    continue;
                }

                if (key.Key == ConsoleKey.Escape || key.KeyChar == '\u001b')
                {
                    pendingEscapeSequence = new StringBuilder();
                    continue;
                }

                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    throw new OperationCanceledException();
                }

                if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n')
                {
                    string submittedInput = buffer.ToString();
                    if (selectedSuggestion >= 0 && selectedSuggestion < suggestions.Count)
                    {
                        string selectedCommand = suggestions[selectedSuggestion];
                        if (selectedCommand.EndsWith(' '))
                        {
                            buffer.Clear();
                            buffer.Append(selectedCommand);
                            selectedSuggestion = -1;
                            continue;
                        }

                        submittedInput = selectedCommand;
                    }

                    RenderInputLine(prompt, submittedInput, [], -1, renderedSuggestionLines);
                    Console.WriteLine();
                    return submittedInput;
                }

                if (key.Key is ConsoleKey.PageUp or ConsoleKey.PageDown or ConsoleKey.Home or ConsoleKey.End)
                {
                    int page = Math.Max(1, GetConsoleHeight() / 2);
                    int delta = key.Key switch
                    {
                        ConsoleKey.PageUp => page,
                        ConsoleKey.PageDown => -page,
                        ConsoleKey.Home => int.MaxValue,
                        ConsoleKey.End => int.MinValue,
                        _ => 0,
                    };
                    ScrollConversation(
                        delta,
                        prompt,
                        buffer.ToString(),
                        suggestions,
                        selectedSuggestion,
                        ref renderedSuggestionLines);
                    continue;
                }

                if (key.Key == ConsoleKey.Backspace || key.KeyChar is '\b' or '\u007f')
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                    }

                    selectedSuggestion = -1;
                    continue;
                }

                if (key.Key == ConsoleKey.UpArrow && suggestions.Count > 0)
                {
                    selectedSuggestion = selectedSuggestion <= 0 ? suggestions.Count - 1 : selectedSuggestion - 1;
                    continue;
                }

                if (key.Key == ConsoleKey.DownArrow && suggestions.Count > 0)
                {
                    selectedSuggestion = selectedSuggestion >= suggestions.Count - 1 ? 0 : selectedSuggestion + 1;
                    continue;
                }

                if (suggestions.Count == 0 &&
                    key.Key is (ConsoleKey.UpArrow or ConsoleKey.DownArrow))
                {
                    ScrollConversation(
                        key.Key == ConsoleKey.UpArrow ? 3 : -3,
                        prompt,
                        buffer.ToString(),
                        suggestions,
                        selectedSuggestion,
                        ref renderedSuggestionLines);
                    continue;
                }

                if (key.Key == ConsoleKey.Tab && suggestions.Count > 0)
                {
                    buffer.Clear();
                    buffer.Append(suggestions[selectedSuggestion < 0 ? 0 : selectedSuggestion]);
                    selectedSuggestion = -1;
                    continue;
                }

                if (key.Key == ConsoleKey.Escape || key.KeyChar == '\u001b')
                {
                    buffer.Clear();
                    selectedSuggestion = -1;
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    buffer.Append(key.KeyChar);
                    selectedSuggestion = -1;
                }
            }
        }
        finally
        {
            Console.TreatControlCAsInput = previousTreatControlCAsInput;
        }
    }

    private void HandleEscapeAction(
        EscapeActionKind action,
        string prompt,
        string input,
        IReadOnlyList<string> suggestions,
        ref int selectedSuggestion,
        ref int renderedSuggestionLines)
    {
        if (action is EscapeActionKind.SuggestionUp or EscapeActionKind.SuggestionDown)
        {
            if (suggestions.Count > 0)
            {
                selectedSuggestion = action == EscapeActionKind.SuggestionUp
                    ? selectedSuggestion <= 0 ? suggestions.Count - 1 : selectedSuggestion - 1
                    : selectedSuggestion >= suggestions.Count - 1 ? 0 : selectedSuggestion + 1;
                return;
            }

            ScrollConversation(
                action == EscapeActionKind.SuggestionUp ? 3 : -3,
                prompt,
                input,
                suggestions,
                selectedSuggestion,
                ref renderedSuggestionLines);
            return;
        }

        int delta = action switch
        {
            EscapeActionKind.PageUp => Math.Max(1, GetConsoleHeight() / 2),
            EscapeActionKind.PageDown => -Math.Max(1, GetConsoleHeight() / 2),
            EscapeActionKind.Home => int.MaxValue,
            EscapeActionKind.End => int.MinValue,
            _ => 0,
        };
        ScrollConversation(
            delta,
            prompt,
            input,
            suggestions,
            selectedSuggestion,
            ref renderedSuggestionLines);
    }

    private static EscapeSequenceState ConsumeEscapeSequence(
        StringBuilder sequence,
        ConsoleKeyInfo key,
        out EscapeAction action)
    {
        action = default;
        if (key.KeyChar == '\0' || key.KeyChar == '\u001b')
        {
            return EscapeSequenceState.Invalid;
        }

        sequence.Append(key.KeyChar);
        string value = sequence.ToString();
        action = value switch
        {
            "[A" => new EscapeAction(EscapeActionKind.SuggestionUp, null),
            "[B" => new EscapeAction(EscapeActionKind.SuggestionDown, null),
            "[5~" => new EscapeAction(EscapeActionKind.PageUp, null),
            "[6~" => new EscapeAction(EscapeActionKind.PageDown, null),
            "[H" => new EscapeAction(EscapeActionKind.Home, null),
            "[F" => new EscapeAction(EscapeActionKind.End, null),
            _ => default,
        };
        if (action.Kind != EscapeActionKind.None)
        {
            return EscapeSequenceState.Complete;
        }

        if (TryParseMouseEventText(value, out MouseEvent mouseEvent))
        {
            action = new EscapeAction(EscapeActionKind.Mouse, mouseEvent);
            return EscapeSequenceState.Complete;
        }

        return value.StartsWith("[", StringComparison.Ordinal) && value.Length <= 32
            ? EscapeSequenceState.Pending
            : EscapeSequenceState.Invalid;
    }

    private bool ToggleToolCallAtRow(
        int mouseRow,
        string prompt,
        string input,
        IReadOnlyList<string> suggestions,
        int selectedSuggestion,
        ref int renderedSuggestionLines)
    {
        ToolCallView? view = toolCallViews.FirstOrDefault(item => item.ContainsVisibleRow(mouseRow));
        if (view is null)
        {
            return false;
        }

        view.ExpandedOverride = !IsToolCallExpanded(view);
        RenderConversationViewport();

        renderedSuggestionLines = RenderInputLine(
            prompt,
            input,
            suggestions,
            selectedSuggestion,
            previousSuggestionLines: 0);
        return true;
    }

    private void ScrollConversation(
        int delta,
        string prompt,
        string input,
        IReadOnlyList<string> suggestions,
        int selectedSuggestion,
        ref int renderedSuggestionLines)
    {
        int nextOffset = delta == int.MaxValue
            ? maximumConversationScrollOffset
            : delta == int.MinValue
                ? 0
                : Math.Clamp(
                    conversationScrollOffset + delta,
                    0,
                    maximumConversationScrollOffset);
        if (nextOffset == conversationScrollOffset)
        {
            return;
        }

        conversationScrollOffset = nextOffset;
        RenderConversationViewport();
        renderedSuggestionLines = RenderInputLine(
            prompt,
            input,
            suggestions,
            selectedSuggestion,
            previousSuggestionLines: 0);
    }

    private static bool TryReadMouseEvent(out MouseEvent mouseEvent)
    {
        mouseEvent = default;
        const int mouseSequenceTimeoutMilliseconds = 1000;
        if (!WaitForInputKey(mouseSequenceTimeoutMilliseconds))
        {
            return false;
        }

        ConsoleKeyInfo openingBracket = Console.ReadKey(intercept: true);
        if (openingBracket.KeyChar != '[' || !WaitForInputKey(mouseSequenceTimeoutMilliseconds))
        {
            return false;
        }

        ConsoleKeyInfo sgrMarker = Console.ReadKey(intercept: true);
        if (sgrMarker.KeyChar != '<' || !WaitForInputKey(mouseSequenceTimeoutMilliseconds))
        {
            return false;
        }

        StringBuilder payload = new();
        char terminator = '\0';
        while (WaitForInputKey(mouseSequenceTimeoutMilliseconds))
        {
            char value = Console.ReadKey(intercept: true).KeyChar;
            if (value is 'M' or 'm')
            {
                terminator = value;
                break;
            }

            payload.Append(value);
            if (payload.Length > 32)
            {
                return false;
            }
        }

        if (terminator != 'M')
        {
            return false;
        }

        string[] parts = payload.ToString().Split(';');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out int button) ||
            !int.TryParse(parts[1], out int column) ||
            !int.TryParse(parts[2], out int row))
        {
            return false;
        }

        mouseEvent = new MouseEvent(
            button,
            column,
            row,
            terminator == 'M',
            (button & 64) != 0
                ? (button & 1) == 0 ? 1 : -1
                : 0);
        return true;
    }

    private static bool TryParseMouseEventText(string value, out MouseEvent mouseEvent)
    {
        mouseEvent = default;
        if (!value.StartsWith("[<", StringComparison.Ordinal) ||
            value.Length < 8 ||
            value[^1] is not ('M' or 'm'))
        {
            return false;
        }

        string[] parts = value[2..^1].Split(';');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out int button) ||
            !int.TryParse(parts[1], out int column) ||
            !int.TryParse(parts[2], out int row))
        {
            return false;
        }

        mouseEvent = new MouseEvent(
            button,
            column,
            row,
            value[^1] == 'M',
            (button & 64) != 0
                ? (button & 1) == 0 ? 1 : -1
                : 0);
        return true;
    }

    private static bool WaitForInputKey(int timeoutMilliseconds = 25)
    {
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!Console.KeyAvailable && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(1);
        }

        return Console.KeyAvailable;
    }

    private static int RenderInputLine(
        string prompt,
        string input,
        IReadOnlyList<string> suggestions,
        int selectedSuggestion,
        int previousSuggestionLines)
    {
        Console.Write("\r\u001b[2K");
        EnsureSuggestionSpace(suggestions.Count);
        Console.Write(prompt);
        Console.Write(input);

        // Keep the cursor at the end of the input while the lines below it are redrawn.
        Console.Write("\u001b[s");

        if (previousSuggestionLines > 0)
        {
            Console.Write("\u001b[1B\r");
            for (int index = 0; index < previousSuggestionLines; index++)
            {
                Console.Write("\u001b[2K");
                if (index < previousSuggestionLines - 1)
                {
                    Console.Write("\u001b[1B\r");
                }
            }

            Console.Write("\u001b[u");
        }

        if (suggestions.Count > 0)
        {
            Console.Write("\u001b[1B\r");
            for (int index = 0; index < suggestions.Count; index++)
            {
                Console.Write("\u001b[2K");
                string marker = index == selectedSuggestion ? "▶ " : "  ";
                Console.Write($"  {marker}{suggestions[index]}");
                if (index < suggestions.Count - 1)
                {
                    Console.Write("\u001b[1B\r");
                }
            }
        }

        Console.Write("\u001b[u");
        return suggestions.Count;
    }

    private static void EnsureSuggestionSpace(int suggestionLines)
    {
        if (suggestionLines <= 0)
        {
            return;
        }

        try
        {
            int bottomRow = Console.WindowTop + Console.WindowHeight - 1;
            int currentRow = Console.CursorTop;
            int rowsToReserve = currentRow + suggestionLines - bottomRow;
            if (rowsToReserve <= 0)
            {
                return;
            }

            // Scroll the viewport first, then move back up so the input line has
            // enough physical rows below it for the suggestion list.
            for (int index = 0; index < rowsToReserve; index++)
            {
                Console.WriteLine();
            }

            Console.Write($"\u001b[{rowsToReserve}A\r");
        }
        catch (IOException)
        {
            // Some redirected or legacy consoles do not expose window dimensions.
        }
    }

    private static int CurrentConsoleRow()
    {
        try
        {
            // SGR mouse coordinates are relative to the visible window, while
            // Console.CursorTop is relative to the whole screen buffer.
            return Console.GetCursorPosition().Top - Console.WindowTop + 1;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    private static void TryClear()
    {
        if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
        {
            AnsiConsole.Clear();
        }
    }

    private static bool EnableMouseReporting()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return false;
        }

        EnableVirtualTerminalInput();

        // SGR mouse mode reports click and wheel coordinates as
        // ESC[<button;column;rowM. 1002 improves wheel support in terminals
        // that only emit wheel events in button-event tracking mode.
        Console.Write("\u001b[?1000h\u001b[?1002h\u001b[?1006h");
        Console.Out.Flush();
        return true;
    }

    private static void DisableMouseReporting()
    {
        Console.Write("\u001b[?1006l\u001b[?1002l\u001b[?1000l");
        Console.Out.Flush();

        if (originalConsoleInputMode.HasValue && OperatingSystem.IsWindows())
        {
            IntPtr inputHandle = GetStdHandle(StandardInputHandle);
            SetConsoleMode(inputHandle, originalConsoleInputMode.Value);
            originalConsoleInputMode = null;
        }
    }

    private static void EnableVirtualTerminalInput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IntPtr inputHandle = GetStdHandle(StandardInputHandle);
        if (inputHandle == IntPtr.Zero ||
            !GetConsoleMode(inputHandle, out uint inputMode))
        {
            return;
        }

        const uint enableVirtualTerminalInput = 0x0200;
        if ((inputMode & enableVirtualTerminalInput) != 0)
        {
            return;
        }

        if (SetConsoleMode(inputHandle, inputMode | enableVirtualTerminalInput))
        {
            originalConsoleInputMode = inputMode;
        }
    }

    private const int StandardInputHandle = -10;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr consoleHandle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr consoleHandle, uint mode);

    private void RenderWelcome()
    {
        AnsiConsole.Write(new FigletText("AEGIS").Centered().Color(Color.DeepSkyBlue1));
        AnsiConsole.Write(new Panel(new Markup("[bold]Microsoft Agent Framework Coding TUI[/]\nPlan · Todo · Tools · Verify"))
            .Border(BoxBorder.Rounded)
            .Header("[deepskyblue1]Aegis Coding Agent[/]"));
        AnsiConsole.MarkupLine("[grey]依頼はそのまま入力。コマンド: /help /tools /provider /model[/]");
        AnsiConsole.MarkupLine("[grey]/exit で終了。Tool Call / Result はクリックで個別に展開・折りたたみできます。[/]");
    }

    private void RenderDashboard()
    {
        ProviderProfile? profile = providerRegistry.Resolve();
        string providerKind = profile is null
            ? settings.BackendKind
            : ProviderKinds.DisplayName(profile.Kind);
        string endpoint = settings.BackendKind.Equals("maf-chatgpt-oauth", StringComparison.OrdinalIgnoreCase)
            ? "ChatGPT Codex"
            : settings.BaseUrl ?? "OpenAI API";
        Table table = new Table().NoBorder().AddColumn(new TableColumn("項目").Width(14)).AddColumn(new TableColumn("値"));
        table.AddRow("プロバイダー", Markup.Escape(settings.ProviderName));
        table.AddRow("種類", Markup.Escape(providerKind));
        table.AddRow("モデル", Markup.Escape(settings.Model));
        table.AddRow("接続先", Markup.Escape(endpoint));
        table.AddRow("ワークスペース", Markup.Escape(workspace.RootPath));
        table.AddRow("ターン", turnNumber.ToString());
        if (oauth is not null &&
            (settings.BackendKind.Equals("maf-chatgpt-oauth", StringComparison.OrdinalIgnoreCase) || oauth.HasStoredCredential))
        {
            table.AddRow("アカウント", Markup.Escape(oauth.AccountSummary));
        }

        Grid grid = new Grid().AddColumn().AddColumn();
        grid.AddRow(new Panel(table).Header("[bold]接続・セッション[/]").Border(BoxBorder.Rounded), new Panel(new Text(RecentHistory())).Header("[bold]直近の会話[/]").Border(BoxBorder.Rounded));
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
            "/toolcalls            Tool Call 表示状態\n" +
            "/toolcalls collapse   Tool Call / Result を折りたたむ\n" +
            "/toolcalls expand     Tool Call / Result を詳細表示\n" +
            "Mouse wheel / ↑↓     会話履歴をスクロール\n" +
            "PageUp/PageDown      会話履歴をページ移動\n" +
            "/tools                利用可能な Tool と説明\n" +
            "/provider list        登録済みプロバイダー\n" +
            "/provider add         プロバイダー登録ウィザード\n" +
            "/provider use <name>  次回起動のプロバイダー切替\n" +
            "/auth openai          ChatGPT OAuth ログイン\n" +
            "/model list           モデル候補一覧\n" +
            "/model use <model>    モデル切替（/model <model> も可）\n" +
            "/history              現在の履歴を表示\n" +
            "/history list         同じ workspace の履歴一覧\n" +
            "/history new          新しい履歴を開始\n" +
            "/history use         一覧から履歴を選択\n" +
            "/history use <id>     ID で履歴を切り替え\n" +
            "/history delete <id>  履歴を削除\n" +
            "/history clear        この workspace の履歴を全削除\n" +
            "/status               git status\n" +
            "/clear                会話セッションをクリア\n" +
            "/exit                 終了"))
            .Header("[bold]commands[/]")
            .Border(BoxBorder.Rounded));
    }

    private void RenderToolCallMode()
    {
        string mode = toolCallsCollapsed ? "折りたたみ表示" : "詳細表示";
        RenderPanel("Tool Call", $"現在の表示: {mode}\n/toolcalls collapse または /toolcalls expand で切り替えできます。", Color.Cyan1);
    }

    private void RenderTools()
    {
        if (mafService is null || mafService.Tools.Count == 0)
        {
            RenderPanel("Tool", "利用可能な Tool はありません。プロバイダーを設定してください。", Color.Yellow);
            return;
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Tool")
            .AddColumn("説明");
        foreach (AITool tool in mafService.Tools)
        {
            string description = string.IsNullOrWhiteSpace(tool.Description)
                ? tool is HostedWebSearchTool
                    ? "モデル側の hosted Web Search を使ってインターネットを検索します。"
                    : "(説明なし)"
                : tool.Description;
            table.AddRow(
                Markup.Escape(tool.Name),
                Markup.Escape(description));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]ファイル編集・コマンド実行などの Tool は、必要なときに Agent が呼び出します。[/]");
    }

    private static void RenderPanel(string title, string text, Color color)
    {
        AnsiConsole.Write(new Panel(new Text(text)).Header($"[{color.ToMarkup()}]{Markup.Escape(title)}[/]").Border(BoxBorder.Rounded));
    }

    private static string Truncate(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        return value[..maximumLength] + "\n...(表示を短縮しました)";
    }

    private static IReadOnlyList<ChatMessage> ToChatMessages(IEnumerable<ChatEntry> entries)
    {
        return entries
            .Where(entry => entry.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ||
                entry.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            .Select(entry => new ChatMessage(
                entry.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? ChatRole.User : ChatRole.Assistant,
                entry.Text))
            .ToArray();
    }

    private sealed record ViewportLine(string Text, ToolCallView? View);

    private enum EscapeSequenceState
    {
        Pending,
        Complete,
        Invalid,
    }

    private enum EscapeActionKind
    {
        None,
        SuggestionUp,
        SuggestionDown,
        PageUp,
        PageDown,
        Home,
        End,
        Mouse,
    }

    private readonly record struct EscapeAction(EscapeActionKind Kind, MouseEvent? MouseEvent);

    private readonly record struct MouseEvent(
        int Button,
        int Column,
        int Row,
        bool IsPress,
        int WheelDirection)
    {
        public bool IsWheel => WheelDirection != 0;

        public bool IsLeftClick => IsPress && !IsWheel && (Button & 3) == 0;
    }

    private sealed class ToolCallView(string callId, string name)
    {
        public string CallId { get; } = callId;

        public string Name { get; } = name;

        public string Arguments { get; set; } = "(引数なし)";

        public string? Result { get; set; }

        public bool HasResult { get; set; }

        public bool Succeeded { get; set; }

        public bool? ExpandedOverride { get; set; }

        public int CallStartRow { get; set; } = -1;

        public int CallEndRow { get; set; } = -1;

        public int ResultStartRow { get; set; } = -1;

        public int ResultEndRow { get; set; } = -1;

        public int VisibleStartRow { get; set; } = -1;

        public int VisibleEndRow { get; set; } = -1;

        public bool ContainsVisibleRow(int row) =>
            IsWithin(row, VisibleStartRow, VisibleEndRow);

        private static bool IsWithin(int row, int start, int end) =>
            start > 0 && end >= start && row >= start && row <= end;
    }

    private sealed record ChatEntry(string Role, string Text);
}
