using System.Text;
using System.Text.Json;

namespace AegisAgent.Core;

/// <summary>
/// Small JSONL-backed history store shared by the TUI and future UI clients.
/// History is partitioned by workspace and provider so unrelated repositories
/// do not appear in the same conversation view.
/// </summary>
public sealed class ConversationHistoryStore
{
    private readonly string historyPath;
    private readonly string sessionSnapshotPath;
    private readonly object writeGate = new();

    public ConversationHistoryStore()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AegisAgent");
        historyPath = Path.Combine(directory, "history.jsonl");
        sessionSnapshotPath = Path.Combine(directory, "history-sessions.json");
    }

    public IReadOnlyList<ConversationHistoryEntry> LoadRecent(
        string workspaceRoot,
        string providerName,
        int maximumEntries = 40,
        string? sessionId = null)
    {
        if (!File.Exists(historyPath) || maximumEntries <= 0)
        {
            return [];
        }

        string normalizedWorkspace = NormalizeWorkspace(workspaceRoot);
        try
        {
            return File.ReadLines(historyPath, Encoding.UTF8)
                .Select(Parse)
                .Where(entry => entry is not null)
                .Select(entry => entry!)
                .Where(entry => entry.WorkspaceRoot.Equals(normalizedWorkspace, StringComparison.OrdinalIgnoreCase) &&
                    entry.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) &&
                    (sessionId is null || entry.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase)))
                .TakeLast(maximumEntries)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<ConversationHistorySession> ListSessions(
        string workspaceRoot,
        string providerName,
        int maximumSessions = 50)
    {
        if (maximumSessions <= 0)
        {
            return [];
        }

        string normalizedWorkspace = NormalizeWorkspace(workspaceRoot);
        try
        {
            return File.Exists(historyPath)
                ? File.ReadLines(historyPath, Encoding.UTF8)
                    .Select(Parse)
                    .Where(entry => entry is not null)
                    .Select(entry => entry!)
                    .Where(entry => entry.WorkspaceRoot.Equals(normalizedWorkspace, StringComparison.OrdinalIgnoreCase) &&
                        entry.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(entry => entry.SessionId, StringComparer.OrdinalIgnoreCase)
                    .Select(CreateSessionSummary)
                    .OrderByDescending(session => session.LastActivity)
                    .Take(maximumSessions)
                    .ToArray()
                : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public string? GetLatestSessionId(string workspaceRoot, string providerName) =>
        ListSessions(workspaceRoot, providerName, maximumSessions: 1).FirstOrDefault()?.SessionId;

    public string? ResolveSessionId(string workspaceRoot, string providerName, string requestedId)
    {
        if (string.IsNullOrWhiteSpace(requestedId))
        {
            return null;
        }

        IReadOnlyList<ConversationHistorySession> sessions = ListSessions(workspaceRoot, providerName);
        ConversationHistorySession? exact = sessions.FirstOrDefault(session =>
            session.SessionId.Equals(requestedId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.SessionId;
        }

        ConversationHistorySession[] matches = sessions
            .Where(session => session.SessionId.StartsWith(requestedId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1 ? matches[0].SessionId : null;
    }

    public void Append(
        string workspaceRoot,
        string providerName,
        string model,
        string sessionId,
        string role,
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        ConversationHistoryEntry entry = new(
            DateTimeOffset.UtcNow,
            NormalizeWorkspace(workspaceRoot),
            providerName,
            model,
            sessionId,
            role,
            text);
        string line = JsonSerializer.Serialize(entry) + Environment.NewLine;

        lock (writeGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
                File.AppendAllText(historyPath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (IOException)
            {
                // History is best-effort and must not stop an agent turn.
            }
            catch (UnauthorizedAccessException)
            {
                // History is best-effort and must not stop an agent turn.
            }
        }
    }

    public bool Clear(string workspaceRoot, string providerName)
    {
        if (!File.Exists(historyPath))
        {
            return false;
        }

        string normalizedWorkspace = NormalizeWorkspace(workspaceRoot);
        lock (writeGate)
        {
            try
            {
                List<string> remaining = [];
                foreach (string line in File.ReadLines(historyPath, Encoding.UTF8))
                {
                    ConversationHistoryEntry? entry = Parse(line);
                    if (entry is null ||
                        (!entry.WorkspaceRoot.Equals(normalizedWorkspace, StringComparison.OrdinalIgnoreCase) ||
                         !entry.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase)))
                    {
                        remaining.Add(line);
                    }
                }

                File.WriteAllLines(historyPath, remaining, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                DeleteSnapshotsUnsafe(workspaceRoot, providerName, sessionId: null);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public bool DeleteSession(string workspaceRoot, string providerName, string sessionId)
    {
        string normalizedWorkspace = NormalizeWorkspace(workspaceRoot);
        lock (writeGate)
        {
            bool removed = false;
            try
            {
                if (File.Exists(historyPath))
                {
                    List<string> remaining = [];
                    foreach (string line in File.ReadLines(historyPath, Encoding.UTF8))
                    {
                        ConversationHistoryEntry? entry = Parse(line);
                        bool matches = entry is not null &&
                            entry.WorkspaceRoot.Equals(normalizedWorkspace, StringComparison.OrdinalIgnoreCase) &&
                            entry.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) &&
                            entry.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase);
                        if (matches)
                        {
                            removed = true;
                        }
                        else
                        {
                            remaining.Add(line);
                        }
                    }

                    File.WriteAllLines(historyPath, remaining, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }

                removed |= DeleteSnapshotsUnsafe(workspaceRoot, providerName, sessionId);
                return removed;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public void SaveSessionSnapshot(
        string workspaceRoot,
        string providerName,
        string sessionId,
        JsonElement snapshot)
    {
        StoredSessionSnapshot value = new(
            NormalizeWorkspace(workspaceRoot),
            providerName,
            sessionId,
            snapshot.Clone());

        lock (writeGate)
        {
            try
            {
                List<StoredSessionSnapshot> snapshots = LoadSnapshotsUnsafe();
                int index = snapshots.FindIndex(item =>
                    item.WorkspaceRoot.Equals(value.WorkspaceRoot, StringComparison.OrdinalIgnoreCase) &&
                    item.ProviderName.Equals(value.ProviderName, StringComparison.OrdinalIgnoreCase) &&
                    item.SessionId.Equals(value.SessionId, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    snapshots[index] = value;
                }
                else
                {
                    snapshots.Add(value);
                }

                WriteSnapshotsUnsafe(snapshots);
            }
            catch (IOException)
            {
                // Session snapshots are best-effort, just like transcript history.
            }
            catch (UnauthorizedAccessException)
            {
                // Session snapshots are best-effort, just like transcript history.
            }
        }
    }

    public JsonElement? LoadSessionSnapshot(
        string workspaceRoot,
        string providerName,
        string sessionId)
    {
        string normalizedWorkspace = NormalizeWorkspace(workspaceRoot);
        lock (writeGate)
        {
            try
            {
                StoredSessionSnapshot? snapshot = LoadSnapshotsUnsafe().FirstOrDefault(item =>
                    item.WorkspaceRoot.Equals(normalizedWorkspace, StringComparison.OrdinalIgnoreCase) &&
                    item.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) &&
                    item.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase));
                return snapshot?.State.Clone();
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static ConversationHistoryEntry? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ConversationHistoryEntry>(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeWorkspace(string workspaceRoot) =>
        Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static ConversationHistorySession CreateSessionSummary(
        IGrouping<string, ConversationHistoryEntry> group)
    {
        ConversationHistoryEntry[] entries = group.OrderBy(entry => entry.Timestamp).ToArray();
        ConversationHistoryEntry? firstUser = entries.FirstOrDefault(entry => entry.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
        ConversationHistoryEntry last = entries[^1];
        string title = string.IsNullOrWhiteSpace(firstUser?.Text)
            ? "(タイトルなし)"
            : firstUser.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (title.Length > 80)
        {
            title = title[..80] + "…";
        }

        return new ConversationHistorySession(
            group.Key,
            last.Timestamp,
            entries.Length,
            last.Model,
            title);
    }

    private List<StoredSessionSnapshot> LoadSnapshotsUnsafe()
    {
        if (!File.Exists(sessionSnapshotPath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<StoredSessionSnapshot>>(
                       File.ReadAllText(sessionSnapshotPath, Encoding.UTF8)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteSnapshotsUnsafe(List<StoredSessionSnapshot> snapshots)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(sessionSnapshotPath)!);
        File.WriteAllText(
            sessionSnapshotPath,
            JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = false }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private bool DeleteSnapshotsUnsafe(string workspaceRoot, string providerName, string? sessionId)
    {
        List<StoredSessionSnapshot> snapshots = LoadSnapshotsUnsafe();
        string normalizedWorkspace = NormalizeWorkspace(workspaceRoot);
        int originalCount = snapshots.Count;
        snapshots.RemoveAll(item =>
            item.WorkspaceRoot.Equals(normalizedWorkspace, StringComparison.OrdinalIgnoreCase) &&
            item.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) &&
            (sessionId is null || item.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase)));
        if (snapshots.Count != originalCount)
        {
            WriteSnapshotsUnsafe(snapshots);
            return true;
        }

        return false;
    }

    private sealed record StoredSessionSnapshot(
        string WorkspaceRoot,
        string ProviderName,
        string SessionId,
        JsonElement State);
}

public sealed record ConversationHistoryEntry(
    DateTimeOffset Timestamp,
    string WorkspaceRoot,
    string ProviderName,
    string Model,
    string SessionId,
    string Role,
    string Text);

public sealed record ConversationHistorySession(
    string SessionId,
    DateTimeOffset LastActivity,
    int MessageCount,
    string Model,
    string Title);
