using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using BusinessHarness.Application;
using BusinessHarness.Core;

namespace BusinessHarness.Infrastructure;

public sealed partial class GitRepository(string directory, HarnessPolicy policy, SecretRedactor redactor) : IChangeSource
{
    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HunkPattern();

    public async Task<ChangeSet> ReadChangesAsync(string baseRef, string headRef, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) throw GitError();
        var baseCommit = await ResolveAsync(baseRef, cancellationToken);
        var headCommit = await ResolveAsync(headRef, cancellationToken);
        var mergeBase = (await RunAsync(["merge-base", baseCommit, headCommit], 256, cancellationToken)).Trim();
        if (!IsCommit(mergeBase)) throw GitError();
        var names = await RunAsync(["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--raw", "-z", mergeBase, headCommit, "--"], 1_000_000, cancellationToken);
        var records = names.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (records.Length % 2 != 0) throw GitError();
        var paths = records.Where((_, index) => index % 2 == 1).ToArray();
        if (paths.Length > policy.MaxFiles) throw Limit("Changed-file limit exceeded.");
        for (var index = 0; index < records.Length; index += 2)
        {
            var fields = records[index].Split(' ');
            if (fields.Length != 5 || !fields[0].StartsWith(':')) throw GitError();
            if (fields[0][1..] is "120000" or "160000" || fields[1] is "120000" or "160000")
                throw new HarnessException(ErrorCode.Git, "Symlink or submodule changes cannot be reviewed as text.",
                    "Review link targets and submodule commits separately. The runner does not traverse or execute them.");
        }
        List<FilePatch> files = [];
        List<string> excluded = [];
        List<Finding> findings = [];
        var totalBytes = 0;
        foreach (var path in paths)
        {
            if (!SafePaths.IsRepositoryPath(path) || redactor.ContainsSecret(path))
                throw new HarnessException(ErrorCode.Git, "Unsupported repository filename.", "Rename files containing control characters, backslashes, colons, or unsafe path segments.");
            if (SafePaths.IsExcluded(path))
            {
                excluded.Add(path);
                findings.Add(new("BH002", "high", path, 1, "Sensitive or excluded file changed",
                    "Content was withheld by filename policy. Inspect this change privately before merging.")
                { Category = "security" });
                continue;
            }
            var patch = await RunAsync(["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--no-color", "--unified=3", mergeBase, headCommit, "--", ":(literal)" + path], policy.MaxFileDiffBytes, cancellationToken);
            totalBytes += Encoding.UTF8.GetByteCount(patch);
            if (totalBytes > policy.MaxDiffBytes) throw Limit("Total diff limit exceeded.");
            if (patch.Contains("Binary files ", StringComparison.Ordinal) || patch.Contains("GIT binary patch", StringComparison.Ordinal) ||
                patch.Contains("file mode 120000", StringComparison.Ordinal) || patch.Contains("file mode 160000", StringComparison.Ordinal) ||
                patch.Contains("mode 100644,120000", StringComparison.Ordinal))
                throw new HarnessException(ErrorCode.Git, "A binary, symlink, or submodule change cannot be reviewed as text.",
                    "Review this change separately. The runner does not silently skip unsupported content.");
            var line = 0;
            HashSet<int> added = [];
            foreach (var text in patch.Split('\n'))
            {
                var match = HunkPattern().Match(text);
                if (match.Success) { line = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture); continue; }
                if (line == 0 || text.StartsWith("\\ No newline", StringComparison.Ordinal)) continue;
                if (text.StartsWith('+'))
                {
                    added.Add(line);
                    if (redactor.ContainsSecret(text)) findings.Add(new("BH001", "critical", path, line,
                        "Possible embedded secret", "An added line contains a credential-like value. Remove it and rotate any real credential.")
                    { Category = "security" });
                    line++;
                }
                else if (!text.StartsWith('-')) line++;
            }
            files.Add(new(path, redactor.Redact(patch), added));
        }
        return new(mergeBase, headCommit, files, excluded, findings);
    }

    private async Task<string> ResolveAsync(string reference, CancellationToken ct)
    {
        if (reference.Length is < 1 or > 256 || reference.Any(char.IsControl) || reference.StartsWith('-'))
            throw new HarnessException(ErrorCode.Configuration, "Invalid Git reference.", "Pass a commit SHA or a valid local ref through a quoted CLI argument.");
        var commit = (await RunAsync(["rev-parse", "--verify", "--end-of-options", reference + "^{commit}"], 256, ct)).Trim();
        return IsCommit(commit) ? commit : throw GitError();
    }

    private static bool IsCommit(string text) => text.Length is 40 or 64 && text.All(Uri.IsHexDigit);

    private async Task<string> RunAsync(IEnumerable<string> arguments, int maxBytes, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path.GetFullPath(directory),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var allowed = new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "TMPDIR" };
        var environment = allowed.Select(key => (key, value: Environment.GetEnvironmentVariable(key))).ToArray();
        start.Environment.Clear();
        foreach (var (key, value) in environment) if (value is not null) start.Environment[key] = value;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_PROTOCOL_FROM_USER"] = "0";
        // Older serviced Git releases do not understand GIT_NO_LAZY_FETCH. Deny transports too.
        foreach (var protocol in new[] { "allow", "http.allow", "https.allow", "ssh.allow", "git.allow", "file.allow", "ext.allow" })
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("protocol." + protocol + "=never");
        }
        foreach (var item in new[] { "-c", "core.quotepath=false", "-c", "core.pager=cat", "-c", "core.fsmonitor=false", "-c", "safe.directory=" + Path.GetFullPath(directory) }) start.ArgumentList.Add(item);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start) ?? throw GitError(); }
        catch (System.ComponentModel.Win32Exception) { throw new HarnessException(ErrorCode.Git, "Git executable could not start.", "Install Git and make sure it is on PATH."); }
        using (process)
        {
            using var registration = ct.Register(Kill);
            try
            {
                var output = ReadBoundedAsync(process.StandardOutput, maxBytes, ct, Kill);
                var error = ReadBoundedAsync(process.StandardError, 8192, ct, Kill);
                await Task.WhenAll(output, error, process.WaitForExitAsync(ct));
                if (process.ExitCode != 0) throw GitError();
                return await output;
            }
            finally { Kill(); }

            void Kill()
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxBytes, CancellationToken ct, Action kill)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        var bytes = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            bytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
            if (bytes > maxBytes) { kill(); throw Limit("Git output limit exceeded."); }
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
    }

    private static HarnessException GitError() => new(ErrorCode.Git, "Git could not resolve or compare the requested commits.",
        "Check --repo, --base and --head. Fetch both commits and their common history (disable shallow checkout). No fetch is performed by the runner.");
    private static HarnessException Limit(string message) => new(ErrorCode.LimitExceeded, message,
        "No partial review was performed. Split the change or increase limits in trusted policy.");
}
