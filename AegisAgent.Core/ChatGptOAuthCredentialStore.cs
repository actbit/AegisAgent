using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace AegisAgent.Core;

/// <summary>
/// Stores and refreshes the ChatGPT subscription OAuth credential used by the
/// ChatGPT Codex endpoint. The browser flow intentionally mirrors the public
/// OpenCode/Codex PKCE flow instead of requiring a Codex executable.
/// </summary>
public sealed class ChatGptOAuthCredentialStore
{
    public const string DefaultModel = "gpt-6-sol";

    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string Issuer = "https://auth.openai.com";
    private const string TokenEndpoint = Issuer + "/oauth/token";
    private const string RedirectUri = "http://localhost:1455/auth/callback";
    private const string CodexEndpoint = "https://chatgpt.com/backend-api/codex";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AegisAgent.chatgpt-oauth.v1");

    private readonly string credentialPath;
    private readonly HttpClient httpClient;
    private OAuthToken? token;
    private readonly SemaphoreSlim tokenGate = new(1, 1);

    public ChatGptOAuthCredentialStore(HttpClient? httpClient = null)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AegisAgent");
        credentialPath = Path.Combine(directory, "openai-chatgpt-oauth.dat");
        this.httpClient = httpClient ?? new HttpClient();
        token = LoadToken();
    }

    public bool HasStoredCredential => token is not null && !string.IsNullOrWhiteSpace(token.RefreshToken);

    public string? AccountId => token?.AccountId;

    public string? Residency => token?.Residency;

    public string AccountSummary => string.IsNullOrWhiteSpace(AccountId)
        ? (HasStoredCredential ? "ChatGPT OAuth" : "未ログイン")
        : $"ChatGPT OAuth / {AccountId[..Math.Min(8, AccountId.Length)]}…";

    public async Task<bool> LoginAsync(CancellationToken cancellationToken = default)
    {
        byte[] verifierBytes = RandomNumberGenerator.GetBytes(32);
        string verifier = Base64UrlEncode(verifierBytes);
        string challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        string authorizationUrl = BuildAuthorizationUrl(challenge, state);
        using HttpListener listener = new();
        listener.Prefixes.Add("http://localhost:1455/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException exception)
        {
            throw new InvalidOperationException(
                "OAuth のコールバックポート localhost:1455 を開けませんでした。" +
                "別のアプリが使用中でないか確認してください。", exception);
        }

        OpenBrowser(authorizationUrl);

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("OAuth のブラウザー認証がタイムアウトしました。");
        }

        string? code = context.Request.QueryString["code"];
        string? returnedState = context.Request.QueryString["state"];
        string? error = context.Request.QueryString["error"];
        if (!string.Equals(context.Request.Url?.AbsolutePath, "/auth/callback", StringComparison.Ordinal))
        {
            await WriteCallbackResponseAsync(context.Response, "不正な OAuth callback です。このタブを閉じてください。");
            throw new InvalidOperationException("OAuth コールバックのパスが不正です。");
        }

        await WriteCallbackResponseAsync(context.Response, string.IsNullOrWhiteSpace(error)
            ? "Aegis の認証が完了しました。このタブを閉じてください。"
            : $"Aegis の認証に失敗しました: {error}");

        if (!string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException($"ChatGPT OAuth が拒否されました: {error}");
        }

        if (!string.Equals(state, returnedState, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("OAuth コールバックの state または code が不正です。");
        }

        OAuthTokenResponse tokenResponse = await ExchangeCodeAsync(code, verifier, cancellationToken);
        OAuthToken newToken = CreateToken(tokenResponse, token?.RefreshToken);
        await SaveTokenAsync(newToken, cancellationToken);
        return true;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await tokenGate.WaitAsync(cancellationToken);
        try
        {
            token ??= LoadToken();
            if (token is null || string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                throw new InvalidOperationException("ChatGPT OAuth が未設定です。TUI で /auth openai を実行してください。");
            }

            if (token.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return token.AccessToken;
            }

            OAuthTokenResponse response = await RefreshTokenAsync(token.RefreshToken, cancellationToken);
            OAuthToken refreshed = CreateToken(response, token.RefreshToken);
            await SaveTokenAsync(refreshed, cancellationToken);
            return refreshed.AccessToken;
        }
        finally
        {
            tokenGate.Release();
        }
    }

    public async Task<ResponsesClient> CreateResponsesClientAsync(
        string model,
        CancellationToken cancellationToken = default)
    {
        string accessToken = await GetAccessTokenAsync(cancellationToken);
        ApiKeyCredential credential = new(accessToken);
        ResponsesClientOptions options = new()
        {
            Endpoint = new Uri(CodexEndpoint),
            UserAgentApplicationId = "aegis-agent",
        };
        options.AddPolicy(new ChatGptOAuthHeaderPolicy(this, credential), PipelinePosition.BeforeTransport);
        return new ResponsesClient(credential, options);
    }

    private async Task<OAuthTokenResponse> ExchangeCodeAsync(
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        using FormUrlEncodedContent content = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
        });
        return await ReadTokenResponseAsync(await httpClient.PostAsync(TokenEndpoint, content, cancellationToken));
    }

    private async Task<OAuthTokenResponse> RefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        using FormUrlEncodedContent content = new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        });
        return await ReadTokenResponseAsync(await httpClient.PostAsync(TokenEndpoint, content, cancellationToken));
    }

    private static async Task<OAuthTokenResponse> ReadTokenResponseAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ChatGPT OAuth token endpoint returned {(int)response.StatusCode}: {body}");
        }

        OAuthTokenResponse? result = JsonSerializer.Deserialize<OAuthTokenResponse>(body);
        if (result is null || string.IsNullOrWhiteSpace(result.AccessToken))
        {
            throw new InvalidOperationException("OAuth token response に access_token がありません。");
        }

        return result;
    }

    private static OAuthToken CreateToken(OAuthTokenResponse response, string? previousRefreshToken)
    {
        string? accountId = FindAccountId(response.AccessToken) ?? FindAccountId(response.IdToken);
        return new OAuthToken(
            response.AccessToken,
            response.RefreshToken ?? previousRefreshToken ?? string.Empty,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, response.ExpiresIn)),
            accountId,
            FindResidency(response.AccessToken) ?? FindResidency(response.IdToken));
    }

    private static string BuildAuthorizationUrl(string challenge, string state)
    {
        Dictionary<string, string> parameters = new()
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = "openid profile email offline_access",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["state"] = state,
            ["originator"] = "aegis-agent",
        };

        string query = string.Join('&', parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return $"{Issuer}/oauth/authorize?{query}";
    }

    private OAuthToken? LoadToken()
    {
        if (!File.Exists(credentialPath))
        {
            return null;
        }

        try
        {
            byte[] protectedBytes = Convert.FromBase64String(File.ReadAllText(credentialPath));
            byte[] plaintext = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser)
                : protectedBytes;
            return JsonSerializer.Deserialize<OAuthToken>(plaintext);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    private async Task SaveTokenAsync(OAuthToken value, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ChatGPT OAuth の資格情報保存は現在 Windows DPAPI を使用します。");
        }

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(credentialPath)!);
        await File.WriteAllTextAsync(credentialPath, Convert.ToBase64String(protectedBytes), cancellationToken);
        token = value;
    }

    private static string? FindAccountId(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return null;
        }

        string[] parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            byte[] payload = Base64UrlDecode(parts[1]);
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("chatgpt_account_id", out JsonElement direct) && direct.ValueKind == JsonValueKind.String)
            {
                return direct.GetString();
            }

            foreach (string key in new[] { "https://api.openai.com/auth", "https://api.openai.com/auth/claims" })
            {
                if (root.TryGetProperty(key, out JsonElement claims) &&
                    claims.ValueKind == JsonValueKind.Object &&
                    claims.TryGetProperty("chatgpt_account_id", out JsonElement nested) &&
                    nested.ValueKind == JsonValueKind.String)
                {
                    return nested.GetString();
                }
            }

            if (root.TryGetProperty("organizations", out JsonElement organizations) &&
                organizations.ValueKind == JsonValueKind.Array &&
                organizations.EnumerateArray().FirstOrDefault() is JsonElement organization &&
                organization.ValueKind == JsonValueKind.Object &&
                organization.TryGetProperty("id", out JsonElement organizationId) &&
                organizationId.ValueKind == JsonValueKind.String)
            {
                return organizationId.GetString();
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            // Some token responses do not expose a JWT account claim.
        }

        return null;
    }

    private static string? FindResidency(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return null;
        }

        string[] parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("chatgpt_compute_residency", out JsonElement direct) && direct.ValueKind == JsonValueKind.String)
            {
                return NormalizeResidency(direct.GetString());
            }

            if (root.TryGetProperty("https://api.openai.com/auth", out JsonElement claims) &&
                claims.ValueKind == JsonValueKind.Object &&
                claims.TryGetProperty("chatgpt_compute_residency", out JsonElement nested) &&
                nested.ValueKind == JsonValueKind.String)
            {
                return NormalizeResidency(nested.GetString());
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            // Some token responses do not expose a residency claim.
        }

        return null;
    }

    private static string? NormalizeResidency(string? residency) =>
        string.IsNullOrWhiteSpace(residency) || residency.Equals("no_constraint", StringComparison.OrdinalIgnoreCase)
            ? null
            : residency;

    private static async Task WriteCallbackResponseAsync(HttpListenerResponse response, string message)
    {
        byte[] body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><p>{WebUtility.HtmlEncode(message)}</p>");
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    private static void OpenBrowser(string url)
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private sealed record OAuthToken(
        string AccessToken,
        string RefreshToken,
        DateTimeOffset ExpiresAtUtc,
        string? AccountId,
        string? Residency = null);

    private sealed record OAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("id_token")] string? IdToken);
}

