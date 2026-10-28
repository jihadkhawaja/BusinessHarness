using System.Net.Http.Headers;
using BusinessHarness.Application;
using BusinessHarness.Core;

namespace BusinessHarness.Infrastructure;

public sealed class ChangeRequestHost : IChangeRequestHost
{
    private readonly SafeHttp http;
    private readonly ChangeRequestPolicy policy;
    private readonly string token;
    private readonly SecretRedactor redactor;
    private readonly Uri endpoint;
    private readonly Uri? conversationComments;

    public ChangeRequestHost(SafeHttp http, ChangeRequestPolicy policy, string token, SecretRedactor redactor)
    {
        policy.Validate(requireIdentity: true);
        this.http = http;
        this.policy = policy;
        this.token = token;
        this.redactor = redactor;
        var apiBase = policy.ApiBase ?? (policy.Platform == "github" ? "https://api.github.com/" : "https://gitlab.com/api/v4/");
        var path = policy.Platform == "github"
            ? "repos/" + string.Join('/', policy.Project.Split('/').Select(Uri.EscapeDataString)) + "/pulls/" + policy.Number
            : "projects/" + Uri.EscapeDataString(policy.Project) + "/merge_requests/" + policy.Number;
        endpoint = new Uri(new Uri(apiBase), path);
        if (policy.Platform == "github")
            conversationComments = new Uri(new Uri(apiBase), "repos/" +
                string.Join('/', policy.Project.Split('/').Select(Uri.EscapeDataString)) + "/issues/" + policy.Number + "/comments");
    }

    public async Task<ChangeRequestContext> ReadAsync(CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, endpoint);
        using var body = await http.SendAsync(request, "Change request host", cancellationToken);
        var root = body.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new HarnessException(ErrorCode.ProviderResponse, "Change request metadata was not an object.", "Check host API compatibility.");
        var github = policy.Platform == "github";
        var title = ApiJson.String(root, "title");
        if (!root.TryGetProperty(github ? "body" : "description", out var descriptionElement) ||
            descriptionElement.ValueKind is not (System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Null))
            throw new HarnessException(ErrorCode.ProviderResponse, "Change request description was missing or invalid.", "Check host API compatibility; reference discovery requires complete metadata.");
        var description = descriptionElement.GetString() ?? "";
        var sha = github && root.TryGetProperty("head", out var head) ? ApiJson.String(head, "sha") : ApiJson.String(root, "sha");
        var baseSha = root.TryGetProperty(github ? "base" : "diff_refs", out var target)
            ? ApiJson.String(target, github ? "sha" : "base_sha") : null;
        var state = ApiJson.String(root, "state");
        if (string.IsNullOrWhiteSpace(title) || !IsCommit(sha) || !IsCommit(baseSha) || state is null ||
            !root.TryGetProperty("draft", out var draft) || draft.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            throw new HarnessException(ErrorCode.ProviderResponse, "Change request metadata was incomplete.", "Check host API compatibility and pull/merge request visibility.");
        if (title.Length + description.Length > 64_000)
            throw new HarnessException(ErrorCode.LimitExceeded, "Change request metadata exceeds 64,000 characters.", "Reduce its title and description before reviewing.");
        return new(redactor.Redact(title), redactor.Redact(description), baseSha!, sha!, draft.GetBoolean(), state == (github ? "open" : "opened"));
    }

    public async Task PublishAsync(ChangeRequestContext reviewed, HarnessReport report, string feedback, CancellationToken cancellationToken)
    {
        if (feedback is not ("comment" or "auto") || report.Mode != "review" || report.HeadCommit != reviewed.HeadCommit)
            throw new HarnessException(ErrorCode.Configuration, "Feedback does not match a completed review.", "Publish only a validated review for the requested source commit.");
        var current = await ReadAsync(cancellationToken);
        if (current != reviewed || !current.Open)
            throw new HarnessException(ErrorCode.Configuration, "Change request changed during review; feedback was withheld.", "Rerun against its current head, title and description.");
        var approve = feedback == "auto" && report.Passed && !current.Draft && report.ExcludedFiles.Count == 0;
        var reject = feedback == "auto" && !report.Passed;
        var markdown = ReportWriter.Markdown(new ReportWriter(redactor).Sanitize(report));
        if (feedback == "auto" && report.Passed && !approve)
            markdown += "\nAutomatic approval withheld: the request is a draft or some files were withheld.\n";
        if (markdown.Length > 60_000)
            throw new HarnessException(ErrorCode.LimitExceeded, "Review comment exceeds 60,000 characters.", "Use local reports or reduce review scope; comments are never silently truncated.");
        if (policy.Platform == "github")
        {
            if (feedback == "comment")
            {
                await PostToAsync(conversationComments!, new { body = markdown }, cancellationToken);
            }
            else
                await PostAsync("reviews", new { commit_id = report.HeadCommit, body = markdown, @event = approve ? "APPROVE" : reject ? "REQUEST_CHANGES" : "COMMENT" }, cancellationToken);
        }
        else
        {
            // GitLab unapprove removes only this bot's approval; a failed pipeline remains the blocking gate.
            if (reject) await PostAsync("unapprove", new { }, cancellationToken);
            await PostAsync("notes", new { body = markdown }, cancellationToken);
            if (approve) await PostAsync("approve", new { sha = report.HeadCommit }, cancellationToken);
        }
    }

    private async Task PostAsync(string suffix, object payload, CancellationToken ct)
        => await PostToAsync(new Uri(endpoint.AbsoluteUri + "/" + suffix), payload, ct);

    private async Task PostToAsync(Uri destination, object payload, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, destination);
        request.Content = SafeHttp.JsonContent(payload);
        using var response = await http.SendAsync(request, "Change request feedback", ct);
    }

    private HttpRequestMessage Request(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("BusinessHarness/0.1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (policy.Platform == "github") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        else request.Headers.Add("PRIVATE-TOKEN", token);
        return request;
    }

    private static bool IsCommit(string? value) => value is not null && value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);
}
