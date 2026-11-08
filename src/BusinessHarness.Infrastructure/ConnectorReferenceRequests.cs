using System.Text.Json.Nodes;
using AgentConnector;
using BusinessHarness.Core;

namespace BusinessHarness.Infrastructure;

/// <summary>Uses the pinned connector catalog without exposing its runtime, proxies or write actions.</summary>
public static class ConnectorReferenceRequests
{
    public static void ValidateSources(IEnumerable<ReferenceSourcePolicy> sources)
    {
        foreach (var source in sources.Where(s => s.Connector is not null))
        {
            var configuration = source.Connector!;
            using var request = Build(configuration, ConnectorReadRecipe.Find(configuration.Action)?.Example ?? "example");
        }
    }

    public static HttpRequestMessage Build(ConnectorReferencePolicy configuration, string id)
    {
        configuration.Validate();
        try
        {
            var catalog = new ProviderCatalog(new ConnectorOptions());
            var recipe = ConnectorReadRecipe.Find(configuration.Action);
            if (recipe is not null)
            {
                var recipeProvider = catalog.GetProvider(recipe.Id.Split('.')[0]);
                if (configuration.IdParameter != recipe.Identifier || configuration.Inputs.Count != 0 ||
                    recipeProvider.RequiresBaseUri != (configuration.BaseUri is not null))
                    throw Invalid();
                if (configuration.BaseUri is not null)
                    recipeProvider = recipeProvider with { BaseUri = IntegrationEndpoint.Validate(configuration.BaseUri) };
                return ValidateEndpoint(recipe.Build(recipeProvider, new() { [recipe.Identifier] = id }));
            }

            var (provider, action) = catalog.GetAction(configuration.Action);
            // Confluence omits page content unless the read request selects a body representation.
            if (action.Id == "confluence.get_page")
                action = action with { Parameters = [.. action.Parameters.Where(p => p.Name != "body-format"), new("body_format", Required: true, WireName: "body-format")] };
            if (!ConnectorReadRecipe.IsCatalogGet(action) ||
                !action.Parameters.Any(p => p.Name == configuration.IdParameter && p.Kind == ParameterKind.String &&
                    (p.Location == ParameterLocation.Path || IsSafeQueryIdentifier(action.Id, p))) ||
                (provider.RequiresBaseUri && configuration.BaseUri is null))
                throw Invalid();
            if (configuration.BaseUri is not null)
                provider = provider with { BaseUri = IntegrationEndpoint.Validate(configuration.BaseUri) };
            var input = new JsonObject();
            foreach (var (name, value) in configuration.Inputs)
                input[name] = JsonNode.Parse(value.GetRawText());
            input[configuration.IdParameter] = id;
            return ValidateEndpoint(ProviderRequestBuilder.Build(provider, action, input));
        }
        catch (Exception exception) when (exception is ConnectorException or ArgumentException)
        {
            throw Invalid();
        }
    }

    private static HttpRequestMessage ValidateEndpoint(HttpRequestMessage request)
    {
        try
        {
            _ = IntegrationEndpoint.Validate(request.RequestUri!.AbsoluteUri);
            return request;
        }
        catch { request.Dispose(); throw; }
    }

    private static bool IsSafeQueryIdentifier(string action, ActionParameter parameter) =>
        parameter.Location == ParameterLocation.Query &&
        ((action == "slack.list_messages" && parameter.Name == "channel") ||
         (action == "jira.find_assignable_users" && parameter.Name == "issueKey") ||
         (action == "asana.list_tasks" && parameter.Name is "project" or "workspace" or "assignee"));

    public static HttpRequestMessage BuildJira(Uri site, string key, string fields)
    {
        var request = Build(new()
        {
            Action = "jira.get_issue",
            IdParameter = "issue_id",
            BaseUri = site.AbsoluteUri
        }, key);
        request.RequestUri = new Uri(request.RequestUri!, "?fields=" + Uri.EscapeDataString(fields));
        return request;
    }

    private static HarnessException Invalid() => new(ErrorCode.Configuration, "Invalid connector reference action or inputs.",
        "Use a supported read action with its identifier and required inputs. Instance providers require a trusted baseUri. See docs/configuration.md.");
}
