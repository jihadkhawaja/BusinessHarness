using System.Text.Json;
using BusinessHarness.Core;
using BusinessHarness.Infrastructure;
using Xunit;

namespace BusinessHarness.Tests;

public sealed class ConnectorRecipeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("linear", "get_issue", "issue_id", "ENG-42", null, "https://api.linear.app/graphql",
        """{"data":{"issue":{"id":"issue-42","title":"Export","description":"Keep precision"}}}""")]
    [InlineData("monday", "get_item", "item_id", "42", null, "https://api.monday.com/v2",
        """{"data":{"items":[{"id":"42","name":"Export","column_values":[{"text":"Keep precision"}]}]}}""")]
    [InlineData("shopify", "get_product", "product_id", "42", "https://team.myshopify.com/",
        "https://team.myshopify.com/admin/api/2026-01/graphql.json", """{"data":{"product":{"id":"gid://shopify/Product/42","title":"Export","description":"Keep precision"}}}""")]
    [InlineData("dropbox", "get_file_metadata", "path", "/Shared/Export.txt", null, "https://api.dropboxapi.com/2/files/get_metadata",
        """{"name":"Export.txt","path_display":"/Shared/Export.txt"}""")]
    [InlineData("google_analytics", "get_weekly_active_users", "property_id", "42", null,
        "https://analyticsdata.googleapis.com/v1beta/properties/42:runReport", """{"rows":[]}""")]
    [InlineData("cohere", "get_model", "model", "command-a-03-2025", null,
        "https://api.cohere.com/v1/models/command-a-03-2025", """{"name":"command-a-03-2025"}""")]
    public async Task DesktopAndPipelineUseTheSameDirectRead(string provider, string action, string parameter,
        string id, string? apiBase, string uri, string response)
    {
        var actionId = provider + "." + action;
        var configuration = new ConnectorReferencePolicy { Action = actionId, IdParameter = parameter, BaseUri = apiBase };
        ConnectorReferenceRequests.ValidateSources([new() { Connector = configuration }]);
        using var pipeline = ConnectorReferenceRequests.Build(configuration, id);
        Assert.Equal(uri, pipeline.RequestUri!.AbsoluteUri);
        string? payload = null;
        if (provider == "cohere")
        {
            Assert.Equal(HttpMethod.Get, pipeline.Method);
            Assert.Null(pipeline.Content);
        }
        else
        {
            Assert.Equal(HttpMethod.Post, pipeline.Method);
            payload = await pipeline.Content!.ReadAsStringAsync(Ct);
            using var body = JsonDocument.Parse(payload);
            if (provider == "dropbox") Assert.Equal(id, body.RootElement.GetProperty("path").GetString());
            else if (provider == "google_analytics")
            {
                Assert.Equal("activeUsers", body.RootElement.GetProperty("metrics")[0].GetProperty("name").GetString());
                Assert.Equal("7", body.RootElement.GetProperty("limit").GetString());
            }
            else
            {
                Assert.StartsWith("query ", body.RootElement.GetProperty("query").GetString());
                Assert.Equal(provider == "shopify" ? "gid://shopify/Product/" + id : id,
                    body.RootElement.GetProperty("variables").GetProperty("id").GetString());
            }
        }

        using var handler = new StubHttpHandler(async request =>
        {
            Assert.Equal(uri, request.RequestUri!.AbsoluteUri);
            Assert.Equal(pipeline.Method, request.Method);
            Assert.Equal(payload, request.Content is null ? null : await request.Content.ReadAsStringAsync(Ct));
            var header = provider == "shopify" ? "X-Shopify-Access-Token" : "Authorization";
            Assert.Equal(provider is "shopify" or "linear" or "monday" ? "synthetic-token" : "Bearer synthetic-token",
                Assert.Single(request.Headers.GetValues(header)));
            if (provider == "monday") Assert.Equal("2026-01", Assert.Single(request.Headers.GetValues("API-Version")));
            return StubHttpHandler.Json(response);
        });
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, _ => null);
        workspace.ConnectCatalogApp(provider, new(apiBase ?? "", "synthetic-token"));
        var attachment = await workspace.ReadCatalogAppAsync(provider, actionId, new Dictionary<string, string> { [parameter] = id }, Ct);
        Assert.NotEmpty(attachment.Content);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PipelineUsesTrustedCredentialsAndExtractsStringFieldsFromArrayItems()
    {
        var source = new ReferenceSourcePolicy
        {
            Name = "board",
            Pattern = @"BOARD-(?<id>[1-9][0-9]*)",
            Connector = new() { Action = "monday.get_item", IdParameter = "item_id" },
            TitlePath = "data.items.0.name",
            ContentPath = "data.items.0.column_values.0.text",
            AuthenticationScheme = "",
            TokenEnvironment = "BOARD_TOKEN"
        };
        source.Validate();
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal("synthetic-token", Assert.Single(request.Headers.GetValues("Authorization")));
            return Task.FromResult(StubHttpHandler.Json("""
                {"data":{"items":[{"id":"42","name":"Export","column_values":[{"text":"Keep precision synthetic-token"}]}]}}
                """));
        });
        using var client = new HttpClient(handler);
        var resolver = new ReferenceResolver(new(client), new() { ReferenceSources = [source] }, _ => "synthetic-token",
            new(["synthetic-token"]), null);
        var document = Assert.Single(await resolver.ResolveAsync("BOARD-42", [], Ct));
        Assert.Equal("Export", document.Summary);
        Assert.Equal("Keep precision [REDACTED]", document.Description);
        await Assert.ThrowsAsync<HarnessException>(() =>
            new ReferenceResolver(new(client), new() { ReferenceSources = [source with { TitlePath = "data.items.1.name" }] },
                _ => "synthetic-token", new(), null).ResolveAsync("BOARD-42", [], Ct));
    }

    [Theory]
    [InlineData("""{"data":{"issue":{"id":"42","title":"Partial","description":"Do not attach"}},"errors":[{"message":"synthetic-secret"}]}""")]
    [InlineData("""{"data":{"issue":{"title":"Partial","description":"Do not attach"}}}""")]
    public async Task GraphQlErrorsRejectPartialResultsInDesktopAndPipeline(string response)
    {
        using var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json(response)));
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, _ => null);
        workspace.ConnectCatalogApp("linear", new("", "synthetic-secret"));
        var desktopError = await Assert.ThrowsAsync<HarnessException>(() => workspace.ReadCatalogAppAsync("linear", "linear.get_issue",
            new Dictionary<string, string> { ["issue_id"] = "ENG-42" }, Ct));
        var source = new ReferenceSourcePolicy
        {
            Name = "linear",
            Pattern = @"LINEAR:(?<id>[A-Z]+-[0-9]+)",
            Connector = new() { Action = "linear.get_issue", IdParameter = "issue_id" },
            TitlePath = "data.issue.title",
            ContentPath = "data.issue.description"
        };
        var resolver = new ReferenceResolver(new(client), new() { ReferenceSources = [source] }, _ => null, new(), null);
        var pipelineError = await Assert.ThrowsAsync<HarnessException>(() => resolver.ResolveAsync("", ["linear:ENG-42"], Ct));
        foreach (var error in new[] { desktopError, pipelineError })
        {
            Assert.Equal(ErrorCode.ProviderResponse, error.Code);
            Assert.DoesNotContain("synthetic-secret", error.Message);
        }
    }

    [Fact]
    public async Task UntrustedTextCannotReplaceTheGraphQlDocumentOrShopifyHost()
    {
        const string input = "ENG-42\") { mutation { deleteIssue } }";
        var configuration = new ConnectorReferencePolicy { Action = "linear.get_issue", IdParameter = "issue_id" };
        using var request = ConnectorReferenceRequests.Build(configuration, input);
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(Ct));
        Assert.DoesNotContain("mutation", json.RootElement.GetProperty("query").GetString());
        Assert.Equal(input, json.RootElement.GetProperty("variables").GetProperty("id").GetString());
        Assert.Throws<HarnessException>(() => ConnectorReferenceRequests.Build(configuration with
        {
            Inputs = new() { ["body"] = JsonSerializer.SerializeToElement("mutation { deleteIssue }") }
        }, "ENG-42"));

        const string outsideShopify = "https://team.myshopify.com.example.com/";
        Assert.Throws<HarnessException>(() => DesktopConnectorCatalog.ValidateConnection("shopify", new(outsideShopify, "synthetic-token")));
        Assert.Throws<HarnessException>(() => ConnectorReferenceRequests.Build(new()
        {
            Action = "shopify.get_product",
            IdParameter = "product_id",
            BaseUri = outsideShopify
        }, "42"));
    }
}
