using System.Text.Json;
using System.Net;
using System.Text;
using BusinessHarness.Core;
using BusinessHarness.Infrastructure;
using Xunit;

namespace BusinessHarness.Tests;

public sealed class ConnectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("http://team.atlassian.net/")]
    [InlineData("https://team.atlassian.net.evil.example/")]
    [InlineData("https://team.atlassian.net:444/")]
    [InlineData("https://user@team.atlassian.net/")]
    [InlineData("https://team.atlassian.net/path")]
    public void DirectJiraRejectsUntrustedSitesBeforeSending(string site)
    {
        using var handler = new StubHttpHandler(_ => throw new InvalidOperationException("Unexpected request"));
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, _ => null);
        Assert.Throws<HarnessException>(() => workspace.ConnectAtlassian(site, "person@example.invalid", "synthetic-token"));
        Assert.Equal(0, handler.Calls);
        Assert.False(workspace.HasAtlassian);
    }

    [Fact]
    public async Task DirectAtlassianReadsOnlySelectedTenantAndRedactsCredentials()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("person@example.invalid:synthetic-token"));
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("team.atlassian.net", request.RequestUri!.Host);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            Assert.Equal(encoded, request.Headers.Authorization.Parameter);
            if (request.RequestUri.AbsolutePath.Contains("/issue/", StringComparison.Ordinal))
                return Task.FromResult(StubHttpHandler.Json("""{"key":"PROJ-123","fields":{"summary":"synthetic-token","description":"details"}}"""));
            Assert.Equal("/wiki/api/v2/pages/123", request.RequestUri.AbsolutePath);
            Assert.Equal("?body-format=storage", request.RequestUri.Query);
            return Task.FromResult(StubHttpHandler.Json("""{"id":"123","title":"Page","body":{"storage":{"value":"person@example.invalid synthetic-token"}}}"""));
        });
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, _ => null);
        workspace.ConnectAtlassian("https://team.atlassian.net/", "person@example.invalid", "synthetic-token");
        var issue = await workspace.ReadAtlassianAsync("jira", "PROJ-123", Ct);
        var page = await workspace.ReadAtlassianAsync("confluence", "123", Ct);
        Assert.DoesNotContain("synthetic-token", issue.Content);
        Assert.DoesNotContain("person@example.invalid", page.Content);
        Assert.DoesNotContain("synthetic-token", page.Content);
        Assert.Contains("[REDACTED]", page.Content);
        var calls = handler.Calls;
        await Assert.ThrowsAsync<HarnessException>(() => workspace.ReadAtlassianAsync("jira", "../issue", Ct));
        await Assert.ThrowsAsync<HarnessException>(() => workspace.ReadAtlassianAsync("confluence", "../123", Ct));
        Assert.Equal(calls, handler.Calls);
        workspace.DisconnectAtlassian();
        await Assert.ThrowsAsync<HarnessException>(() => workspace.ReadAtlassianAsync("jira", "PROJ-123", Ct));
        Assert.Equal(calls, handler.Calls);
    }

    [Fact]
    public async Task DirectAtlassianRejectsRedirectAndMismatchedPage()
    {
        using var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json("""{"id":"999","title":"Page","body":{"storage":{"value":"body"}}}""")));
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, _ => null);
        workspace.ConnectAtlassian("https://team.atlassian.net/", "person@example.invalid", "synthetic-token");
        await Assert.ThrowsAsync<HarnessException>(() => workspace.ReadAtlassianAsync("confluence", "123", Ct));
        using var redirects = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json("{}", HttpStatusCode.Redirect)));
        using var redirectClient = new HttpClient(redirects);
        var other = new DesktopWorkspace(redirectClient, _ => null);
        other.ConnectAtlassian("https://team.atlassian.net/", "person@example.invalid", "synthetic-token");
        await Assert.ThrowsAsync<HarnessException>(() => other.ReadAtlassianAsync("confluence", "123", Ct));
        Assert.Equal(1, redirects.Calls);
    }

    [Fact]
    public async Task DirectAtlassianRotationKeepsOldBasicCredentialsOutOfChat()
    {
        var oldBasic = Convert.ToBase64String(Encoding.UTF8.GetBytes("person@example.invalid:old-personal-token"));
        using var handler = new StubHttpHandler(async request =>
        {
            Assert.Equal("api.openai.com", request.RequestUri!.Host);
            var body = await request.Content!.ReadAsStringAsync(Ct);
            Assert.DoesNotContain(oldBasic, body);
            Assert.DoesNotContain("old-personal-token", body);
            Assert.DoesNotContain("new-personal-token", body);
            return StubHttpHandler.Json("""{"status":"completed","output":[{"type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Done"}]}]}""");
        });
        using var client = new HttpClient(handler);
        var workspace = new DesktopWorkspace(client, name => name == "OPENAI_API_KEY" ? "synthetic-key" : null);
        await workspace.ConfigureAsync(null, "openai", "test-model", Ct);
        workspace.ConnectAtlassian("https://team.atlassian.net/", "person@example.invalid", "old-personal-token");
        var chat = workspace.CreateChat(false);
        await chat.SendAsync(oldBasic + " old-personal-token", [], Ct);
        workspace.DisconnectAtlassian();
        workspace.ConnectAtlassian("https://team.atlassian.net/", "person@example.invalid", "new-personal-token");
        await chat.SendAsync("new-personal-token", [], Ct);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("https://github.com/org/repo/pull/123", "github", "org/repo", 123)]
    [InlineData("https://gitlab.com/group/subgroup/repo/-/merge_requests/45", "gitlab", "group/subgroup/repo", 45)]
    public void PullRequestInputMapsToTrustedHost(string url, string platform, string project, int number)
    {
        var parsed = DesktopWorkspace.ParseChangeRequest(url);
        Assert.Equal(platform, parsed.Platform);
        Assert.Equal(project, parsed.Project);
        Assert.Equal(number, parsed.Number);
    }

    [Theory]
    [InlineData("https://github.com.evil.example/org/repo/pull/123")]
    [InlineData("http://github.com/org/repo/pull/123")]
    [InlineData("https://github.com/org/repo/pull/123?redirect=evil")]
    [InlineData("https://github.com/org/repo/pull/0")]
    public void PullRequestInputRejectsUntrustedHostsOrInvalidIdentity(string url) =>
        Assert.Throws<HarnessException>(() => DesktopWorkspace.ParseChangeRequest(url));

    [Theory]
    [InlineData("https://api.openai.com/v1/", true)]
    [InlineData("https://evil.example/v1/", false)]
    public async Task CredentialImportAcceptsOnlyFixedOfficialEndpoint(string endpoint, bool valid)
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, ".credentials");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { OpenAI = new { Endpoint = endpoint, ApiKey = "synthetic-key", ChatModel = "test-model" } }), Ct);
        if (valid)
        {
            var result = await DesktopCredentialImport.ReadAsync(path, Ct);
            Assert.Equal("openai", result.Provider);
            Assert.Equal("synthetic-key", result.Credential);
        }
        else await Assert.ThrowsAsync<HarnessException>(() => DesktopCredentialImport.ReadAsync(path, Ct));
    }

}
