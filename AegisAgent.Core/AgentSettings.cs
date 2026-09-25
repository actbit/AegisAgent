namespace AegisAgent.Core;

public sealed record AgentSettings(
    string ApiKey,
    string Model,
    string? BaseUrl,
    int MaxToolIterations,
    int MaxOutputTokens,
    string ProviderName,
    string BackendKind)
{
    public static AgentSettings Load(string? modelOverride, string? baseUrlOverride, ProviderProfile? profile = null)
    {
        if (profile?.Kind.Equals(ProviderKinds.ChatGptOAuth, StringComparison.OrdinalIgnoreCase) == true)
        {
            return new AgentSettings(
                string.Empty,
                modelOverride ?? profile.Model,
                null,
                ParsePositiveInt("AEGIS_MAX_TOOL_ITERATIONS", 20),
                ParsePositiveInt("AEGIS_MAX_OUTPUT_TOKENS", 8_192),
                profile.Name,
                "maf-chatgpt-oauth");
        }

        if (profile is not null && ProviderKinds.IsLocal(profile.Kind))
        {
            return new AgentSettings(
                profile.GetApiKey() ?? "local",
                modelOverride ?? profile.Model,
                NormalizeBaseUrl(baseUrlOverride ?? profile.BaseUrl ?? ProviderKinds.DefaultBaseUrl(profile.Kind)),
                ParsePositiveInt("AEGIS_MAX_TOOL_ITERATIONS", 20),
                ParsePositiveInt("AEGIS_MAX_OUTPUT_TOKENS", 8_192),
                profile.Name,
                "maf-local");
        }

        string? apiKey = profile?.GetApiKey() ?? FirstEnvironment(EnvironmentKeyNames(profile?.Kind));
        string model = modelOverride ?? profile?.Model ?? Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1-mini";
        string? baseUrl = baseUrlOverride ?? profile?.BaseUrl ??
            (profile is null
                ? Environment.GetEnvironmentVariable("OPENAI_BASE_URL")
                : ProviderKinds.DefaultBaseUrl(profile.Kind));

        return new AgentSettings(
            apiKey ?? string.Empty,
            model,
            NormalizeBaseUrl(baseUrl),
            ParsePositiveInt("AEGIS_MAX_TOOL_ITERATIONS", 20),
            ParsePositiveInt("AEGIS_MAX_OUTPUT_TOKENS", 8_192),
            profile?.Name ?? "environment",
            profile?.Kind.Equals(ProviderKinds.Anthropic, StringComparison.OrdinalIgnoreCase) == true
                ? "maf-anthropic"
                : "maf");
    }

    private static string? FirstEnvironment(params string[] names)
    {
        foreach (string name in names)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string[] EnvironmentKeyNames(string? kind) => kind?.ToLowerInvariant() switch
    {
        ProviderKinds.Anthropic => ["ANTHROPIC_API_KEY"],
        ProviderKinds.OpenRouter => ["OPENROUTER_API_KEY", "OPENAI_API_KEY"],
        ProviderKinds.DeepSeek => ["DEEPSEEK_API_KEY", "OPENAI_API_KEY"],
        _ => ["OPENAI_API_KEY", "AZURE_OPENAI_API_KEY"],
    };

    private static string? NormalizeBaseUrl(string? baseUrl)
    {
        return string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim().TrimEnd('/');
    }

    private static int ParsePositiveInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0
            ? value
            : fallback;
    }
}

public static class DotEnv
{
    public static void Load(string startDirectory)
    {
        string? directory = Path.GetFullPath(startDirectory);

        while (directory is not null)
        {
            string path = Path.Combine(directory, ".env");
            if (File.Exists(path))
            {
                foreach (string rawLine in File.ReadLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith('#'))
                    {
                        continue;
                    }

                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    string name = line[..separator].Trim();
                    string value = line[(separator + 1)..].Trim().Trim('"', '\'');
                    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                    {
                        Environment.SetEnvironmentVariable(name, value);
                    }
                }

                return;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }
    }
}
