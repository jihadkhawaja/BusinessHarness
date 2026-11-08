using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentConnector;
using BusinessHarness.Core;

namespace BusinessHarness.Infrastructure;

public sealed record DesktopConnectorInfo(string Id, string Name, bool RequiresBase, bool RequiresCredential, string CredentialDescription, bool HasReadActions);
public sealed record DesktopConnectorAction(string Id, string Name, IReadOnlyList<DesktopConnectorParameter> Parameters);
public sealed record DesktopConnectorParameter(string Name, string Kind, bool Required, string? Example = null);
public sealed class DesktopConnectorSettings(string baseUri, string credential)
{
    public string BaseUri { get; } = baseUri;
    public string Credential { get; } = credential;
}

/// <summary>User-selected, catalog-bound read requests. The model has no connector tools.</summary>
public static class DesktopConnectorCatalog
{
    private static ProviderCatalog Catalog() => new(new ConnectorOptions());

    public static IReadOnlyList<DesktopConnectorInfo> Providers() => Catalog().Providers
        .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
        .Select(p => new DesktopConnectorInfo(p.Id, p.DisplayName, p.RequiresBaseUri, p.CredentialKind != CredentialKind.None,
            CredentialDescription(p), p.Actions.Any(ConnectorReadRecipe.IsCatalogGet) || Recipes(p).Any()))
        .ToArray();

    public static IReadOnlyList<DesktopConnectorAction> Actions(string providerId)
    {
        var provider = Provider(providerId);
        return provider.Actions.Where(ConnectorReadRecipe.IsCatalogGet)
            .Select(a => new DesktopConnectorAction(a.Id, a.Description,
                a.Parameters.Select(p => new DesktopConnectorParameter(p.Name, p.Kind.ToString(), p.Required)).ToArray()))
            .Concat(Recipes(provider).Select(recipe => new DesktopConnectorAction(recipe.Id, recipe.Name, [new(recipe.Identifier, "String", true, recipe.Example)])))
            .ToArray();
    }

    public static void ValidateConnection(string providerId, DesktopConnectorSettings settings)
    {
        var provider = Provider(providerId);
        if (settings is null || settings.BaseUri is null || settings.Credential is null ||
            settings.Credential.Length > 16_000 || settings.Credential.Any(char.IsControl) ||
            (provider.CredentialKind != CredentialKind.None && string.IsNullOrWhiteSpace(settings.Credential)) ||
            (provider.CredentialKind == CredentialKind.Basic && (settings.Credential.IndexOf(':') < 1 || settings.Credential.EndsWith(':'))) ||
            (!provider.RequiresBaseUri && settings.BaseUri.Length != 0)) throw Invalid();
        if (provider.RequiresBaseUri)
        {
            var uri = IntegrationEndpoint.Validate(settings.BaseUri);
            if (!settings.BaseUri.EndsWith('/') || uri.Query.Length != 0 || settings.BaseUri.Length > 2000) throw Invalid();
            ConnectorReadRecipe.ValidateInstance(provider.Id, uri);
        }
    }

