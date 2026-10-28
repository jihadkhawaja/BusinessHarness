namespace BusinessHarness.Core;

public sealed record HarnessPolicy
{
    public string Provider { get; init; } = "openai";
    public string? Model { get; init; }
    public string FailOn { get; init; } = "high";
    public int MaxFiles { get; init; } = 60;
    public int MaxDiffBytes { get; init; } = 200_000;
    public int MaxFileDiffBytes { get; init; } = 64_000;
    public int TimeoutSeconds { get; init; } = 120;
    public int MaxOutputTokens { get; init; } = 6000;
    public string? JiraSite { get; init; }
    public string? JiraAcceptanceCriteriaField { get; init; }
    public List<ReferenceSourcePolicy> ReferenceSources { get; init; } = [];
    public ChangeRequestPolicy? ChangeRequest { get; init; }
    public MemoryPolicy? Memory { get; init; }

    public void Validate()
    {
        if (ReferenceSources is null || ReferenceSources.Count > 20 ||
            ReferenceSources.Any(s => s is null) ||
            ReferenceSources.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != ReferenceSources.Count)
            throw new HarnessException(ErrorCode.Configuration, "Invalid reference sources.", "Configure at most 20 uniquely named sources in trusted policy.");
        foreach (var source in ReferenceSources) source.Validate();
        ChangeRequest?.Validate();
        Memory?.Validate();
        if (Provider is not ("openai" or "anthropic"))
            throw new HarnessException(ErrorCode.Configuration, "Unknown review provider.", "Set provider to openai or anthropic in trusted policy.");
        if (!Severity.IsValid(FailOn))
            throw new HarnessException(ErrorCode.Configuration, "Invalid failOn severity.", "Use info, low, medium, high, or critical.");
        if (MaxFiles is < 1 or > 500 || MaxDiffBytes is < 1000 or > 1_000_000 ||
            MaxFileDiffBytes < 1000 || MaxFileDiffBytes > MaxDiffBytes ||
            TimeoutSeconds is < 5 or > 600 || MaxOutputTokens is < 256 or > 32_000)
            throw new HarnessException(ErrorCode.Configuration, "Policy limits are outside the supported range.", "See docs/configuration.md for allowed limits.");
        if (Model is not null && (Model.Length is < 1 or > 120 || Model.Any(char.IsControl)))
            throw new HarnessException(ErrorCode.Configuration, "Invalid model identifier.", "Set model to an API model supporting structured outputs.");
        if (JiraAcceptanceCriteriaField is not null &&
            (!JiraAcceptanceCriteriaField.StartsWith("customfield_", StringComparison.Ordinal) ||
             JiraAcceptanceCriteriaField.Length is < 13 or > 32 || !JiraAcceptanceCriteriaField[12..].All(char.IsAsciiDigit)))
            throw new HarnessException(ErrorCode.Configuration, "Invalid Jira acceptance criteria field.", "Use a custom field ID such as customfield_10001.");
    }
}

public static class Severity
{
    private static readonly string[] Values = ["info", "low", "medium", "high", "critical"];
    public static bool IsValid(string value) => Array.IndexOf(Values, value) >= 0;
    public static bool Blocks(string value, string threshold) =>
        Array.IndexOf(Values, value) >= Array.IndexOf(Values, threshold);
}

public sealed record Finding(string RuleId, string Severity, string Path, int Line, string Title, string Description)
{
    public string Category { get; init; } = "correctness";
}
public sealed record RequirementAssessment(string IssueKey, string Status, string Evidence);
public sealed record ReviewResult(string Summary, List<Finding> Findings, List<RequirementAssessment> Requirements);
public sealed record FilePatch(string Path, string Patch, HashSet<int> AddedLines);
public sealed record ChangeSet(string BaseCommit, string HeadCommit, List<FilePatch> Files,
    List<string> ExcludedFiles, List<Finding> SecurityFindings)
{
    public ChangeRequestContext? ChangeRequest { get; init; }
    public IReadOnlyList<ReviewMemoryEntry> Memories { get; init; } = [];
}
public sealed record ReferenceDocument(string Key, string Summary, string Description, string AcceptanceCriteria = "");
public sealed record HarnessReport(string SchemaVersion, string Mode, string Provider, string? Model,
    string BaseCommit, string HeadCommit, DateTimeOffset GeneratedAt, bool Passed, string Summary,
    IReadOnlyList<string> ReviewedFiles, IReadOnlyList<string> ExcludedFiles,
    IReadOnlyList<Finding> Findings, IReadOnlyList<RequirementAssessment> Requirements);
