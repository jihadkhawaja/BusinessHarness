using System.Net;
using System.Text.Json;
using BusinessHarness.Cli;
using BusinessHarness.Core;
using BusinessHarness.Infrastructure;
using Xunit;

namespace BusinessHarness.Tests;

public sealed class ConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static ReferenceSourcePolicy Source => new()
    {
        Name = "support",
        Pattern = @"SUPPORT-(?<id>[1-9][0-9]*)",
        TitlePath = "ticket.subject",
        ContentPath = "ticket.description",
        TokenEnvironment = "SUPPORT_TOKEN",
        Connector = new()
        {
            Action = "zendesk.get_ticket",
            IdParameter = "ticket_id",
            BaseUri = "https://team.zendesk.com/"
        }
    };

    [Fact]
    public async Task ConnectorReferencesAreDiscoveredDeduplicatedAndRedacted()
    {
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://team.zendesk.com/api/v2/tickets/42.json", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-support-secret", request.Headers.Authorization.Parameter);
            return Task.FromResult(StubHttpHandler.Json("""{"ticket":{"subject":"Export","description":"Require precision synthetic-support-secret"}}"""));
        });
        using var client = new HttpClient(handler);
        var policy = new HarnessPolicy { ReferenceSources = [Source] };
        policy.Validate();
        ConnectorReferenceRequests.ValidateSources(policy.ReferenceSources);
        var resolver = new ReferenceResolver(new(client), policy, _ => "synthetic-support-secret", new(["synthetic-support-secret"]), null);
        var document = Assert.Single(await resolver.ResolveAsync("SUPPORT-42 SUPPORT-42", ["support:42"], Ct));
        Assert.Equal("support:42", document.Key);
        Assert.Equal("Export", document.Summary);
        Assert.Equal("Require precision [REDACTED]", document.Description);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void CatalogBuildsTypedFixedInputsAndProviderHeaders()
    {
        using var request = ConnectorReferenceRequests.Build(new()
        {
            Action = "github.list_issues",
            IdParameter = "repo",
            Inputs = new()
            {
                ["owner"] = JsonSerializer.SerializeToElement("team"),
                ["page"] = JsonSerializer.SerializeToElement(2)
            }
        }, "project");
        Assert.Equal("https://api.github.com/repos/team/project/issues?page=2", request.RequestUri!.AbsoluteUri);
        Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
        Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/vnd.github+json");
    }

    [Theory]
    [InlineData("github.create_issue")]
    [InlineData("linear.graphql")]
    [InlineData("github.proxy")]
    [InlineData("unknown.read")]
    public void WriteProxyAndUnknownActionsAreRejected(string action)
    {
        var failure = Assert.Throws<HarnessException>(() => ConnectorReferenceRequests.Build(new()
        {
            Action = action,
            IdParameter = "repo"
        }, "project"));
        Assert.Equal(ErrorCode.Configuration, failure.Code);
    }

    [Theory]
    [InlineData("jira.search_issues", "jql")]
    [InlineData("confluence.search", "cql")]
    [InlineData("salesforce.query", "q")]
    public void UntrustedReferenceCannotSupplyQueryLanguage(string action, string parameter)
    {
        Assert.Throws<HarnessException>(() => ConnectorReferenceRequests.Build(new()
        {
            Action = action,
            IdParameter = parameter,
            BaseUri = "https://tenant.example.com/"
        }, "status = Open OR assignee is not EMPTY"));
    }

    [Fact]
    public void ExistingSlackChannelQueryIdentifierRemainsSupported()
    {
        using var request = ConnectorReferenceRequests.Build(new() { Action = "slack.list_messages", IdParameter = "channel" }, "C123");
        Assert.Equal("https://slack.com/api/conversations.history?channel=C123", request.RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("workspace")]
    [InlineData("assignee")]
    public void ExistingAsanaQueryIdentifiersRemainSupported(string parameter)
    {
        using var request = ConnectorReferenceRequests.Build(new() { Action = "asana.list_tasks", IdParameter = parameter }, "123");
        Assert.Equal($"https://app.asana.com/api/1.0/tasks?{parameter}=123", request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ConfluenceRequestsStorageBodyThroughTheTrustedGateway()
    {
        var source = Source with
        {
            Name = "confluence",
            Pattern = @"SPEC-(?<id>[1-9][0-9]*)",
            TitlePath = "title",
            ContentPath = "body.storage.value",
            Connector = new()
            {
                Action = "confluence.get_page",
                IdParameter = "page_id",
                BaseUri = "https://api.atlassian.com/ex/confluence/trusted-cloud/",
                Inputs = new() { ["body_format"] = JsonSerializer.SerializeToElement("storage") }
            }
        };
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal("https://api.atlassian.com/ex/confluence/trusted-cloud/wiki/api/v2/pages/42?body-format=storage", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(StubHttpHandler.Json("""{"title":"Precision","body":{"storage":{"value":"<p>Keep microseconds</p>"}}}"""));
        });
        using var client = new HttpClient(handler);
        var resolver = new ReferenceResolver(new(client), new() { ReferenceSources = [source] }, _ => "synthetic-secret", new(), null);
        Assert.Equal("<p>Keep microseconds</p>", Assert.Single(await resolver.ResolveAsync("SPEC-42", [], Ct)).Description);
        Assert.Throws<HarnessException>(() => ConnectorReferenceRequests.Build(source.Connector with { Inputs = [] }, "42"));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("a/b")]
    [InlineData("%2e%2e")]
    [InlineData("a\\b")]
    [InlineData("..")]
    public void IdentifierCannotEscapeTheCatalogPath(string id)
    {
        var failure = Assert.Throws<HarnessException>(() => ConnectorReferenceRequests.Build(Source.Connector!, id));
        Assert.Equal(ErrorCode.Configuration, failure.Code);
        Assert.DoesNotContain(id, failure.Message);
    }

    [Theory]
    [InlineData("https://{id}.example.com/")]
    [InlineData("http://team.zendesk.com/")]
    [InlineData("https://user:secret@team.zendesk.com/")]
    [InlineData("https://team.zendesk.com:444/")]
    [InlineData("https://team.zendesk.com/?query=1")]
    [InlineData("https://team.zendesk.com/api")]
    public void RejectsUnsafeOrIncompleteBaseUris(string baseUri) =>
        Assert.Throws<HarnessException>(() => ConnectorReferenceRequests.Build(Source.Connector! with { BaseUri = baseUri }, "42"));

    [Fact]
    public void RequiresInstanceBaseAndValidParameters()
    {
        Assert.Throws<HarnessException>(() => ConnectorReferenceRequests.Build(Source.Connector! with { BaseUri = null }, "42"));
        Assert.Throws<HarnessException>(() => ConnectorReferenceRequests.Build(Source.Connector! with { IdParameter = "host" }, "42"));
        Assert.Throws<HarnessException>(() => ConnectorReferenceRequests.Build(new()
        {
            Action = "github.get_repository",
            IdParameter = "repo"
        }, "project"));
        Assert.Throws<HarnessException>(() => (Source with { Endpoint = "https://example.com/{id}" }).Validate());
    }

    [Theory]
    [InlineData("ticket_id", "1")]
    [InlineData("body", "{}")]
    [InlineData("extra", "null")]
    [InlineData("extra", "[]")]
    [InlineData("extra", "true")]
    public void RejectsConflictingStructuredAndUnknownFixedInputs(string name, string json)
    {
        var configuration = Source.Connector! with { Inputs = new() { [name] = JsonSerializer.Deserialize<JsonElement>(json) } };
        Assert.Equal(ErrorCode.Configuration, Assert.Throws<HarnessException>(() => ConnectorReferenceRequests.Build(configuration, "42")).Code);
    }

    [Theory]
    [InlineData("{}", 200, ErrorCode.ProviderResponse)]
    [InlineData("{bad json", 200, ErrorCode.ProviderResponse)]
    [InlineData("{}", 401, ErrorCode.Authentication)]
    [InlineData("{}", 302, ErrorCode.Network)]
    [InlineData("{}", 429, ErrorCode.Network)]
    public async Task ConnectorFailuresRemainTypedAndAreNotRetried(string response, int status, ErrorCode code)
    {
        using var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json(response, (HttpStatusCode)status)));
        using var client = new HttpClient(handler);
        var resolver = new ReferenceResolver(new(client), new() { ReferenceSources = [Source] }, _ => "synthetic-secret", new(), null);
        Assert.Equal(code, (await Assert.ThrowsAsync<HarnessException>(() => resolver.ResolveAsync("SUPPORT-42", [], Ct))).Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ConnectorReferencesRetainResponseAndDocumentLimits()
    {
        foreach (var size in new[] { 32_001, 1_000_001 })
        {
            using var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json(JsonSerializer.Serialize(new
            {
                ticket = new { subject = "Export", description = new string('x', size) }
            }))));
            using var client = new HttpClient(handler);
            var resolver = new ReferenceResolver(new(client), new() { ReferenceSources = [Source] }, _ => "synthetic-secret", new(), null);
            Assert.Equal(ErrorCode.LimitExceeded, (await Assert.ThrowsAsync<HarnessException>(() => resolver.ResolveAsync("SUPPORT-42", [], Ct))).Code);
        }
    }

    [Fact]
    public async Task ValidateRejectsConnectorConfigurationBeforeRequiringApiCredentials()
    {
        using var temp = new TempDirectory();
        var config = Path.Combine(temp.Path, "policy.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new HarnessPolicy
        {
            ReferenceSources = [Source with { Connector = Source.Connector! with { Action = "zendesk.create_ticket" } }]
        }, JsonDefaults.Options), Ct);
        var stderr = new StringWriter();
        var code = await new CliApplication(_ => null).RunAsync(["validate", "--config", config, "--output", temp.Path], new StringWriter(), stderr, Ct);
        Assert.Equal(2, code);
        Assert.Contains("connector reference action", stderr.ToString());
        Assert.DoesNotContain("OPENAI_API_KEY", stderr.ToString());
    }

    [Fact]
    public async Task ValidateAcceptsConnectorPolicyAndRequiresItsSecretWithoutApiCalls()
    {
        using var temp = new TempDirectory();
        var config = Path.Combine(temp.Path, "policy.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new HarnessPolicy
        {
            Model = "test-model",
            ReferenceSources = [Source]
        }, JsonDefaults.Options), Ct);
        var environment = new Dictionary<string, string> { ["OPENAI_API_KEY"] = "synthetic-key" };
        var stderr = new StringWriter();
        var cli = new CliApplication(environment.GetValueOrDefault);
        Assert.Equal(2, await cli.RunAsync(["validate", "--config", config, "--output", temp.Path], new StringWriter(), stderr, Ct));
        Assert.Contains("SUPPORT_TOKEN", stderr.ToString());
        environment["SUPPORT_TOKEN"] = "synthetic-support-secret";
        Assert.Equal(0, await cli.RunAsync(["validate", "--config", config, "--output", temp.Path], new StringWriter(), new StringWriter(), Ct));
    }
}
