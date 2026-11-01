using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BusinessHarness.Application;
using BusinessHarness.Core;

namespace BusinessHarness.Infrastructure;

public sealed partial class JiraIssueSource : IIssueSource
{
    private readonly SafeHttp http;
    private readonly Uri site;
    private readonly AuthenticationHeaderValue authorization;
    private readonly string? acceptanceField;
    private readonly SecretRedactor redactor;

    public JiraIssueSource(SafeHttp http, HarnessPolicy policy, string email, string apiToken, SecretRedactor redactor)
    {
        this.http = http;
        this.redactor = redactor;
        site = ValidateSite(policy.JiraSite);
        acceptanceField = policy.JiraAcceptanceCriteriaField;
        authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(email + ":" + apiToken)));
    }

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{1,31}-[1-9][0-9]{0,11}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex KeyPattern();

    public static bool IsIssueKey(string key) => KeyPattern().IsMatch(key);

    public static Uri ValidateSite(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            !uri.Host.EndsWith(".atlassian.net", StringComparison.Ordinal) ||
            uri.Host[..^14].Length is < 1 or > 63 ||
            !uri.Host[..^14].All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new HarnessException(ErrorCode.Configuration, "Invalid Jira Cloud site.",
                "Set jiraSite to https://your-team.atlassian.net. Custom hosts, paths, ports and redirects are not supported.");
        return uri;
    }

    public async Task<ReferenceDocument> GetIssueAsync(string key, CancellationToken cancellationToken)
    {
        if (!IsIssueKey(key)) throw new HarnessException(ErrorCode.Configuration, "Invalid Jira issue key.", "Use a key such as PROJ-123.");
        var fields = "summary,description" + (acceptanceField is null ? "" : "," + acceptanceField);
        using var request = ConnectorReferenceRequests.BuildJira(site, key, fields);
        request.Headers.Authorization = authorization;
        request.Headers.Accept.Add(new("application/json"));
        using var body = await http.SendAsync(request, "Jira", cancellationToken);
        return ReadDocument(body.RootElement, key, acceptanceField, redactor);
    }

    public static ReferenceDocument ReadDocument(JsonElement root, string key, string? acceptanceField, SecretRedactor redactor)
    {
        if (ApiJson.String(root, "key") != key ||
            !root.TryGetProperty("fields", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("summary", out var summary) ||
            summary.ValueKind != JsonValueKind.String)
            throw new HarnessException(ErrorCode.ProviderResponse, "Jira returned an unexpected issue shape or key.", "Check issue visibility, Jira API compatibility, and whether the issue was moved.");
        var description = data.TryGetProperty("description", out var desc) ? Flatten(desc) : "";
        var criteria = acceptanceField is not null && data.TryGetProperty(acceptanceField, out var ac) ? Flatten(ac) : "";
        if (acceptanceField is not null && string.IsNullOrWhiteSpace(criteria))
            throw new HarnessException(ErrorCode.Configuration, "Configured acceptance criteria are missing or empty.", "Verify the custom field ID and populate acceptance criteria before requesting validation.");
        if (summary.GetString()!.Length + description.Length + criteria.Length > 32_000)
            throw new HarnessException(ErrorCode.LimitExceeded, "Jira issue text exceeds 32,000 characters.", "Reduce the issue text; requirements are never silently truncated.");
        return new(key, redactor.Redact(summary.GetString()!), redactor.Redact(description), redactor.Redact(criteria));
    }

    private static string Flatten(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString()!;
        if (element.ValueKind == JsonValueKind.Null) return "";
        var result = new StringBuilder();
        Append(element, result);
        return result.ToString().Trim();
    }

    private static void Append(JsonElement element, StringBuilder builder)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) builder.Append(text.GetString());
        if (element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var child in content.EnumerateArray()) Append(child, builder);
        if (ApiJson.String(element, "type") is "paragraph" or "heading" or "listItem" or "hardBreak") builder.AppendLine();
    }
}
