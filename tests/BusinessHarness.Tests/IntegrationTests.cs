using System.Net;
using System.Text.Json;
using BusinessHarness.Application;
using BusinessHarness.Cli;
using BusinessHarness.Core;
using BusinessHarness.Infrastructure;
using Xunit;

namespace BusinessHarness.Tests;

public sealed class IntegrationTests
{
    private static readonly string Base = new('a', 40);
    private static readonly string Head = new('b', 40);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static ReferenceSourcePolicy Source => new()
    {
        Name = "specs",
        Pattern = @"https://docs\.example\.com/specs/(?<id>[A-Z0-9-]+)",
        Endpoint = "https://api.example.com/specs/{id}",
        TitlePath = "data.title",
        ContentPath = "data.content",
        TokenEnvironment = "SPEC_TOKEN"
    };

    [Theory]
    [InlineData("custom-secret-value")]
    [InlineData("abc1234")]
    public async Task DiscoversJiraAndCustomReferencesFromMetadataAndDeduplicatesExplicitOnes(string secret)
    {
        var jira = new JiraStub();
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal("https://api.example.com/specs/ABC-2", request.RequestUri!.AbsoluteUri);
            Assert.Equal(secret, request.Headers.Authorization!.Parameter);
            return Task.FromResult(StubHttpHandler.Json(JsonSerializer.Serialize(new { data = new { title = "Spec", content = "Requirement " + secret } })));
        });
        using var client = new HttpClient(handler);
        var policy = new HarnessPolicy { JiraSite = "https://team.atlassian.net", ReferenceSources = [Source] };
        policy.Validate();
        var resolver = new ReferenceResolver(new(client), policy, _ => secret, new([secret]), jira);
        var documents = await resolver.ResolveAsync("PROJ-1: implement https://docs.example.com/specs/ABC-2\nPROJ-1 https://docs.example.com/specs/ABC-2", ["PROJ-1", "specs:ABC-2"], Ct);
        Assert.Equal(2, documents.Count);
        Assert.Equal(["PROJ-1"], jira.Keys);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("Requirement [REDACTED]", documents.Single(d => d.Key == "specs:ABC-2").Description);
    }

    [Fact]
    public async Task ExplicitIdentifiersAreEscapedIntoTrustedEndpoint()
    {
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal("api.example.com", request.RequestUri!.Host);
            Assert.Contains("%2F", request.RequestUri.AbsoluteUri);
            Assert.Contains("%3F", request.RequestUri.AbsoluteUri);
            return Task.FromResult(StubHttpHandler.Json("""{"data":{"title":"Spec","content":"Requirement"}}"""));
        });
        using var client = new HttpClient(handler);
        var resolver = new ReferenceResolver(new(client), new() { ReferenceSources = [Source] }, _ => "token", new(), null);
        Assert.Single(await resolver.ResolveAsync("", ["specs:a/b?c"], Ct));
    }

    [Theory]
    [InlineData("{}", 200, ErrorCode.ProviderResponse)]
    [InlineData("{\"data\":{\"title\":\"Spec\",\"content\":\"\"}}", 200, ErrorCode.ProviderResponse)]
    [InlineData("{}", 404, ErrorCode.Network)]
    [InlineData("{}", 302, ErrorCode.Network)]
    public async Task FailedReferenceFetchCannotBeSilentlySkipped(string response, int status, ErrorCode code)
    {
        using var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json(response, (HttpStatusCode)status)));
        using var client = new HttpClient(handler);
        var resolver = new ReferenceResolver(new(client), new() { ReferenceSources = [Source] }, _ => "token", new(), null);
        var failure = await Assert.ThrowsAsync<HarnessException>(() => resolver.ResolveAsync("https://docs.example.com/specs/ABC-1", [], Ct));
        Assert.Equal(code, failure.Code);
    }

    [Fact]
    public async Task ArbitraryLinksAreNotFetchedAndMissingSourcesFailExplicitReferences()
    {
        using var handler = new StubHttpHandler(_ => throw new InvalidOperationException("No network expected"));
        using var client = new HttpClient(handler);
        var resolver = new ReferenceResolver(new(client), new(), _ => null, new(), null);
        Assert.Empty(await resolver.ResolveAsync("https://evil.invalid/private PROJ-1", [], Ct));
        await Assert.ThrowsAsync<HarnessException>(() => resolver.ResolveAsync("", ["unknown:1"], Ct));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ReferenceCountLimitIsCheckedBeforeAnyFetch()
    {
        using var handler = new StubHttpHandler(_ => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        var resolver = new ReferenceResolver(new(client), new() { ReferenceSources = [Source] }, _ => "token", new(), null);
        var text = string.Join(' ', Enumerable.Range(1, 21).Select(i => "https://docs.example.com/specs/ABC-" + i));
        Assert.Equal(ErrorCode.LimitExceeded, (await Assert.ThrowsAsync<HarnessException>(() => resolver.ResolveAsync(text, [], Ct))).Code);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("http://api.example.com/{id}")]
    [InlineData("https://{id}.example.com/item")]
    [InlineData("https://api.example.com@evil.invalid/{id}")]
    [InlineData("https://127.0.0.1/{id}")]
    [InlineData("https://api.example.com/{id}#fragment")]
    public void RejectsUnsafeReferenceRouting(string endpoint) =>
        Assert.Throws<HarnessException>(() => (Source with { Endpoint = endpoint }).Validate());

    [Theory]
    [InlineData("comment", true, false, false, "COMMENT")]
    [InlineData("comment", false, false, false, "COMMENT")]
    [InlineData("auto", true, false, false, "APPROVE")]
    [InlineData("auto", false, false, false, "REQUEST_CHANGES")]
    [InlineData("auto", true, true, false, "COMMENT")]
    [InlineData("auto", true, false, true, "COMMENT")]
    public async Task GitHubFeedbackUsesGateAndPinsReviewedCommit(string mode, bool passed, bool draft, bool excluded, string decision)
    {
        var posts = 0;
        using var handler = new StubHttpHandler(async request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            if (request.Method == HttpMethod.Get) return Metadata("github", draft: draft);
            posts++;
            Assert.Equal(mode == "comment" ? "https://api.github.com/repos/org/repo/issues/12/comments" :
                "https://api.github.com/repos/org/repo/pulls/12/reviews", request.RequestUri!.AbsoluteUri);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(Ct));
            if (mode == "comment")
            {
                Assert.False(payload.RootElement.TryGetProperty("commit_id", out _));
                Assert.False(payload.RootElement.TryGetProperty("event", out _));
            }
            else
            {
                Assert.Equal(Head, payload.RootElement.GetProperty("commit_id").GetString());
                Assert.Equal(decision, payload.RootElement.GetProperty("event").GetString());
            }
            var text = payload.RootElement.GetProperty("body").GetString();
            Assert.DoesNotContain("custom-secret-value", text);
            Assert.DoesNotContain("@everyone", text);
            return StubHttpHandler.Json("{}");
        });
        using var client = new HttpClient(handler);
        var host = Host(client, "github");
        await host.PublishAsync(await host.ReadAsync(Ct), Report(passed, excluded), mode, Ct);
        Assert.Equal(1, posts);
    }

    [Theory]
    [InlineData("comment")]
    [InlineData("auto")]
    public async Task CommentPublicationFailureIsNeverRetried(string mode)
    {
        var posts = 0;
        using var handler = new StubHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Metadata("github"));
            posts++;
            return Task.FromResult(StubHttpHandler.Json("{}", HttpStatusCode.Forbidden));
        });
        using var client = new HttpClient(handler);
        var host = Host(client, "github");
        var reviewed = await host.ReadAsync(Ct);
        await Assert.ThrowsAsync<HarnessException>(() => host.PublishAsync(reviewed, Report(true), mode, Ct));
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task ConversationRoutingDoesNotRewriteProjectSegments()
    {
        using var handler = new StubHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Metadata("github"));
            Assert.Equal("https://api.github.com/repos/pulls/pulls/issues/12/comments", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(StubHttpHandler.Json("{}"));
        });
        using var client = new HttpClient(handler);
        var host = new ChangeRequestHost(new(client), new() { Project = "pulls/pulls", Number = 12 }, "host-token", new());
        await host.PublishAsync(await host.ReadAsync(Ct), Report(true), "comment", Ct);
        Assert.Equal(3, handler.Calls); // initial metadata, fresh metadata, one comment
    }

    [Theory]
    [InlineData("sha")]
    [InlineData("base")]
    [InlineData("description")]
    [InlineData("title")]
    [InlineData("draft")]
    [InlineData("closed")]
    public async Task WithholdsFeedbackWhenRequestChanges(string changed)
    {
        var reads = 0;
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Metadata("github", ++reads > 1 ? changed : ""));
        });
        using var client = new HttpClient(handler);
        var host = Host(client, "github");
        var original = await host.ReadAsync(Ct);
        await Assert.ThrowsAsync<HarnessException>(() => host.PublishAsync(original, Report(true), "auto", Ct));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ConversationCommentIsWithheldWhenHeadChanges()
    {
        var reads = 0;
        using var handler = new StubHttpHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Metadata("github", ++reads == 1 ? "" : "sha"));
        });
        using var client = new HttpClient(handler);
        var host = Host(client, "github");
        var reviewed = await host.ReadAsync(Ct);
        await Assert.ThrowsAsync<HarnessException>(() => host.PublishAsync(reviewed, Report(true), "comment", Ct));
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GitLabApprovesWithShaOrRemovesOwnApprovalAndComments(bool passed)
    {
        List<string> calls = [];
        using var handler = new StubHttpHandler(async request =>
        {
            Assert.Equal("host-token", request.Headers.GetValues("PRIVATE-TOKEN").Single());
            if (request.Method == HttpMethod.Get) return Metadata("gitlab");
            var action = request.RequestUri!.Segments.Last();
            calls.Add(action);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(Ct));
            if (action == "approve") Assert.Equal(Head, payload.RootElement.GetProperty("sha").GetString());
            return StubHttpHandler.Json("{}");
        });
        using var client = new HttpClient(handler);
        var host = Host(client, "gitlab");
        await host.PublishAsync(await host.ReadAsync(Ct), Report(passed), "auto", Ct);
        Assert.Equal(passed ? ["notes", "approve"] : new[] { "unapprove", "notes" }, calls);
    }

    [Fact]
    public async Task CompleteReviewUsesHostBaseDiscoversReferencesAndPublishesOnlyValidatedResult()
    {
        using var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.Host == "api.example.com")
                return Task.FromResult(StubHttpHandler.Json("""{"data":{"title":"Spec","content":"Must implement feature"}}"""));
            return Task.FromResult(request.Method == HttpMethod.Get ? Metadata("github") : StubHttpHandler.Json("{}"));
        });
        using var client = new HttpClient(handler);
        var source = new ChangesStub();
        var provider = new ProviderStub();
        var writer = new WriterStub();
        var resolver = new ReferenceResolver(new(client), new() { ReferenceSources = [Source] }, _ => "token", new(), null);
        var useCase = new ReviewUseCase(source, provider, null, writer, TimeProvider.System, resolver, Host(client, "github"), "auto");
        var report = await useCase.ExecuteAsync("review", "wrong-base", "HEAD", [], new(), "unused", Ct);
        Assert.True(report.Passed);
        Assert.Equal(Base, source.BaseRef);
        Assert.Equal("specs:ABC-2", Assert.Single(provider.Documents!).Key);
        Assert.NotNull(provider.Changes!.ChangeRequest);
        Assert.Same(report, writer.Report);
        Assert.Equal(4, handler.Calls); // metadata, reference, fresh metadata, review publication
    }

    [Fact]
    public async Task InvalidReviewNeverPublishesFeedback()
    {
        var host = new HostStub();
        var useCase = new ReviewUseCase(new ChangesStub(), new BadProviderStub(), null, new WriterStub(), TimeProvider.System,
            changeRequestHost: host, feedback: "auto");
        await Assert.ThrowsAsync<HarnessException>(() => useCase.ExecuteAsync("review", "base", "head", [], new(), "unused", Ct));
        Assert.False(host.Published);
    }

    [Fact]
    public async Task UnmatchedHeadNeverPublishesOrCallsProvider()
    {
        var host = new HostStub();
        var provider = new ProviderStub();
        var useCase = new ReviewUseCase(new WrongHeadStub(), provider, null, new WriterStub(), TimeProvider.System,
            changeRequestHost: host, feedback: "auto");
        await Assert.ThrowsAsync<HarnessException>(() => useCase.ExecuteAsync("review", "base", "head", [], new(), "unused", Ct));
        Assert.Null(provider.Changes);
        Assert.False(host.Published);
    }

    [Fact]
    public async Task PublishingFailuresAreReportedWithoutRetry()
    {
        var posts = 0;
        using var handler = new StubHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Metadata("github"));
            posts++;
            return Task.FromResult(StubHttpHandler.Json("{}", HttpStatusCode.Forbidden));
        });
        using var client = new HttpClient(handler);
        var host = Host(client, "github");
        var reviewed = await host.ReadAsync(Ct);
        Assert.Equal(ErrorCode.Authentication, (await Assert.ThrowsAsync<HarnessException>(() => host.PublishAsync(reviewed, Report(true), "auto", Ct))).Code);
        Assert.Equal(1, posts);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"title\":\"Title\",\"body\":42}")]
    public async Task InvalidMetadataCannotBecomeAnEmptySuccessfulReview(string response)
    {
        using var handler = new StubHttpHandler(_ => Task.FromResult(StubHttpHandler.Json(response)));
        using var client = new HttpClient(handler);
        Assert.Equal(ErrorCode.ProviderResponse, (await Assert.ThrowsAsync<HarnessException>(() => Host(client, "github").ReadAsync(Ct))).Code);
    }

    [Theory]
    [InlineData("--change", "https://github.com/org/repo/pull/12")]
    [InlineData("--reference", "specs:1")]
    [InlineData("--feedback", "auto")]
    public void ScanCannotFetchContextOrWriteFeedback(string option, string value) =>
        Assert.Throws<HarnessException>(() => CliOptions.Parse(["scan", "--base", "HEAD", option, value], _ => null));

    [Theory]
    [InlineData("github", "https://github.com/org/repo/pull/12")]
    [InlineData("gitlab", "https://gitlab.com/group/sub/repo/-/merge_requests/12")]
    public async Task PublicChangeUrlsSelectHost(string platform, string url)
    {
        var options = await ChangeRequestOptions.ResolveAsync(url, null, _ => null, Ct);
        Assert.Equal(platform, options!.Platform);
        Assert.Equal(12, options.Number);
    }

    [Fact]
    public async Task DiscoversGitHubAndGitLabCiRequests()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "event.json");
        await File.WriteAllTextAsync(path, "{\"number\":12}", Ct);
        var env = new Dictionary<string, string> { ["GITHUB_EVENT_NAME"] = "pull_request", ["GITHUB_EVENT_PATH"] = path, ["GITHUB_REPOSITORY"] = "org/repo" };
        Assert.Equal("org/repo", (await ChangeRequestOptions.ResolveAsync(null, null, env.GetValueOrDefault, Ct))!.Project);
        env = new() { ["CI_PIPELINE_SOURCE"] = "merge_request_event", ["CI_MERGE_REQUEST_IID"] = "12", ["CI_PROJECT_PATH"] = "group/sub/repo" };
        Assert.Equal("gitlab", (await ChangeRequestOptions.ResolveAsync(null, null, env.GetValueOrDefault, Ct))!.Platform);
        env["CI_SERVER_URL"] = "https://private.example.com";
        await Assert.ThrowsAsync<HarnessException>(() => ChangeRequestOptions.ResolveAsync(null, null, env.GetValueOrDefault, Ct));
        var configured = new ChangeRequestPolicy { Platform = "gitlab", ApiBase = "https://private.example.com/api/v4/", TokenEnvironment = "BOT_TOKEN" };
        configured.Validate();
        var privateHost = await ChangeRequestOptions.ResolveAsync(null, configured, env.GetValueOrDefault, Ct);
        Assert.Equal(12, privateHost!.Number);
        Assert.Equal("group/sub/repo", privateHost.Project);
        Assert.Equal(configured.ApiBase, privateHost.ApiBase);
    }

    private static ChangeRequestHost Host(HttpClient client, string platform) => new(new(client),
        new() { Platform = platform, Project = "org/repo", Number = 12 }, "host-token", new(["custom-secret-value"]));

    private static HttpResponseMessage Metadata(string platform, string changed = "", bool draft = false) =>
        StubHttpHandler.Json(JsonSerializer.Serialize(new
        {
            title = changed == "title" ? "changed" : "Implement feature",
            body = changed == "description" ? "changed" : "https://docs.example.com/specs/ABC-2",
            description = "https://docs.example.com/specs/ABC-2",
            head = new { sha = changed == "sha" ? new string('c', 40) : Head },
            sha = Head,
            @base = new { sha = changed == "base" ? new string('d', 40) : Base },
            diff_refs = new { base_sha = Base },
            draft = draft || changed == "draft",
            state = changed == "closed" ? "closed" : platform == "github" ? "open" : "opened"
        }));

    private static HarnessReport Report(bool passed, bool excluded = false) => new("1.0", "review", "openai", "test", Base, Head,
        DateTimeOffset.UtcNow, passed, "Reviewed @everyone custom-secret-value", ["sample.cs"], excluded ? [".env"] : [], [], []);

    private sealed class JiraStub : IIssueSource
    {
        public List<string> Keys { get; } = [];
        public Task<ReferenceDocument> GetIssueAsync(string key, CancellationToken cancellationToken)
        {
            Keys.Add(key);
            return Task.FromResult(new ReferenceDocument(key, "Issue", "Requirement"));
        }
    }

    private sealed class ChangesStub : IChangeSource
    {
        public string? BaseRef { get; private set; }
        public Task<ChangeSet> ReadChangesAsync(string baseRef, string headRef, CancellationToken cancellationToken)
        {
            BaseRef = baseRef;
            return Task.FromResult(new ChangeSet(Base, Head, [new("sample.cs", "+line", [1])], [], []));
        }
    }

    private sealed class WrongHeadStub : IChangeSource
    {
        public Task<ChangeSet> ReadChangesAsync(string baseRef, string headRef, CancellationToken cancellationToken) =>
            Task.FromResult(new ChangeSet(Base, new string('c', 40), [], [], []));
    }

    private sealed class ProviderStub : IReviewProvider
    {
        public IReadOnlyList<ReferenceDocument>? Documents { get; private set; }
        public ChangeSet? Changes { get; private set; }
        public Task<ReviewResult> ReviewAsync(ChangeSet changes, IReadOnlyList<ReferenceDocument> issues, CancellationToken cancellationToken)
        {
            Documents = issues;
            Changes = changes;
            return Task.FromResult(new ReviewResult("Reviewed", [], issues.Select(d => new RequirementAssessment(d.Key, "met", "Implemented in added line")).ToList()));
        }
    }

    private sealed class BadProviderStub : IReviewProvider
    {
        public Task<ReviewResult> ReviewAsync(ChangeSet changes, IReadOnlyList<ReferenceDocument> issues, CancellationToken cancellationToken) =>
            Task.FromResult(new ReviewResult("Invalid", [new("BUG", "high", "unknown.cs", 1, "Bad", "Bad")], []));
    }

    private sealed class HostStub : IChangeRequestHost
    {
        public bool Published { get; private set; }
        public Task<ChangeRequestContext> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(new ChangeRequestContext("Title", "", Base, Head, false, true));
        public Task PublishAsync(ChangeRequestContext reviewed, HarnessReport report, string feedback, CancellationToken cancellationToken)
        {
            Published = true;
            return Task.CompletedTask;
        }
    }

    private sealed class WriterStub : IReportWriter
    {
        public HarnessReport? Report { get; private set; }
        public Task WriteAsync(HarnessReport report, string outputDirectory, CancellationToken cancellationToken)
        {
            Report = report;
            return Task.CompletedTask;
        }
    }
}
