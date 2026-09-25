using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AegisAgent.Core;

public sealed class CodingWorkspace
{
    private static readonly Regex DangerousCommand = new(
        @"(?ix)(\bformat\b|\bshutdown\b|\breboot\b|\bRemove-Item\b.*(-Recurse|-Force)|\bdel\b.*(/s|/q)|\brm\b.*-[a-z]*r|\bgit\s+(reset\s+--hard|clean\s+-f|checkout\s+--|restore\s+\.))",
        RegexOptions.Compiled);

    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private bool autoApprove;

    public CodingWorkspace(string rootPath, bool autoApprove)
    {
        RootPath = Path.GetFullPath(rootPath);
        this.autoApprove = autoApprove;
    }

    public string RootPath { get; }

    public AITool[] CreateTools() =>
    [
        AIFunctionFactory.Create(ListFiles),
        AIFunctionFactory.Create(ReadFile),
        AIFunctionFactory.Create(SearchCode),
        AIFunctionFactory.Create(GetGitStatus),
        AIFunctionFactory.Create(WriteFile),
        AIFunctionFactory.Create(ReplaceInFile),
        AIFunctionFactory.Create(RunCommand),
    ];

    [Description("List files and directories in the workspace. Use this before reading or editing unfamiliar code.")]
    public string ListFiles(
        [Description("Workspace-relative directory path. Use '.' for the workspace root.")] string path = ".",
        [Description("Maximum directory depth to traverse.")] int maxDepth = 2,
        [Description("Maximum number of entries to return.")] int maxEntries = 200)
    {
        string directory = ResolvePath(path, mustExist: true);
        if (!Directory.Exists(directory))
        {
            return $"Not a directory: {path}";
        }

        maxDepth = Math.Clamp(maxDepth, 0, 6);
        maxEntries = Math.Clamp(maxEntries, 1, 500);
        List<string> entries = [];
        VisitDirectory(directory, 0, maxDepth, maxEntries, entries);
        return entries.Count == 0 ? "(empty)" : string.Join(Environment.NewLine, entries);
    }

    [Description("Read a text file with line numbers. Always inspect relevant code before editing it.")]
    public string ReadFile(
        [Description("Workspace-relative file path.")] string path,
        [Description("One-based first line number.")] int startLine = 1,
        [Description("Maximum number of lines to return.")] int maxLines = 400)
    {
        string file = ResolvePath(path, mustExist: true);
        if (!File.Exists(file))
        {
            return $"Not a file: {path}";
        }

        startLine = Math.Max(1, startLine);
        maxLines = Math.Clamp(maxLines, 1, 1_000);

        try
        {
            string[] lines = File.ReadAllLines(file);
            if (startLine > lines.Length)
            {
                return $"File has {lines.Length} lines; startLine {startLine} is past the end.";
            }

            int end = Math.Min(lines.Length, startLine - 1 + maxLines);
            StringBuilder result = new();
            for (int index = startLine - 1; index < end; index++)
            {
                result.Append(index + 1).Append(": ").AppendLine(lines[index]);
            }

            if (end < lines.Length)
            {
                result.AppendLine($"... truncated; read from line {end + 1} to continue ...");
            }

            return result.ToString();
        }
        catch (IOException exception)
        {
            return $"Unable to read '{path}': {exception.Message}";
        }
    }

