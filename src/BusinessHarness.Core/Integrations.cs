using System.Text.Json;
using System.Text.RegularExpressions;

namespace BusinessHarness.Core;

/// <summary>Trusted routing configuration. Matched text can supply an identifier, never an endpoint.</summary>
public sealed record ReferenceSourcePolicy
{
    public string Name { get; init; } = "";
    public string Pattern { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string TitlePath { get; init; } = "title";
    public string ContentPath { get; init; } = "body";
    public string? TokenEnvironment { get; init; }
    public string AuthenticationHeader { get; init; } = "Authorization";
    public string AuthenticationScheme { get; init; } = "Bearer";
    public ConnectorReferencePolicy? Connector { get; init; }

    public Regex CreatePattern() => new(Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public void Validate()
    {
        if (!Regex.IsMatch(Name, "^[a-z][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant) || Name == "jira" ||
            Pattern.Length is < 1 or > 2000 || Endpoint.Length > 2000 ||
            (Connector is null && !Endpoint.Contains("{id}", StringComparison.Ordinal)) ||
            (Connector is not null && Endpoint.Length != 0) ||
            TitlePath.Length is < 1 or > 200 || ContentPath.Length is < 1 or > 200 ||
            AuthenticationHeader is not ("Authorization" or "PRIVATE-TOKEN" or "X-Api-Key" or "X-Shopify-Access-Token") ||
            AuthenticationScheme is not ("Bearer" or "Basic" or "") ||
            (TokenEnvironment is not null && !Regex.IsMatch(TokenEnvironment, "^[A-Z][A-Z0-9_]{0,127}$", RegexOptions.CultureInvariant)))
            throw Invalid();
        try
        {
            if (!CreatePattern().GetGroupNames().Contains("id", StringComparer.Ordinal)) throw Invalid();
        }
        catch (ArgumentException) { throw Invalid(); }
        if (Connector is not null)
        {
            Connector.Validate();
            return;
        }
        var uri = IntegrationEndpoint.Validate(Endpoint.Replace("{id}", "example", StringComparison.Ordinal));
        // Identifiers may only occur in a path or query, never in the credential destination.
        if (Endpoint.IndexOf("{id}", StringComparison.Ordinal) < Endpoint.IndexOf('/', "https://".Length)) throw Invalid();
        if (uri.AbsolutePath == "/" && uri.Query.Length == 0) throw Invalid();
    }

    private static HarnessException Invalid() => new(ErrorCode.Configuration, "Invalid reference source configuration.",
        "Use a unique source name, a regex with named group id, an HTTPS endpoint template containing {id} in its path/query, and JSON field paths. See docs/configuration.md.");
}

/// <summary>Only trusted policy selects the action, destination and fixed input values.</summary>
public sealed record ConnectorReferencePolicy
{
    public string Action { get; init; } = "";
    public string IdParameter { get; init; } = "";
    public string? BaseUri { get; init; }
    public Dictionary<string, JsonElement> Inputs { get; init; } = new(StringComparer.Ordinal);

    public void Validate()
    {
        if (string.IsNullOrEmpty(Action) || Action.Length is < 3 or > 128 ||
            string.IsNullOrEmpty(IdParameter) || IdParameter.Length > 128 ||
            Inputs is null || Inputs.Count > 20 || Inputs.ContainsKey(IdParameter) ||
            Inputs.Any(p => p.Key.Length is < 1 or > 128 || p.Value.ValueKind is not
                (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) ||
                p.Value.GetRawText().Length > 2000))
            throw new HarnessException(ErrorCode.Configuration, "Invalid connector reference configuration.",
                "Select a named read-only action, its identifier parameter, and at most 20 fixed scalar inputs. See docs/configuration.md.");
        if (BaseUri is not null)
        {
            var uri = IntegrationEndpoint.Validate(BaseUri);
            if (BaseUri.Length > 2000 || !BaseUri.EndsWith('/') || uri.Query.Length != 0)
                throw new HarnessException(ErrorCode.Configuration, "Invalid connector base URI.", "Use a trusted HTTPS base URI ending in / without a query.");
        }
    }
}

public sealed record ChangeRequestPolicy
{
    public string Platform { get; init; } = "github";
    public string Project { get; init; } = "";
    public int Number { get; init; }
    public string? ApiBase { get; init; }
    public string? TokenEnvironment { get; init; }

    public void Validate(bool requireIdentity = false)
    {
        if (Platform is not ("github" or "gitlab") || Number < 0 || (requireIdentity && (Number == 0 || Project.Length == 0)) || Project.Length > 300 ||
            (Project.Length > 0 && Project.Split('/').Any(p => p.Length == 0 || p is "." or ".." || !p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) ||
            (Project.Length > 0 && Platform == "github" && Project.Split('/').Length != 2) ||
            (TokenEnvironment is not null && !Regex.IsMatch(TokenEnvironment, "^[A-Z][A-Z0-9_]{0,127}$", RegexOptions.CultureInvariant)))
            throw new HarnessException(ErrorCode.Configuration, "Invalid change request configuration.", "Set platform (github/gitlab), project, positive number, and optionally a trusted apiBase and tokenEnvironment.");
        _ = IntegrationEndpoint.Validate(ApiBase ?? (Platform == "github" ? "https://api.github.com/" : "https://gitlab.com/api/v4/"));
        if (ApiBase is not null && (new Uri(ApiBase).Query.Length > 0 || !ApiBase.EndsWith('/')))
            throw new HarnessException(ErrorCode.Configuration, "Invalid change request API base.", "Use an HTTPS base ending in / without a query.");
    }
}

public sealed record ChangeRequestContext(string Title, string Description, string BaseCommit, string HeadCommit, bool Draft, bool Open);

public static class IntegrationEndpoint
{
    public static Uri Validate(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.HostNameType != UriHostNameType.Dns ||
            uri.IsLoopback || !uri.Host.Contains('.'))
            throw new HarnessException(ErrorCode.Configuration, "Invalid integration endpoint.",
                "Configure a trusted HTTPS DNS endpoint without credentials, fragments or custom ports. Restrict runner egress to approved services.");
        return uri;
    }
}
