using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using BusinessHarness.Application;
using BusinessHarness.Core;

namespace BusinessHarness.Infrastructure;

/// <summary>Discovers references using trusted routes, with no shell execution or arbitrary URL fetching.</summary>
public sealed partial class ReferenceResolver(SafeHttp http, HarnessPolicy policy, Func<string, string?> environment,
    SecretRedactor redactor, IIssueSource? jira) : IReferenceResolver
{
    [GeneratedRegex(@"(?<![A-Za-z0-9_])[A-Z][A-Z0-9_]{1,31}-[1-9][0-9]{0,11}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant, 100)]
    private static partial Regex JiraKeys();

    public async Task<IReadOnlyList<ReferenceDocument>> ResolveAsync(string text, IReadOnlyList<string> explicitReferences, CancellationToken cancellationToken)
    {
        if (text.Length > 64_000) throw Limit();
        Dictionary<string, (ReferenceSourcePolicy? Source, string Id)> selected = new(StringComparer.Ordinal);
        foreach (var reference in explicitReferences)
        {
            if (JiraIssueSource.IsIssueKey(reference)) selected.TryAdd(reference, (null, reference));
            else
            {
                var parts = reference.Split(':', 2);
                var source = parts.Length == 2 ? policy.ReferenceSources.SingleOrDefault(s => s.Name == parts[0]) : null;
                if (source is null || !ValidId(parts[1]))
                    throw new HarnessException(ErrorCode.Configuration, "Unknown explicit reference.", "Use SOURCE:ID for a configured source, or a Jira issue key. PR references are discovered automatically.");
                selected.TryAdd(reference, (source, parts[1]));
            }
        }
        List<(int Start, int End)> routedSpans = [];
        try
        {
            foreach (var source in policy.ReferenceSources)
                foreach (Match match in source.CreatePattern().Matches(text))
                {
                    var id = match.Groups["id"].Value;
                    routedSpans.Add((match.Index, match.Index + match.Length));
                    if (!ValidId(id)) throw new HarnessException(ErrorCode.Configuration, "A matched reference has an invalid identifier.", "Use non-empty identifiers up to 256 characters; adjust the trusted source pattern.");
                    selected.TryAdd(source.Name + ":" + id, (source, id));
                    if (selected.Count > 20) throw Limit();
                }
        }
        catch (RegexMatchTimeoutException)
        {
            throw new HarnessException(ErrorCode.LimitExceeded, "Reference discovery pattern exceeded its time limit.", "Simplify the regex in trusted policy.");
        }
        if (policy.JiraSite is not null)
            foreach (Match match in JiraKeys().Matches(text))
                if (!routedSpans.Any(s => match.Index < s.End && match.Index + match.Length > s.Start))
                    selected.TryAdd(match.Value, (null, match.Value));
        if (selected.Count > 20) throw Limit();
        List<ReferenceDocument> documents = [];
        foreach (var (key, route) in selected)
        {
            if (route.Source is null)
            {
                if (jira is null) throw new HarnessException(ErrorCode.Configuration, "Jira reference discovered but Jira is not configured.", "Set jiraSite, JIRA_EMAIL and JIRA_API_TOKEN.");
                documents.Add(await jira.GetIssueAsync(route.Id, cancellationToken));
            }
            else documents.Add(await FetchAsync(key, route.Source, route.Id, cancellationToken));
            if (documents.Sum(d => d.Summary.Length + d.Description.Length + d.AcceptanceCriteria.Length) > 128_000) throw Limit();
        }
        return documents;
    }

    private async Task<ReferenceDocument> FetchAsync(string key, ReferenceSourcePolicy source, string id, CancellationToken ct)
    {
        using var request = source.Connector is null ? BuildJsonRequest(source, id) : ConnectorReferenceRequests.Build(source.Connector, id);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("BusinessHarness/0.1.0");
        if (source.TokenEnvironment is not null)
        {
            var token = IntegrationSecrets.Require(environment, source.TokenEnvironment);
            request.Headers.Add(source.AuthenticationHeader, (source.AuthenticationScheme.Length == 0 ? "" : source.AuthenticationScheme + " ") + token);
        }
        using var body = await http.SendAsync(request, "Reference source " + source.Name, ct);
        ConnectorReadRecipe.ValidateResponse(source.Connector?.Action, body.RootElement);
        var title = ReadPath(body.RootElement, source.TitlePath);
        var content = ReadPath(body.RootElement, source.ContentPath);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
            throw new HarnessException(ErrorCode.ProviderResponse, "Reference source returned missing or empty content.", "Check JSON field paths, reference visibility and the API contract. References are never silently skipped.");
        if (title.Length + content.Length > 32_000) throw Limit();
        return new(redactor.Redact(key), redactor.Redact(title), redactor.Redact(content));
    }

    private static HttpRequestMessage BuildJsonRequest(ReferenceSourcePolicy source, string id)
    {
        var endpoint = IntegrationEndpoint.Validate(source.Endpoint.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal));
        var expected = new Uri(source.Endpoint.Replace("{id}", "example", StringComparison.Ordinal));
        if (endpoint.Authority != expected.Authority) throw new HarnessException(ErrorCode.Configuration, "Reference endpoint changed origin.", "Keep {id} in the path or query of a trusted endpoint.");
        return new(HttpMethod.Get, endpoint);
    }

    private static string? ReadPath(JsonElement element, string path)
    {
        foreach (var name in path.Split('.'))
        {
            if (element.ValueKind == JsonValueKind.Array && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                index < element.GetArrayLength())
                element = element[index];
            else if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
                return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static bool ValidId(string id) => id.Length is > 0 and <= 256 && id is not ("." or "..") && !id.Any(char.IsControl);
    private static HarnessException Limit() => new(ErrorCode.LimitExceeded, "Reference context exceeded its limit.", "Use at most 20 distinct references, 32,000 characters per document, 128,000 characters total and 64,000 characters of PR metadata.");
}

public static class IntegrationSecrets
{
    public static string Require(Func<string, string?> environment, string name)
    {
        var value = environment(name);
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new HarnessException(ErrorCode.Configuration, $"Required variable {name} is missing or invalid.", "Configure the integration credential as a masked CI secret.");
        return value;
    }
}
