using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AegisAgent.Core;

public sealed class ProviderRegistry
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AegisAgent.provider.v1");
    private readonly List<ProviderProfile> profiles;

    private ProviderRegistry(string configPath, List<ProviderProfile> profiles, string? activeProvider)
    {
        ConfigPath = configPath;
        this.profiles = profiles;
        ActiveProvider = activeProvider;
    }

    public string ConfigPath { get; }

    public string? ActiveProvider { get; private set; }

    public IReadOnlyList<ProviderProfile> Profiles => profiles;

    public static ProviderRegistry Load()
    {
        string configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AegisAgent");
        string configPath = Path.Combine(configDirectory, "providers.json");

        try
        {
            if (File.Exists(configPath))
            {
                ProviderRegistryFile? file = JsonSerializer.Deserialize<ProviderRegistryFile>(File.ReadAllText(configPath));
                return new ProviderRegistry(configPath, file?.Providers ?? [], file?.ActiveProvider);
            }
        }
        catch (JsonException)
        {
            // Start with an empty registry; the TUI can repair it by saving a new profile.
        }
        catch (IOException)
        {
            // A read-only profile store should not prevent environment-based startup.
        }

        return new ProviderRegistry(configPath, [], null);
    }

    public ProviderProfile? Resolve(string? name = null)
    {
        string? requested = name ?? ActiveProvider;
        return string.IsNullOrWhiteSpace(requested)
            ? null
            : profiles.FirstOrDefault(profile => profile.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
    }

    public ProviderProfile Upsert(string name, string kind, string model, string? baseUrl, string? apiKey, string? apiKeyEnvironmentVariable)
    {
        ProviderProfile profile = new(
            name.Trim(),
            kind,
            model.Trim(),
            string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim().TrimEnd('/'),
            string.IsNullOrWhiteSpace(apiKeyEnvironmentVariable) ? null : apiKeyEnvironmentVariable.Trim(),
            Protect(apiKey));

        int index = profiles.FindIndex(existing => existing.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            profiles[index] = profile;
        }
        else
        {
            profiles.Add(profile);
        }

        ActiveProvider ??= profile.Name;
        Save();
        return profile;
    }

    public bool SetActive(string name)
    {
        ProviderProfile? profile = Resolve(name);
        if (profile is null)
        {
            return false;
        }

        ActiveProvider = profile.Name;
        Save();
        return true;
    }

    public bool Remove(string name)
    {
        int removed = profiles.RemoveAll(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return false;
        }

        if (ActiveProvider?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
        {
            ActiveProvider = profiles.FirstOrDefault()?.Name;
        }

        Save();
        return true;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        JsonSerializerOptions serializerOptions = new() { WriteIndented = true };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new ProviderRegistryFile(ActiveProvider, profiles), serializerOptions));
    }

    private static string? Protect(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Provider API-key storage currently uses Windows DPAPI. Configure an environment variable on non-Windows systems.");
        }

        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private sealed record ProviderRegistryFile(string? ActiveProvider, List<ProviderProfile> Providers);
}

public sealed record ProviderProfile(
    string Name,
    string Kind,
    string Model,
    string? BaseUrl,
    string? ApiKeyEnvironmentVariable,
    string? ProtectedApiKey)
{
    public string? GetApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable))
        {
            return Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(ProtectedApiKey))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            byte[] encrypted = Convert.FromBase64String(ProtectedApiKey);
            byte[] plaintext = ProtectedData.Unprotect(
                encrypted,
                Encoding.UTF8.GetBytes("AegisAgent.provider.v1"),
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
