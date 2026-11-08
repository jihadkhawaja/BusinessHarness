using System.Net;
using System.Text.Json;
using BusinessHarness.Core;
using BusinessHarness.Infrastructure;
using Xunit;

namespace BusinessHarness.Tests;

public sealed class DesktopConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void CatalogListsProvidersAndNeverExposesWritesAsReads()
    {
        var providers = DesktopConnectorCatalog.Providers();
        Assert.All(providers, p => Assert.True(p.HasReadActions, p.Id));
        Assert.Contains(providers, p => p.Id == "notion" && p.HasReadActions);
        Assert.Contains(DesktopConnectorCatalog.Actions("linear"), a => a.Id == "linear.get_issue");
        Assert.DoesNotContain(DesktopConnectorCatalog.Actions("linear"), a => a.Id == "linear.graphql");
        Assert.Contains(providers, p => p.Id == "hackernews" && !p.RequiresCredential);
        Assert.Contains(DesktopConnectorCatalog.Actions("github"), a => a.Id == "github.get_repository");
        Assert.DoesNotContain(DesktopConnectorCatalog.Actions("github"), a => a.Id == "github.create_issue");
    }

    [Fact]
    public async Task EachDirectConnectionUsesItsOwnProviderCredentialAndRedactsResponse()
    {
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            if (request.RequestUri!.Host == "app.asana.com")
            {
                Assert.Equal("first-credential", request.Headers.Authorization!.Parameter);
                return Task.FromResult(StubHttpHandler.Json("""{"name":"first-credential"}"""));
            }
            Assert.Equal("api.notion.com", request.RequestUri.Host);
            Assert.Equal("second-credential", request.Headers.Authorization!.Parameter);
            Assert.Equal("2025-09-03", Assert.Single(request.Headers.GetValues("Notion-Version")));
            return Task.FromResult(StubHttpHandler.Json("""{"id":"second-credential"}"""));
        });
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, _ => null);
        workspace.ConnectCatalogApp("asana", new("", "first-credential"));
        workspace.ConnectCatalogApp("notion", new("", "second-credential"));
        var first = await workspace.ReadCatalogAppAsync("asana", "asana.get_current_user", new Dictionary<string, string>(), Ct);
        var second = await workspace.ReadCatalogAppAsync("notion", "notion.get_page", new Dictionary<string, string> { ["page_id"] = "123" }, Ct);
        Assert.Contains("[REDACTED]", first.Content);
        Assert.Contains("[REDACTED]", second.Content);
        Assert.DoesNotContain("credential", first.Content);
        Assert.DoesNotContain("credential", second.Content);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("http://team.zendesk.com/")]
    [InlineData("https://user@team.zendesk.com/")]
    [InlineData("https://team.zendesk.com:444/")]
    [InlineData("https://team.zendesk.com/?secret=x")]
    public void InstanceConnectorRejectsUnsafeDestinations(string uri)
    {
        using var handler = new StubHttpHandler(_ => throw new InvalidOperationException("Must not send"));
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, _ => null);
        Assert.Throws<HarnessException>(() => workspace.ConnectCatalogApp("zendesk", new(uri, "token")));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ReadRejectsWriteUnknownInputsPathEscapesAndRedirects()
    {
        using var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json("{}", HttpStatusCode.Redirect)));
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, _ => null);
        workspace.ConnectCatalogApp("github", new("", "token"));
        await Assert.ThrowsAsync<HarnessException>(() => workspace.ReadCatalogAppAsync("github", "github.create_issue", new Dictionary<string, string>(), Ct));
        await Assert.ThrowsAsync<HarnessException>(() => workspace.ReadCatalogAppAsync("github", "github.get_repository", new Dictionary<string, string> { ["owner"] = "../bad", ["repo"] = "repo" }, Ct));
        await Assert.ThrowsAsync<HarnessException>(() => workspace.ReadCatalogAppAsync("github", "github.get_repository", new Dictionary<string, string> { ["owner"] = "ok", ["repo"] = "repo", ["extra"] = "value" }, Ct));
        Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<HarnessException>(() => workspace.ReadCatalogAppAsync("github", "github.get_repository", new Dictionary<string, string> { ["owner"] = "ok", ["repo"] = "repo" }, Ct));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task BasicCredentialComponentsAreRedactedIndividually()
    {
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            return Task.FromResult(StubHttpHandler.Json("""{"message":"personal-token","user":"account-id"}"""));
        });
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, _ => null);
        workspace.ConnectCatalogApp("twilio", new("", "account-id:personal-token"));
        var result = await workspace.ReadCatalogAppAsync("twilio", "twilio.get_account", new Dictionary<string, string> { ["account_sid"] = "AC123" }, Ct);
        Assert.DoesNotContain("personal-token", result.Content);
        Assert.DoesNotContain("account-id", result.Content);
        Assert.Contains("[REDACTED]", result.Content);
    }

    [Fact]
    public async Task EscapedCredentialIsRemovedFromJsonNamesValuesAndProviderRequest()
    {
        const string token = "private\"\\value";
        var response = JsonSerializer.Serialize(new Dictionary<string, object> { [token] = token, ["nested"] = new[] { token } });
        using var handler = new StubHttpHandler(async request =>
        {
            if (request.RequestUri!.Host == "api.clickup.com") return StubHttpHandler.Json(response);
            Assert.Equal("api.openai.com", request.RequestUri.Host);
            var body = await request.Content!.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("private", body);
            return StubHttpHandler.Json("""{"status":"completed","output":[{"type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Done"}]}]}""");
        });
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, name => name == "OPENAI_API_KEY" ? "synthetic-api" : null);
        await workspace.ConfigureAsync(null, "openai", "test-model", Ct);
        workspace.ConnectCatalogApp("clickup", new("", token));
        var attachment = await workspace.ReadCatalogAppAsync("clickup", "clickup.get_current_user", new Dictionary<string, string>(), Ct);
        Assert.DoesNotContain("private", attachment.Content);
        using var json = JsonDocument.Parse(attachment.Content);
        Assert.True(json.RootElement.TryGetProperty("[REDACTED]", out _));
        await workspace.CreateChat(false).SendAsync("Summarize", [attachment], Ct);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task RedactionRejectsCollidingKeysAndCoversNumericScalars()
    {
        var response = """{"123456789":1,"[REDACTED]":2}""";
        using var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json(response)));
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, _ => null);
        workspace.ConnectCatalogApp("clickup", new("", "123456789"));
        await Assert.ThrowsAsync<HarnessException>(() => workspace.ReadCatalogAppAsync("clickup", "clickup.get_current_user", new Dictionary<string, string>(), Ct));
        response = """{"value":123456789}""";
        var result = await workspace.ReadCatalogAppAsync("clickup", "clickup.get_current_user", new Dictionary<string, string>(), Ct);
        Assert.DoesNotContain("123456789", result.Content);
        Assert.Contains("[REDACTED]", result.Content);
    }
}