    public static IEnumerable<string> Secrets(string providerId, DesktopConnectorSettings settings)
    {
        ValidateConnection(providerId, settings);
        if (settings.Credential.Length == 0) return [];
        if (Provider(providerId).CredentialKind != CredentialKind.Basic) return [settings.Credential];
        var separator = settings.Credential.IndexOf(':');
        return [settings.Credential, settings.Credential[..separator], settings.Credential[(separator + 1)..],
            Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Credential))];
    }

    public static async Task<ChatAttachment> ReadAsync(HttpClient client, string providerId, DesktopConnectorSettings settings,
        string actionId, IReadOnlyDictionary<string, string> values, SecretRedactor redactor, CancellationToken ct)
    {
        ValidateConnection(providerId, settings);
        var provider = Provider(providerId);
        var recipe = Recipes(provider).SingleOrDefault(recipe => recipe.Id == actionId);
        var action = provider.Actions.SingleOrDefault(a => a.Id == actionId && ConnectorReadRecipe.IsCatalogGet(a));
        if (recipe is null && action is null) throw Invalid();
        if (values.Count > 20 || values.Any(p => p.Key.Length > 128 || p.Value.Length > 2000 || p.Value.Any(char.IsControl))) throw Invalid();
        var input = new JsonObject();
        foreach (var (name, value) in values)
        {
            var kind = recipe is not null && recipe.Identifier == name ? ParameterKind.String :
                action?.Parameters.SingleOrDefault(p => p.Name == name)?.Kind ?? throw Invalid();
            input[name] = kind switch
            {
                ParameterKind.String or ParameterKind.Guid => JsonValue.Create(value),
                ParameterKind.Integer when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number),
                ParameterKind.Boolean when bool.TryParse(value, out var boolean) => JsonValue.Create(boolean),
                _ => throw Invalid()
            };
        }
        if (provider.RequiresBaseUri) provider = provider with { BaseUri = IntegrationEndpoint.Validate(settings.BaseUri) };
        try
        {
            using var request = recipe is null ? ProviderRequestBuilder.Build(provider, action!, input) : recipe.Build(provider, input);
            var destination = IntegrationEndpoint.Validate(request.RequestUri!.AbsoluteUri);
            if (destination.Authority != provider.BaseUri.Authority) throw Invalid();
            request.Headers.Accept.Add(new("application/json"));
            request.Headers.UserAgent.ParseAdd("BusinessHarness/0.1.0");
            switch (provider.CredentialKind)
            {
                case CredentialKind.None: break;
                case CredentialKind.Bearer or CredentialKind.OAuth2:
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Credential); break;
                case CredentialKind.Basic:
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Credential))); break;
                case CredentialKind.ApiKey when provider.ApiKeyHeader is not null:
                    if (!request.Headers.TryAddWithoutValidation(provider.ApiKeyHeader, settings.Credential)) throw Invalid();
                    break;
                default: throw Invalid();
            }
            using var response = await new SafeHttp(client).SendAsync(request, "Connected app", ct);
            ConnectorReadRecipe.ValidateResponse(recipe?.Id, response.RootElement);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer)) WriteRedacted(writer, response.RootElement, redactor);
            var content = Encoding.UTF8.GetString(buffer.ToArray());
            if (content.Length > 32_000) throw new HarnessException(ErrorCode.LimitExceeded, "The selected result is too large.", "Choose a smaller result.");
            return new(providerId + ":" + actionId, content);
        }
        catch (Exception ex) when (ex is ConnectorException or ArgumentException or InvalidOperationException or FormatException) { throw Invalid(); }
    }

    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement value, SecretRedactor redactor)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    var name = redactor.Redact(property.Name);
                    if (!names.Add(name)) throw new HarnessException(ErrorCode.ProviderResponse, "The app returned ambiguous content after redaction.", "Choose a different result; no partial content was attached.");
                    writer.WritePropertyName(name);
                    WriteRedacted(writer, property.Value, redactor);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteRedacted(writer, item, redactor);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(redactor.Redact(value.GetString()!));
                break;
            default:
                var original = value.GetRawText();
                var sanitized = redactor.Redact(original);
                if (sanitized == original) value.WriteTo(writer);
                else writer.WriteStringValue(sanitized);
                break;
        }
    }

    private static ConnectorProvider Provider(string id)
    {
        try { return Catalog().GetProvider(id); }
        catch (ConnectorException) { throw Invalid(); }
    }

    private static IEnumerable<ConnectorReadRecipe> Recipes(ConnectorProvider provider) => ConnectorReadRecipe.All
        .Where(recipe => recipe.Id.StartsWith(provider.Id + ".", StringComparison.Ordinal));

    private static string CredentialDescription(ConnectorProvider provider) => provider.CredentialKind switch
    {
        CredentialKind.None => "No credential required",
        CredentialKind.Basic => "Account ID and token, separated by :",
        CredentialKind.ApiKey => "API key or provider authorization value",
        CredentialKind.OAuth2 => "Provider OAuth access token",
        CredentialKind.Bearer when provider.OAuth is not null => "Provider access token",
        _ => "Personal access token"
    };

    private static HarnessException Invalid() => new(ErrorCode.Configuration, "This app connection or request is invalid.",
        "Check the app, token, API address and required fields. Only catalog read actions are available.");
}
