using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AegisAgent.Core;

/// <summary>
/// Legacy JSONL client for the official Codex app-server.
/// The default Aegis OAuth path no longer needs this process; it remains as a
/// compatibility building block for callers that explicitly choose app-server.
/// </summary>
public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly string workspaceRoot;
    private readonly string model;
    private readonly Func<string, Task<string>> approvalHandler;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private Process? process;
    private StreamWriter? input;
    private StreamReader? output;
    private int nextRequestId;
    private string? threadId;

    public CodexAppServerClient(string workspaceRoot, string model, Func<string, Task<string>> approvalHandler)
    {
        this.workspaceRoot = workspaceRoot;
        this.model = model;
        this.approvalHandler = approvalHandler;
    }

    public string? AccountSummary { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (process is not null)
        {
            return;
        }

        string command = Environment.GetEnvironmentVariable("AEGIS_CODEX_COMMAND")
            ?? "codex";
        ProcessStartInfo startInfo = new()
        {
            FileName = command,
            WorkingDirectory = workspaceRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("app-server");

        process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Codex app-server プロセスを起動できませんでした。");
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Codex CLI が見つかりません。PATH に codex.exe を追加するか、AEGIS_CODEX_COMMAND に実行ファイルの絶対パスを設定してください。({command})",
                exception);
        }

        input = process.StandardInput;
        output = process.StandardOutput;
        _ = DrainErrorsAsync(process.StandardError);

        JsonObject initialize = new()
        {
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "aegis-agent",
                ["title"] = "Aegis Coding Agent",
                ["version"] = "0.1.0",
            },
        };
        await SendRequestAndWaitAsync("initialize", initialize, cancellationToken);
        await SendAsync(new JsonObject { ["method"] = "initialized", ["params"] = new JsonObject() }, cancellationToken);

        JsonObject threadParams = new()
        {
            ["model"] = model,
            ["cwd"] = workspaceRoot,
            ["approvalPolicy"] = "on-request",
            ["sandbox"] = "workspaceWrite",
            ["serviceName"] = "aegis-agent",
        };
        JsonObject threadResponse = await SendRequestAndWaitAsync("thread/start", threadParams, cancellationToken);
        threadId = threadResponse["result"]?["thread"]?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Codex app-server did not return a thread id.");

        await RefreshAccountAsync(cancellationToken);
    }

    public async Task<bool> LoginWithChatGptAsync(CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        JsonObject loginParams = new()
        {
            ["type"] = "chatgpt",
            ["useHostedLoginSuccessPage"] = true,
            ["appBrand"] = "chatgpt",
        };
        int requestId = NextRequestId();
        await SendAsync(Request("account/login/start", requestId, loginParams), cancellationToken);

        string? loginId = null;
        bool? completed = null;
        while (completed is null)
        {
            JsonObject message = await ReadMessageAsync(cancellationToken);
            if (GetInt(message["id"]) == requestId)
            {
                if (message["error"] is not null)
                {
                    throw new InvalidOperationException(GetErrorMessage(message));
                }

                JsonObject result = message["result"]?.AsObject() ?? [];
                loginId = result["loginId"]?.GetValue<string>();
                string? authUrl = result["authUrl"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(authUrl))
                {
                    OpenBrowser(authUrl);
                }

                continue;
            }

            if (await HandleServerRequestOrNotificationAsync(message, cancellationToken) is { } loginCompleted)
            {
                if (loginCompleted.Method == "account/login/completed" &&
                    (loginId is null || loginCompleted.Params?["loginId"]?.GetValue<string>() == loginId))
                {
                    completed = loginCompleted.Params?["success"]?.GetValue<bool>() ?? false;
                }
            }
        }

        await RefreshAccountAsync(cancellationToken);
        return completed == true;
    }

    public async Task<string> RunTurnAsync(string prompt, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        if (threadId is null)
        {
            throw new InvalidOperationException("Codex app-server thread was not initialized.");
        }

        int requestId = NextRequestId();
        JsonObject turnParams = new()
        {
            ["threadId"] = threadId,
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = prompt,
            }),
        };
        await SendAsync(Request("turn/start", requestId, turnParams), cancellationToken);

        System.Text.StringBuilder answer = new();
        while (true)
        {
            JsonObject message = await ReadMessageAsync(cancellationToken);
            if (GetInt(message["id"]) == requestId)
            {
                if (message["error"] is not null)
                {
                    throw new InvalidOperationException(GetErrorMessage(message));
                }

                continue;
            }

            ServerMessage? handled = await HandleServerRequestOrNotificationAsync(message, cancellationToken);
            if (handled is null)
            {
                continue;
            }

            JsonObject parameters = handled.Params ?? [];
            if (handled.Method == "item/agentMessage/delta")
            {
                string delta = parameters["delta"]?.GetValue<string>() ?? parameters["text"]?.GetValue<string>() ?? string.Empty;
                answer.Append(delta);
                onProgress?.Invoke("agentMessage");
            }
            else if (handled.Method == "item/plan/delta")
            {
                onProgress?.Invoke("plan");
            }
            else if (handled.Method == "item/commandExecution/outputDelta")
            {
                onProgress?.Invoke("command");
            }
            else if (handled.Method == "turn/completed")
            {
                string status = parameters["turn"]?["status"]?.GetValue<string>() ?? "completed";
                if (status is "failed" or "interrupted")
                {
                    string? error = parameters["turn"]?["error"]?["message"]?.GetValue<string>();
                    throw new InvalidOperationException(error ?? $"Codex turn ended with status '{status}'.");
                }

                return answer.ToString().Trim();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (process is not null && !process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process already exited.
            }
        }

        process?.Dispose();
        writeGate.Dispose();
        await Task.CompletedTask;
    }

    private async Task RefreshAccountAsync(CancellationToken cancellationToken)
    {
        JsonObject response = await SendRequestAndWaitAsync(
            "account/read",
            new JsonObject { ["refreshToken"] = false },
            cancellationToken);
        JsonObject? account = response["result"]?["account"]?.AsObject();
        AccountSummary = account is null
            ? "未ログイン"
            : $"{account["type"]?.GetValue<string>() ?? "unknown"} / {account["planType"]?.GetValue<string>() ?? "plan unknown"}";
    }

    private async Task<ServerMessage?> HandleServerRequestOrNotificationAsync(JsonObject message, CancellationToken cancellationToken)
    {
        string? method = message["method"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(method))
        {
            return null;
        }

        JsonObject? parameters = message["params"]?.AsObject();
        JsonNode? id = message["id"];
        if (method.Contains("requestApproval", StringComparison.Ordinal))
        {
            string command = parameters?["command"]?.GetValue<string>()
                ?? parameters?["reason"]?.GetValue<string>()
                ?? "Codex requests approval";
            string decision = await approvalHandler(command);
            JsonObject result = new() { ["decision"] = decision };
            if (id is not null)
            {
                await SendAsync(new JsonObject { ["id"] = id.DeepClone(), ["result"] = result }, cancellationToken);
            }

            return new ServerMessage(method, parameters);
        }

        return new ServerMessage(method, parameters);
    }

    private async Task<JsonObject> SendRequestAndWaitAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        int requestId = NextRequestId();
        await SendAsync(Request(method, requestId, parameters), cancellationToken);
        while (true)
        {
            JsonObject message = await ReadMessageAsync(cancellationToken);
            if (GetInt(message["id"]) == requestId)
            {
                if (message["error"] is not null)
                {
                    throw new InvalidOperationException(GetErrorMessage(message));
                }

                return message;
            }

            await HandleServerRequestOrNotificationAsync(message, cancellationToken);
        }
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        EnsureStarted();
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await input!.WriteLineAsync(message.ToJsonString());
            await input.FlushAsync(cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task<JsonObject> ReadMessageAsync(CancellationToken cancellationToken)
    {
        EnsureStarted();
        string? line = await output!.ReadLineAsync(cancellationToken);
        if (line is null)
        {
            throw new InvalidOperationException("Codex app-server closed its JSONL stream.");
        }

        try
        {
            return JsonNode.Parse(line)?.AsObject() ?? throw new JsonException("Empty JSON-RPC message.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Invalid response from Codex app-server: {exception.Message}");
        }
    }

    private static JsonObject Request(string method, int id, JsonObject parameters) => new()
    {
        ["id"] = id,
        ["method"] = method,
        ["params"] = parameters,
    };

    private int NextRequestId() => Interlocked.Increment(ref nextRequestId);

    private void EnsureStarted()
    {
        if (process is null || input is null || output is null)
        {
            throw new InvalidOperationException("Codex app-server is not running. Install Codex CLI and start the provider first.");
        }
    }

    private static int? GetInt(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<int>(out int result) ? result : null;
    }

    private static string GetErrorMessage(JsonObject message)
    {
        return message["error"]?["message"]?.GetValue<string>() ?? "Codex app-server returned an unknown error.";
    }

    private static void OpenBrowser(string url)
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private static async Task DrainErrorsAsync(StreamReader errors)
    {
        while (await errors.ReadLineAsync() is not null)
        {
        }
    }

    private sealed record ServerMessage(string Method, JsonObject? Params);
}
