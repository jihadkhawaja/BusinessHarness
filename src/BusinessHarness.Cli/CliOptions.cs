using BusinessHarness.Core;

namespace BusinessHarness.Cli;

public sealed record CliOptions(string Command, string Repository, string BaseRef, string HeadRef,
    string OutputDirectory, string? ConfigPath, string[] IssueKeys, string? ChangeUrl = null, string Feedback = "none",
    string? MemoryScope = null, string? MemoryDirectory = null, string? SessionId = null, string? EventPath = null, string? Query = null,
    string? CommentTemplatePath = null, string[]? Skills = null)
{
    public static CliOptions Parse(string[] args, Func<string, string?> environment)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h" or "help") return new("help", "", "", "", "", null, []);
        if (args.Length == 1 && args[0] == "--version") return new("version", "", "", "", "", null, []);
        if (args.Length == 0 || args[0] is not ("review" or "scan" or "validate" or "follow-up" or "memory-search"))
            throw Usage("A valid command is required.");
        var allowed = new[] { "--repo", "--base", "--head", "--output", "--config", "--issue", "--reference", "--change", "--feedback", "--comment-template", "--skills", "--memory-scope", "--memory-directory", "--session", "--event", "--query" };
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (!allowed.Contains(args[i], StringComparer.Ordinal) || i + 1 >= args.Length ||
                string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(args[i], args[i + 1]))
                throw Usage("Unknown, repeated or incomplete option.");
        }
        var baseRef = values.GetValueOrDefault("--base") ?? environment("HARNESS_BASE");
        if (args[0] is "review" or "scan" && string.IsNullOrWhiteSpace(baseRef)) throw Usage("The base commit is required.");
        if (args[0] == "follow-up" && !values.ContainsKey("--event")) throw Usage("Follow-up requires --event PATH.");
        if (args[0] == "memory-search" && !values.ContainsKey("--query")) throw Usage("Memory search requires --query TEXT.");
        if (values.TryGetValue("--memory-scope", out var scope) && scope != "off" && !MemoryPolicy.IsScope(scope)) throw Usage("Use memory scope off, global, repository or session.");
        if (values.ContainsKey("--event") && args[0] is not ("review" or "follow-up")) throw Usage("Events require review or follow-up.");
        if (values.ContainsKey("--query") && args[0] != "memory-search") throw Usage("Query is only supported by memory-search.");
        if (values.ContainsKey("--comment-template") && args[0] is not ("review" or "validate")) throw Usage("Comment templates require review or validate.");
        string[]? skills = null;
        if (values.TryGetValue("--skills", out var selection))
        {
            if (args[0] is not ("review" or "validate")) throw Usage("Skills require review or validate.");
            skills = selection == "none" ? [] : selection.Split(',', StringSplitOptions.TrimEntries);
            if (skills.Length > ReviewSkill.MaxCount || skills.Any(s => !ReviewSkill.IsName(s)) ||
                skills.Distinct(StringComparer.Ordinal).Count() != skills.Length)
                throw Usage("Select unique comma-separated skill names, or none.");
        }
        var issues = string.Join(',', values.GetValueOrDefault("--issue") ?? "", values.GetValueOrDefault("--reference") ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (issues.Length > 20) throw Usage("At most 20 references are supported per run.");
        var feedback = values.GetValueOrDefault("--feedback") ?? "none";
        if (feedback is not ("none" or "comment" or "auto")) throw Usage("Feedback must be none, comment or auto.");
        if (feedback != "none" && args[0] is not ("review" or "validate")) throw Usage("Feedback is only supported by review.");
        if (args[0] == "scan" && (issues.Length > 0 || values.ContainsKey("--change") || feedback != "none"))
            throw Usage("Offline scan cannot fetch references or publish feedback.");
        return new(args[0], Path.GetFullPath(values.GetValueOrDefault("--repo") ?? "."), baseRef ?? "",
            values.GetValueOrDefault("--head") ?? environment("HARNESS_HEAD") ?? "HEAD",
            Path.GetFullPath(values.GetValueOrDefault("--output") ?? "harness-results"), values.GetValueOrDefault("--config"), issues,
            values.GetValueOrDefault("--change"), feedback, values.GetValueOrDefault("--memory-scope"), values.GetValueOrDefault("--memory-directory"),
            values.GetValueOrDefault("--session"), values.GetValueOrDefault("--event"), values.GetValueOrDefault("--query"), values.GetValueOrDefault("--comment-template"), skills);
    }

    private static HarnessException Usage(string message) => new(ErrorCode.Configuration, message,
        "Run businessharness --help. Example: businessharness scan --base origin/main --head HEAD.");
}
