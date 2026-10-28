using BusinessHarness.Core;

namespace BusinessHarness.Application;

/// <summary>Application policy; knows nothing about HTTP, Git processes, or a CI vendor.</summary>
public sealed class ReviewUseCase(IChangeSource changeSource, IReviewProvider? provider,
    IIssueSource? issueSource, IReportWriter writer, TimeProvider timeProvider,
    IReferenceResolver? referenceResolver = null, IChangeRequestHost? changeRequestHost = null, string feedback = "none",
    IReviewMemory? memory = null)
{
    public async Task<HarnessReport> ExecuteAsync(string mode, string baseRef, string headRef,
        IReadOnlyList<string> issueKeys, HarnessPolicy policy, string outputDirectory, CancellationToken cancellationToken)
    {
        policy.Validate();
        if (feedback is not ("none" or "comment" or "auto") ||
            (feedback != "none" && (mode != "review" || changeRequestHost is null)))
            throw new HarnessException(ErrorCode.Configuration, "Invalid feedback configuration.", "Use review with a change request host and feedback none, comment or auto.");
        if (mode is not ("scan" or "review"))
            throw new HarnessException(ErrorCode.Configuration, "Unknown execution mode.", "Use scan or review.");
        if (mode == "scan" && issueKeys.Count > 0)
            throw new HarnessException(ErrorCode.Configuration, "Offline scan cannot validate external references.", "Use review with configured reference sources.");
        if (mode == "review" && provider is null)
            throw new HarnessException(ErrorCode.Configuration, "No review provider configured.", "Configure an API provider and model.");
        if (issueKeys.Count > 0 && issueSource is null && referenceResolver is null)
            throw new HarnessException(ErrorCode.Configuration, "Jira is not configured.", "Set jiraSite and JIRA_EMAIL / JIRA_API_TOKEN.");
        var context = mode == "review" && changeRequestHost is not null ? await changeRequestHost.ReadAsync(cancellationToken) : null;
        // Use the host's target/base to prevent approval of a caller-selected, incomplete diff.
        var changes = await changeSource.ReadChangesAsync(context?.BaseCommit ?? baseRef, headRef, cancellationToken);
        if (context is not null && (!context.Open || context.HeadCommit != changes.HeadCommit))
            throw new HarnessException(ErrorCode.Configuration, "Change request head does not match the reviewed checkout or is closed.", "Fetch the source head commit (not a synthetic merge commit) and rerun on an open change request.");
        changes = changes with { ChangeRequest = context };
        IReadOnlyList<ReferenceDocument> issues;
        if (mode == "review" && referenceResolver is not null)
            issues = await referenceResolver.ResolveAsync(context is null ? "" : context.Title + "\n" + context.Description, issueKeys, cancellationToken);
        else
        {
            List<ReferenceDocument> explicitIssues = [];
            foreach (var key in issueKeys.Distinct(StringComparer.Ordinal))
                explicitIssues.Add(await issueSource!.GetIssueAsync(key, cancellationToken));
            issues = explicitIssues;
        }

        var query = new MemoryQuery(string.Join('\n', new[] { context?.Title ?? "Repository review", context?.Description ?? "" }
            .Concat(issues.Select(i => i.Key + " " + i.Summary)).Concat(changes.Files.Select(f => f.Path))), issues.Select(i => i.Key).ToArray());
        if (mode == "review" && memory is not null)
            changes = changes with { Memories = await memory.RecallAsync(query, cancellationToken) };
        var result = mode == "scan"
            ? new ReviewResult("Offline credential-pattern scan only; no AI or requirements review performed.", [], [])
            : await provider!.ReviewAsync(changes, issues, cancellationToken);
        ReviewValidation.Validate(result, changes, issues);
        var findings = changes.SecurityFindings.Concat(result.Findings).ToArray();
        var passed = !findings.Any(f => Severity.Blocks(f.Severity, policy.FailOn)) &&
            result.Requirements.All(r => r.Status == "met");
        var report = new HarnessReport("1.0", mode, mode == "scan" ? "local" : policy.Provider,
            mode == "scan" ? null : policy.Model, changes.BaseCommit, changes.HeadCommit, timeProvider.GetUtcNow(),
            passed, result.Summary, changes.Files.Select(f => f.Path).ToArray(), changes.ExcludedFiles,
            findings, result.Requirements);
        await writer.WriteAsync(report, outputDirectory, cancellationToken);
        if (mode == "review" && memory is not null)
            await memory.RememberReviewAsync(query, report, cancellationToken);
        if (feedback != "none")
            await changeRequestHost!.PublishAsync(context!, report, feedback, cancellationToken);
        return report;
    }
}

public static class ReviewValidation
{
    public static void Validate(ReviewResult result, ChangeSet changes, IReadOnlyList<ReferenceDocument> issues)
    {
        if (result.Summary is null || result.Summary.Length is < 1 or > 8000 ||
            result.Findings is null || result.Requirements is null || result.Findings.Count > 100 || result.Requirements.Count > 20)
            throw Invalid();
        foreach (var finding in result.Findings)
        {
            if (finding is null || !Severity.IsValid(finding.Severity) ||
                finding.Category is not ("security" or "maintainability" or "correctness") || finding.RuleId is null ||
                finding.RuleId.Length is < 1 or > 64 || !finding.RuleId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
                finding.Title is null || finding.Title.Length is < 1 or > 200 ||
                finding.Description is null || finding.Description.Length is < 1 or > 8000)
                throw Invalid();
            var file = changes.Files.SingleOrDefault(f => f.Path == finding.Path);
            if (file is null || !file.AddedLines.Contains(finding.Line)) throw Invalid();
        }
        if (result.Requirements.Count != issues.Count ||
            result.Requirements.Select(r => r?.IssueKey).Distinct(StringComparer.Ordinal).Count() != issues.Count)
            throw Invalid();
        foreach (var requirement in result.Requirements)
            if (requirement is null || !issues.Any(i => i.Key == requirement.IssueKey) ||
                requirement.Status is not ("met" or "partial" or "missing" or "not_verifiable") ||
                requirement.Evidence is null || requirement.Evidence.Length is < 1 or > 8000)
                throw Invalid();
    }

    private static HarnessException Invalid() => new(ErrorCode.ProviderResponse,
        "Review output did not pass validation.",
        "Use a model supporting structured outputs. Findings must reference added lines; each resolved reference needs one assessment.");
}
