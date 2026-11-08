using System.Text.Json;
using System.Text.Json.Nodes;
using AgentConnector;
using BusinessHarness.Core;

namespace BusinessHarness.Infrastructure;

/// <summary>Fixed read operations for catalog providers that need a request body or another API version.</summary>
internal sealed record ConnectorReadRecipe(string Id, string Name, string Identifier, string Example,
    string Path, Func<string, JsonObject>? CreateBody = null, string? GraphQlQuery = null, string? GraphQlResult = null)
{
    internal static IReadOnlyList<ConnectorReadRecipe> All { get; } =
    [
        new("linear.get_issue", "Get issue", "issue_id", "ENG-42", "graphql",
            GraphQlQuery: "query Issue($id: String!) { issue(id: $id) { id identifier title description } }", GraphQlResult: "issue"),
        new("monday.get_item", "Get item", "item_id", "42", "v2",
            GraphQlQuery: "query Item($id: ID!) { items(ids: [$id]) { id name column_values { id text } } }", GraphQlResult: "items"),
        new("shopify.get_product", "Get product", "product_id", "42", "admin/api/2026-01/graphql.json",
            GraphQlQuery: "query Product($id: ID!) { product(id: $id) { id title description } }", GraphQlResult: "product"),
        new("dropbox.get_file_metadata", "Get file or folder metadata", "path", "/example.txt", "files/get_metadata",
            path => new() { ["path"] = path }),
        new("google_analytics.get_weekly_active_users", "Get active users for the last 7 days", "property_id", "42",
            "properties/{property_id}:runReport", _ => new()
            {
                ["dateRanges"] = new JsonArray(new JsonObject { ["startDate"] = "7daysAgo", ["endDate"] = "yesterday" }),
                ["dimensions"] = new JsonArray(new JsonObject { ["name"] = "date" }),
                ["metrics"] = new JsonArray(new JsonObject { ["name"] = "activeUsers" }),
                ["limit"] = "7"
            }),
        new("cohere.get_model", "Get model details", "model", "command-a-03-2025", "models/{model}")
    ];

    internal static ConnectorReadRecipe? Find(string id) => All.SingleOrDefault(recipe => recipe.Id == id);

    internal static bool IsCatalogGet(ConnectorAction action) => action.Method == HttpMethod.Get &&
        !action.AcceptsBody && action.Parameters.All(p => p.Location != ParameterLocation.Form);

    internal HttpRequestMessage Build(ConnectorProvider provider, JsonObject inputs)
    {
        if (inputs.Count != 1 || inputs[Identifier] is not JsonValue value || !value.TryGetValue<string>(out var id) ||
            string.IsNullOrWhiteSpace(id) || id.Length > 2000 || id.Any(char.IsControl) ||
            (Id is "monday.get_item" or "shopify.get_product" or "google_analytics.get_weekly_active_users" &&
                (id.Length > 20 || id[0] == '0' || !id.All(char.IsAsciiDigit))) ||
            (Id == "dropbox.get_file_metadata" && !(id.Length > 1 && id.StartsWith('/') ||
                id.Length > 3 && id.StartsWith("id:", StringComparison.Ordinal))))
            throw Invalid();

        ValidateInstance(provider.Id, provider.BaseUri);
        if (Id == "cohere.get_model")
            provider = provider with { BaseUri = new Uri(provider.BaseUri, "/v1/") };

        var body = GraphQlQuery is null ? CreateBody?.Invoke(id) : new JsonObject
        {
            ["query"] = GraphQlQuery,
            ["variables"] = new JsonObject { ["id"] = Id == "shopify.get_product" ? "gid://shopify/Product/" + id : id }
        };
        ActionParameter[] parameters = Path.Contains('{') ? [new(Identifier, ParameterLocation.Path, Required: true)] : [];
        var requestInputs = new JsonObject();
        if (parameters.Length != 0) requestInputs[Identifier] = id;
        if (body is not null) requestInputs["body"] = body;
        return ProviderRequestBuilder.Build(provider,
            new(Id, Name, body is null ? HttpMethod.Get : HttpMethod.Post, Path, parameters,
                AcceptsBody: body is not null, RequiresBody: body is not null), requestInputs);
    }

    internal static void ValidateInstance(string providerId, Uri uri)
    {
        if (providerId == "shopify" && (!uri.Host.EndsWith(".myshopify.com", StringComparison.Ordinal) || uri.AbsolutePath != "/"))
            throw new HarnessException(ErrorCode.Configuration, "Enter your Shopify store's HTTPS address.",
                "Use the store's root myshopify.com URL, with a trailing slash.");
    }

    internal static void ValidateResponse(string? actionId, JsonElement root)
    {
        if (actionId is null || Find(actionId)?.GraphQlResult is not { } field) return;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("errors", out _) ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty(field, out var result))
            throw Incomplete();
        if (field == "items")
        {
            if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() != 1) throw Incomplete();
            result = result[0];
        }
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
            throw Incomplete();
    }

    private static HarnessException Incomplete() => new(ErrorCode.ProviderResponse, "The app returned incomplete or unavailable data.",
        "Check the identifier and the token's read access. No partial result was accepted.");

    private static HarnessException Invalid() => new(ErrorCode.Configuration, "Invalid input for the connector read action.",
        "Enter the requested identifier and use the app's HTTPS API address.");
}