    [Description("Search source files for a text or regular expression and return matching file and line locations.")]
    public async Task<string> SearchCode(
        [Description("Text or regular expression to search for.")] string query,
        [Description("File glob such as '*.cs' or '*'.")] string glob = "*",
        [Description("Maximum number of matching lines.")] int maxResults = 100)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "The search query cannot be empty.";
        }

        maxResults = Math.Clamp(maxResults, 1, 500);
        try
        {
            ProcessResult result = await RunProcessAsync(
                "rg",
                ["--line-number", "--no-heading", "--color", "never", "--hidden", "--glob", glob, "--glob", "!.git", "--", query, RootPath],
                RootPath,
                TimeSpan.FromSeconds(30));

            if (result.ExitCode is 0 or 1)
            {
                string[] lines = result.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                return lines.Length == 0 ? "No matches." : string.Join(Environment.NewLine, lines.Take(maxResults));
            }
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            // Fall through to the managed implementation when ripgrep is unavailable.
        }

        return ManagedSearch(query, glob, maxResults);
    }

    [Description("Show the current git branch and working tree status.")]
    public string GetGitStatus()
    {
        try
        {
            ProcessResult result = RunProcessAsync(
                    "git",
                    ["status", "--short", "--branch", "--untracked-files=all"],
                    RootPath,
                    TimeSpan.FromSeconds(15))
                .GetAwaiter()
                .GetResult();

            return result.ExitCode == 0
                ? (string.IsNullOrWhiteSpace(result.StandardOutput) ? "Working tree is clean." : result.StandardOutput.Trim())
                : $"git status failed: {result.StandardError.Trim()}";
        }
        catch (Exception exception)
        {
            return $"Unable to run git status: {exception.Message}";
        }
    }

    [Description("Create or replace a workspace file. Requires user approval unless --auto-approve was used.")]
    public async Task<string> WriteFile(
        [Description("Workspace-relative file path.")] string path,
        [Description("Complete UTF-8 text content to write.")] string content)
    {
        string file = ResolvePath(path);
        if (content.Length > 500_000)
        {
            return "Refused: content is larger than 500,000 characters. Use a focused edit instead.";
        }

        await mutationGate.WaitAsync();
        try
        {
            if (!await ConfirmAsync($"write {RelativePath(file)} ({content.Length} chars)"))
            {
                return "User declined the file write.";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return $"Wrote {RelativePath(file)}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Unable to write '{path}': {exception.Message}";
        }
        finally
        {
            mutationGate.Release();
        }
    }

    [Description("Replace one exact text occurrence in a workspace file. Requires user approval unless --auto-approve was used.")]
    public async Task<string> ReplaceInFile(
        [Description("Workspace-relative file path.")] string path,
        [Description("Exact existing text to replace.")] string oldText,
        [Description("Replacement text.")] string newText)
    {
        string file = ResolvePath(path, mustExist: true);
        if (!File.Exists(file))
        {
            return $"Not a file: {path}";
        }

        await mutationGate.WaitAsync();
        try
        {
            string current = await File.ReadAllTextAsync(file);
            int occurrenceCount = CountOccurrences(current, oldText);
            if (occurrenceCount == 0)
            {
                return "The exact oldText was not found; no changes were made.";
            }

            if (occurrenceCount > 1)
            {
                return $"Refused: oldText occurs {occurrenceCount} times. Include more context so the edit is unambiguous.";
            }

            if (!await ConfirmAsync($"edit {RelativePath(file)} (replace one exact occurrence)"))
            {
                return "User declined the file edit.";
            }

            string updated = current.Replace(oldText, newText, StringComparison.Ordinal);
            await File.WriteAllTextAsync(file, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return $"Updated {RelativePath(file)}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Unable to edit '{path}': {exception.Message}";
        }
        finally
        {
            mutationGate.Release();
        }
    }

    [Description("Run a focused repository-local command such as dotnet test or dotnet build. Requires user approval unless --auto-approve was used.")]
    public async Task<string> RunCommand(
        [Description("PowerShell command to run from the workspace root.")] string command,
        [Description("Timeout in seconds.")] int timeoutSeconds = 120)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "The command cannot be empty.";
        }

        if (DangerousCommand.IsMatch(command))
        {
            return "Refused: command matches a destructive-command safety rule.";
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 600);
        await mutationGate.WaitAsync();
        try
        {
            if (!await ConfirmAsync($"run command: {command}"))
            {
                return "User declined the command.";
            }

            string shell = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command]
                : ["-lc", command];
            ProcessResult result = await RunProcessAsync(shell, arguments, RootPath, TimeSpan.FromSeconds(timeoutSeconds));
            return FormatProcessResult(result);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private async Task<bool> ConfirmAsync(string action)
    {
        if (autoApprove)
        {
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"\n[approval] {action} ? [y]es / [n]o / [a]lways: ");
        Console.ResetColor();
        string? answer = await Task.Run(Console.ReadLine);
        if (answer?.Equals("a", StringComparison.OrdinalIgnoreCase) == true || answer?.Equals("always", StringComparison.OrdinalIgnoreCase) == true)
        {
            autoApprove = true;
            return true;
        }

        return answer?.Equals("y", StringComparison.OrdinalIgnoreCase) == true || answer?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
    }

    private string ResolvePath(string path, bool mustExist = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be empty.", nameof(path));
        }

        string fullPath = Path.GetFullPath(Path.Combine(RootPath, path));
        string rootWithSeparator = RootPath.EndsWith(Path.DirectorySeparatorChar)
            ? RootPath
            : RootPath + Path.DirectorySeparatorChar;

        if (!fullPath.Equals(RootPath, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Path escapes the workspace root.");
        }

        if (mustExist && !File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException($"Path does not exist: {path}");
        }

        return fullPath;
    }

    private void VisitDirectory(string current, int depth, int maxDepth, int maxEntries, List<string> output)
    {
        if (output.Count >= maxEntries)
        {
            return;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(current)
                .Where(path => !IsIgnoredDirectory(Path.GetFileName(path)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            output.Add($"{RelativePath(current)}/ [access denied]");
            return;
        }

        foreach (string child in children)
        {
            if (output.Count >= maxEntries)
            {
                output.Add("... truncated ...");
                return;
            }

            bool isDirectory = Directory.Exists(child);
            string relative = RelativePath(child);
            output.Add(isDirectory ? $"{relative}/" : relative);
            if (isDirectory && depth < maxDepth)
            {
                VisitDirectory(child, depth + 1, maxDepth, maxEntries, output);
            }
        }
    }

    private string ManagedSearch(string query, string glob, int maxResults)
    {
        Regex? expression = null;
        try
        {
            expression = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException)
        {
            // Treat invalid regex input as a literal search.
        }

        List<string> matches = [];
        foreach (string file in Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories))
        {
            string? parentName = Path.GetDirectoryName(file) is { } parent ? Path.GetFileName(parent) : null;
            if (matches.Count >= maxResults || (parentName is not null && IsIgnoredDirectory(parentName)) || !MatchesGlob(Path.GetFileName(file), glob))
            {
                continue;
            }

            try
            {
                int lineNumber = 0;
                foreach (string line in File.ReadLines(file))
                {
                    lineNumber++;
                    if ((expression is not null && expression.IsMatch(line)) || (expression is null && line.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    {
                        matches.Add($"{RelativePath(file)}:{lineNumber}:{line.Trim()}");
                        if (matches.Count >= maxResults)
                        {
                            break;
                        }
                    }
                }
            }
            catch (IOException)
            {
                // Ignore binary or locked files during a broad search.
            }
        }

        return matches.Count == 0 ? "No matches." : string.Join(Environment.NewLine, matches);
    }

    private static bool MatchesGlob(string fileName, string glob)
    {
        string pattern = "^" + Regex.Escape(string.IsNullOrWhiteSpace(glob) ? "*" : glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsIgnoredDirectory(string name) => name is ".git" or ".vs" or "bin" or "obj" or "node_modules" or ".venv";

    private string RelativePath(string path) => Path.GetRelativePath(RootPath, path).Replace(Path.DirectorySeparatorChar, '/');

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        process.Start();
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource cancellation = new(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process already exited.
            }

            return new ProcessResult(-1, await outputTask, $"Timed out after {timeout.TotalSeconds:0} seconds.");
        }

        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static string FormatProcessResult(ProcessResult result)
    {
        StringBuilder output = new();
        output.AppendLine($"Exit code: {result.ExitCode}");
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            output.AppendLine("--- stdout ---");
            output.AppendLine(result.StandardOutput.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            output.AppendLine("--- stderr ---");
            output.AppendLine(result.StandardError.TrimEnd());
        }

        return output.ToString().TrimEnd();
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