internal sealed class ChatGptOAuthHeaderPolicy : PipelinePolicy
{
    private readonly ChatGptOAuthCredentialStore credentialStore;
    private readonly ApiKeyCredential credential;
    private readonly string sessionId = Guid.NewGuid().ToString("N");

    public ChatGptOAuthHeaderPolicy(ChatGptOAuthCredentialStore credentialStore, ApiKeyCredential credential)
    {
        this.credentialStore = credentialStore;
        this.credential = credential;
    }

    public override void Process(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        ApplyHeadersAsync(message).GetAwaiter().GetResult();
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        await ApplyHeadersAsync(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private async Task ApplyHeadersAsync(PipelineMessage message)
    {
        EnsureStoreDisabled(message);
        string accessToken = await credentialStore.GetAccessTokenAsync(message.CancellationToken);
        credential.Update(accessToken);
        message.Request.Headers.Set("Authorization", $"Bearer {accessToken}");
        message.Request.Headers.Set("originator", "aegis-agent");
        message.Request.Headers.Set("User-Agent", "aegis-agent/0.1.0");
        message.Request.Headers.Set("session-id", sessionId);
        if (!string.IsNullOrWhiteSpace(credentialStore.AccountId))
        {
            message.Request.Headers.Set("ChatGPT-Account-Id", credentialStore.AccountId!);
        }
        if (!string.IsNullOrWhiteSpace(credentialStore.Residency))
        {
            message.Request.Headers.Set("x-openai-internal-codex-residency", credentialStore.Residency!);
        }
    }

    private static void EnsureStoreDisabled(PipelineMessage message)
    {
        BinaryContent? content = message.Request.Content;
        if (content is null)
        {
            return;
        }

        using MemoryStream bodyStream = new();
        content.WriteTo(bodyStream, message.CancellationToken);
        string body = Encoding.UTF8.GetString(bodyStream.ToArray());
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            if (JsonNode.Parse(body) is not JsonObject root)
            {
                return;
            }

            root["store"] = false;
            message.Request.Content = BinaryContent.CreateJson(root.ToJsonString(), validate: false);
        }
        catch (JsonException)
        {
            // Let the SDK send non-JSON requests unchanged.
        }
    }
}
