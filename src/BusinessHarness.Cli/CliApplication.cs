using System.Text.Json;
using BusinessHarness.Application;
using BusinessHarness.Core;
using BusinessHarness.Infrastructure;

namespace BusinessHarness.Cli;

/// <summary>Composition root and single error boundary. Exceptions never leak raw API/OS messages.</summary>
public sealed class CliApplication(Func<string, string?>? environment = null)
{
    private readonly Func<string, string?> environment = environment ?? Environment.GetEnvironmentVariable;

    public async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        string? outputDirectory = null;
        var stage = "arguments";
        var correlationId = Guid.NewGuid().ToString("N");
        try
        {
            var options = CliOptions.Parse(args, environment);
            if (options.Command == "help") { await output.WriteLineAsync(Help); return 0; }
            if (options.Command == "version") { await output.WriteLineAsync("BusinessHarness 0.1.0"); return 0; }
            stage = "reports";
            if (options.Command is not ("follow-up" or "memory-search"))
            {
                ReportWriter.Prepare(options.OutputDirectory);
                outputDirectory = options.OutputDirectory;
            }
            stage = "configuration";
            var policy = await LoadPolicyAsync(options.ConfigPath, cancellationToken);
            policy = policy with
            {
                Provider = NonEmptyEnvironment("HARNESS_PROVIDER") ?? policy.Provider,
                Model = NonEmptyEnvironment("HARNESS_MODEL") ?? policy.Model,
                JiraSite = NonEmptyEnvironment("HARNESS_JIRA_SITE") ?? policy.JiraSite,
                EnabledSkills = options.Skills?.ToList() ?? policy.EnabledSkills
            };
            policy = policy with { Memory = await MemorySettings.ResolveAsync(policy.Memory, options, environment, cancellationToken) };
            policy.Validate();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(policy.TimeoutSeconds));
            var commentTemplate = options.CommentTemplatePath is null ? policy.ReviewCommentTemplate :
                await LoadCommentTemplateAsync(options.CommentTemplatePath, timeout.Token);
            ReviewCommentTemplate.Validate(commentTemplate);
            var changeRequest = options.Command == "scan" ? null : await ChangeRequestOptions.ResolveAsync(options.ChangeUrl, policy.ChangeRequest, environment, timeout.Token);
            if (options.Feedback != "none" && changeRequest is null)
                throw new HarnessException(ErrorCode.Configuration, "Feedback requires a pull/merge request.", "Use --change URL, trusted changeRequest policy or GitHub/GitLab CI metadata.");
            foreach (var reference in options.IssueKeys)
                if (!JiraIssueSource.IsIssueKey(reference) && !policy.ReferenceSources.Any(s => reference.StartsWith(s.Name + ":", StringComparison.Ordinal) && reference.Length > s.Name.Length + 1))
                    throw new HarnessException(ErrorCode.Configuration, "Unknown reference.", "Configure a reference source and use SOURCE:ID, or supply a Jira key.");
            if (options.Command == "scan" && options.IssueKeys.Length > 0)
                throw new HarnessException(ErrorCode.Configuration, "Offline scan cannot validate external references.", "Use review with configured reference sources.");
            var secretNames = new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "JIRA_API_TOKEN", "GITHUB_TOKEN", "GITLAB_TOKEN", "SYSTEM_ACCESSTOKEN", "CI_JOB_TOKEN", "BITBUCKET_ACCESS_TOKEN" }
                .Concat(policy.ReferenceSources.Select(s => s.TokenEnvironment).OfType<string>())
                .Concat(changeRequest?.TokenEnvironment is { } hostSecret ? [hostSecret] : []);
            var redactor = new SecretRedactor(secretNames.Select(environment).OfType<string>());
            if (options.Command is "review" or "validate") _ = ReviewContract.BuildInstructions(policy, redactor);
            IReviewMemory? memory = null;
            if (options.Command != "scan" && policy.Memory.Enabled)
            {
                var identity = MemorySettings.Identity(policy.Memory, options.Repository, changeRequest);
                FollowUpEvent? followUp = options.EventPath is null ? null : await FollowUpEventReader.ReadAsync(options.EventPath, identity, changeRequest, policy.JiraSite, timeout.Token);
                identity = identity with { SessionId = identity.SessionId ?? followUp?.SessionId };
                memory = new MagiCoreReviewMemory(policy.Memory, identity,
                    MemorySettings.DirectoryPath(policy.Memory, options.Repository, options.OutputDirectory), redactor);
                if (followUp is not null)
                {
                    stage = "memory";
                    var accepted = await memory.RememberFollowUpAsync(followUp, timeout.Token);
                    await output.WriteLineAsync(accepted ? "Follow-up remembered with source and actor provenance." : "Follow-up ignored: actor or agent mention did not match trusted policy.");
                    if (!accepted || options.Command == "follow-up") return 0;
                }
            }
            else if (options.EventPath is not null || options.Command is "follow-up" or "memory-search")
                throw new HarnessException(ErrorCode.Configuration, "Long-term memory is disabled.", "Enable memory in trusted global/repository policy or set --memory-scope global, repository or session.");
            if (options.Command == "memory-search")
            {
                stage = "memory";
                var recalled = await memory!.RecallAsync(new MemoryQuery(options.Query!, options.IssueKeys), timeout.Token);
                await output.WriteLineAsync(JsonSerializer.Serialize(recalled, JsonDefaults.Options));
                return 0;
            }
            var keyName = policy.Provider == "openai" ? "OPENAI_API_KEY" : "ANTHROPIC_API_KEY";
            if (options.Command is "review" or "validate")
            {
                if (string.IsNullOrWhiteSpace(policy.Model))
                    throw new HarnessException(ErrorCode.Configuration, "Review model is not configured.", "Set HARNESS_MODEL or model in trusted policy. Select a model with structured output support.");
                _ = RequiredSecret(keyName);
                if (policy.JiraSite is not null || options.IssueKeys.Any(JiraIssueSource.IsIssueKey))
                {
                    _ = JiraIssueSource.ValidateSite(policy.JiraSite);
                    _ = RequiredSecret("JIRA_EMAIL");
                    _ = RequiredSecret("JIRA_API_TOKEN");
                }
                foreach (var source in policy.ReferenceSources.Where(s => s.TokenEnvironment is not null))
                    _ = RequiredSecret(source.TokenEnvironment!);
                if (changeRequest is not null)
                    _ = RequiredSecret(changeRequest.TokenEnvironment ?? (changeRequest.Platform == "github" ? "GITHUB_TOKEN" : "GITLAB_TOKEN"));
            }
            if (options.Command == "validate")
            {
                await output.WriteLineAsync("Configuration valid. Credentials are present; remote authentication was not tested.");
                return 0;
            }
            using var client = SafeHttp.CreateClient();
            var http = new SafeHttp(client);
            IReviewProvider? provider = options.Command == "scan" ? null : policy.Provider == "openai"
                ? new OpenAiReviewProvider(http, policy, RequiredSecret(keyName), redactor)
                : new AnthropicReviewProvider(http, policy, RequiredSecret(keyName), redactor);
            IIssueSource? jira = options.Command == "scan" || (policy.JiraSite is null && !options.IssueKeys.Any(JiraIssueSource.IsIssueKey)) ? null : new JiraIssueSource(http, policy,
                RequiredSecret("JIRA_EMAIL"), RequiredSecret("JIRA_API_TOKEN"), redactor);
            IReferenceResolver? resolver = options.Command == "scan" ? null : new ReferenceResolver(http, policy, environment, redactor, jira);
            IChangeRequestHost? host = changeRequest is null ? null : new ChangeRequestHost(http, changeRequest,
                RequiredSecret(changeRequest.TokenEnvironment ?? (changeRequest.Platform == "github" ? "GITHUB_TOKEN" : "GITLAB_TOKEN")), redactor, commentTemplate);
            var useCase = new ReviewUseCase(new GitRepository(options.Repository, policy, redactor), provider,
                jira, new ReportWriter(redactor), TimeProvider.System, resolver, host, options.Feedback, memory);
            stage = "review";
            var report = await useCase.ExecuteAsync(options.Command, options.BaseRef, options.HeadRef,
                options.IssueKeys, policy, options.OutputDirectory, timeout.Token);
            await output.WriteLineAsync($"BusinessHarness {options.Command}: {(report.Passed ? "PASSED" : "GATE FAILED")} | findings={report.Findings.Count} | requirements={report.Requirements.Count}");
            await output.WriteLineAsync("Reports: review.json, review.md, review.sarif in the configured output directory.");
            if (options.Feedback != "none") await output.WriteLineAsync("Pull/merge request feedback published.");
            return report.Passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            var failure = MapFailure(exception, cancellationToken.IsCancellationRequested);
            var report = new FailureReport("1.0", failure.Id, failure.Code.ToString(), stage, failure.Message,
                failure.Hint, (int)failure.Code, correlationId);
            await error.WriteLineAsync($"{report.ErrorCode} [{report.Category}]: {report.Message}");
            await error.WriteLineAsync($"Fix: {report.Hint}");
            await error.WriteLineAsync($"Correlation: {correlationId}");
            if (outputDirectory is not null)
            {
                try
                {
                    ReportWriter.Prepare(outputDirectory);
                    await ReportWriter.WriteFailureAsync(report, outputDirectory, CancellationToken.None);
                }
                catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException or HarnessException)
                {
                    await error.WriteLineAsync("BH1008 [FileSystem]: Could not save error.json. Check output directory permissions and links.");
                }
            }
            return report.ExitCode;
        }
    }

    private string? NonEmptyEnvironment(string name) => environment(name) is { Length: > 0 } value ? value : null;

    private string RequiredSecret(string name)
    {
        var value = environment(name);
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new HarnessException(ErrorCode.Configuration, $"Required variable {name} is missing or invalid.",
                "Configure it as a masked/secret CI variable. Fork and protected pipelines may intentionally withhold secrets.");
        return value;
    }

    private static async Task<HarnessPolicy> LoadPolicyAsync(string? path, CancellationToken ct)
    {
        if (path is null) return new HarnessPolicy();
        SafePaths.RejectLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 64_000) throw new HarnessException(ErrorCode.Configuration, "Policy file exceeds 64 KB.", "Use a small explicit JSON policy; do not embed credentials or repository content.");
        return await JsonSerializer.DeserializeAsync<HarnessPolicy>(stream, JsonDefaults.Options, ct) ??
            throw new HarnessException(ErrorCode.Configuration, "Policy file is empty.", "Supply a JSON policy object.");
    }

    private static async Task<string> LoadCommentTemplateAsync(string path, CancellationToken ct)
    {
        SafePaths.RejectLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        var buffer = new char[ReviewCommentTemplate.MaxLength + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        if (count > ReviewCommentTemplate.MaxLength)
            throw new HarnessException(ErrorCode.Configuration, "Review comment template exceeds 16,000 characters.", "Use a smaller trusted Markdown template. Comments are never silently truncated.");
        var template = new string(buffer, 0, count).TrimStart('\uFEFF');
        ReviewCommentTemplate.Validate(template);
        return template;
    }

    private static HarnessException MapFailure(Exception exception, bool cancelled) => exception switch
    {
        HarnessException known => known,
        OperationCanceledException => new(ErrorCode.Cancelled, cancelled ? "Run cancelled." : "Run exceeded its time limit.", "Rerun or adjust timeoutSeconds in trusted policy; no passing review was recorded."),
        JsonException => new(ErrorCode.Configuration, "Invalid policy JSON or unknown property.", "Check property spelling, types and JSON syntax against docs/configuration.md."),
        IOException or UnauthorizedAccessException => new(ErrorCode.FileSystem, "Could not read configuration or write reports.", "Check paths, file permissions, free space and whether OneDrive or another process locked the file."),
        ArgumentException or NotSupportedException => new(ErrorCode.Configuration, "An argument or path is invalid.", "Run --help and check the policy and paths. Do not place credentials in command-line arguments."),
        _ => new(ErrorCode.Unexpected, "An unexpected internal error stopped the run.", "Report the correlation ID, tool version and sanitized reproduction. No successful review was recorded.")
    };

    private const string Help = """
        BusinessHarness 0.1.0 — portable repository automation (.NET 10)

        businessharness scan     --base REF [options]  Offline credential-pattern scan
        businessharness review   --base REF [options]  AI review + discovered reference validation
        businessharness validate [options]             Validate configuration, no API calls
        businessharness follow-up --event PATH [options] Remember an allowed agent mention, no API calls
        businessharness memory-search --query TEXT [options] Search durable scoped memory, no API calls

        --repo PATH       Git checkout (default: current directory)
        --base REF        Target commit/ref; also HARNESS_BASE (required for scan/review)
        --head REF        Source commit/ref; also HARNESS_HEAD (default: HEAD)
        --output PATH     Report directory (default: harness-results)
        --config PATH     Explicit TRUSTED policy JSON; never loaded from the PR automatically
        --issue KEYS      Comma-separated Jira keys (review only, maximum 20)
        --reference REFS  Optional SOURCE:ID references; discovered from PR/MR metadata by default
        --change URL      GitHub PR or GitLab MR; auto-detected in GitHub/GitLab CI
        --feedback MODE   none (default), comment, auto (approve pass / reject failure)
        --comment-template PATH  Explicit TRUSTED UTF-8 Markdown template (review/validate)
        --skills NAMES    Enable comma-separated trusted skill names, or none (review/validate)
        --memory-scope S  off, global, repository or session; overrides trusted memory defaults
        --memory-directory PATH  Persistent trusted storage outside the reviewed checkout
        --session ID      Stable session/thread ID; also HARNESS_SESSION_ID
        --event PATH      Verified comment/webhook payload (review or follow-up)
        --query TEXT      Search text (memory-search)

        Provider: HARNESS_PROVIDER=openai|anthropic; HARNESS_MODEL is required for review.
        Secrets: OPENAI_API_KEY or ANTHROPIC_API_KEY; Jira: JIRA_EMAIL + JIRA_API_TOKEN.
        Hosts: GITHUB_TOKEN or GITLAB_TOKEN. Reference sources are configured in trusted policy.
        Memory: HARNESS_MEMORY_CONFIG selects trusted global defaults; repository --config overrides them.
        Exit: 0 pass, 1 gate failed, 2 configuration, 3 Git, 4 auth, 5 network,
              6 invalid response, 7 limits, 8 filesystem, 9 timeout/cancel, 10 internal.
        """;
}
