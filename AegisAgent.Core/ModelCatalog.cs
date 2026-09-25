namespace AegisAgent.Core;

using System.Net.Http.Headers;
using System.Text.Json;

/// <summary>
/// Curated model suggestions for the TUI. Providers may support additional
/// model IDs; /model use accepts any non-empty ID so this list is not a limit.
/// </summary>
public static class ModelCatalog
{
    public static IReadOnlyList<string> GetSuggestions(
        ProviderProfile? profile,
        string currentModel,
        IEnumerable<string>? discoveredModels = null)
    {
        IEnumerable<string> models = profile?.Kind.ToLowerInvariant() switch
        {
            "openai-chatgpt-oauth" =>
            ["gpt-5.5", "gpt-5.3-codex-spark", "gpt-5.4", "gpt-5.4-mini", "gpt-6-sol", "gpt-6-luna"],
            "deepseek" =>
            ["deepseek-chat", "deepseek-reasoner"],
            "anthropic" =>
            ["claude-sonnet-4-5", "claude-opus-5-5", "claude-haiku-4-5"],
            "ollama" or "lm-studio" or "llama-cpp" or "vllm" =>
            ["local-model", "qwen3-coder", "deepseek-r1", "llama3.3"],
            "openrouter" =>
            ["openai/gpt-oss-120b", "qwen/qwen3-coder", "deepseek/deepseek-r1"],
            "openai-compatible" =>
            [currentModel],
            _ =>
            ["gpt-4.1-mini", "gpt-4.1", "gpt-4o", "o4-mini", "o3"],
        };

        return models
            .Concat(discoveredModels ?? [])
            .Append(currentModel)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<IReadOnlyList<string>> DiscoverAsync(
        AgentSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (settings.BackendKind.Equals("maf-chatgpt-oauth", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return [];
        }

        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        using HttpRequestMessage request = new(HttpMethod.Get, BuildModelsUri(settings));
        if (settings.BackendKind.Equals("maf-anthropic", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", settings.ApiKey);
            }

            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (!settings.BackendKind.Equals("maf-local", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseModelIds(body);
    }

    private static Uri BuildModelsUri(AgentSettings settings)
    {
        string suffix = settings.BackendKind.Equals("maf-anthropic", StringComparison.OrdinalIgnoreCase)
            ? "/v1/models"
            : "/models";
        return new Uri($"{settings.BaseUrl!.TrimEnd('/')}{suffix}", UriKind.Absolute);
    }

    private static IReadOnlyList<string> ParseModelIds(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            JsonElement models = root.TryGetProperty("data", out JsonElement data)
                ? data
                : root.TryGetProperty("models", out JsonElement nativeModels)
                    ? nativeModels
                    : default;
            if (models.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return models.EnumerateArray()
                .Select(model => model.ValueKind == JsonValueKind.String
                    ? model.GetString()
                    : model.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String
                        ? id.GetString()
                        : null)
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
