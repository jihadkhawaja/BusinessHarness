using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BusinessHarness.Core;

namespace BusinessHarness.Infrastructure;

/// <summary>Explicit, session-only personal Jira Cloud connection with read-only routes.</summary>
public sealed class DirectAtlassianConnection
{
    private readonly SafeHttp http;
    private readonly Uri site;
    private readonly string email;
    private readonly string token;
    private readonly string encoded;
    private readonly Func<SecretRedactor> redactor;

    public DirectAtlassianConnection(HttpClient client, string siteUrl, string email, string token, Func<SecretRedactor> redactor)
    {
        site = JiraIssueSource.ValidateSite(siteUrl);
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320 || email.Contains(':') || email.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(token) || token.Length > 16_000 || token.Any(char.IsControl))
            throw new HarnessException(ErrorCode.Configuration, "Enter your Atlassian email and API token.", "Use an Atlassian API token, not your account password.");
        http = new(client);
        this.email = email;
        this.token = token;
        encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(email + ":" + token));
        this.redactor = redactor;
    }

    public string Site => site.AbsoluteUri;
    public IEnumerable<string> Secrets => [email, token, encoded];

    public async Task<ReferenceDocument> ReadAsync(string kind, string identifier, CancellationToken ct)
    {
        if (kind == "jira")
            return await new JiraIssueSource(http, new HarnessPolicy { JiraSite = site.AbsoluteUri }, email, token, redactor()).GetIssueAsync(identifier, ct);
        if (kind != "confluence" || identifier.Length is < 1 or > 20 || !identifier.All(char.IsAsciiDigit))
            throw new HarnessException(ErrorCode.Configuration, "Enter a valid page ID.", "Use a numeric Confluence page ID.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(site, "wiki/api/v2/pages/" + Uri.EscapeDataString(identifier) + "?body-format=storage"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        request.Headers.Accept.Add(new("application/json"));
        using var response = await http.SendAsync(request, "Confluence", ct);
        var root = response.RootElement;
        if (ApiJson.String(root, "id") != identifier || ApiJson.String(root, "title") is not { Length: > 0 } title ||
            !root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("storage", out var storage) || storage.ValueKind != JsonValueKind.Object ||
            ApiJson.String(storage, "value") is not { Length: > 0 } content || title.Length + content.Length > 32_000)
            throw new HarnessException(ErrorCode.ProviderResponse, "The page content is missing or too large.", "Choose a smaller published page you can access.");
        var currentRedactor = redactor();
        return new("confluence:" + identifier, currentRedactor.Redact(title), currentRedactor.Redact(content));
    }
}
