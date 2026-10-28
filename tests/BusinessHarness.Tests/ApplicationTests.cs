using BusinessHarness.Application;
using BusinessHarness.Core;
using Xunit;

namespace BusinessHarness.Tests;

public sealed class ApplicationTests
{
    private static ChangeSet Changes => new("base", "head", [new("sample.cs", "+line", [2])], [], []);

    [Theory]
    [InlineData("met", true)]
    [InlineData("partial", false)]
    [InlineData("missing", false)]
    [InlineData("not_verifiable", false)]
    public async Task RequirementsGateTreatsUnverifiableAsFailure(string status, bool passed)
    {
        var writer = new MemoryWriter();
        var result = new ReviewResult("Reviewed", [], [new("PROJ-1", status, "Evidence from diff")]);
        var useCase = new ReviewUseCase(new ChangesStub(Changes), new ProviderStub(result), new IssuesStub(), writer, TimeProvider.System);
        var report = await useCase.ExecuteAsync("review", "base", "head", ["PROJ-1"], new(), "unused", TestContext.Current.CancellationToken);
        Assert.Equal(passed, report.Passed);
        Assert.Same(report, writer.Report);
    }

    [Fact]
    public async Task LocalCriticalFindingCannotBeSuppressedByModel()
    {
        var changes = Changes with { SecurityFindings = [new("BH001", "critical", "sample.cs", 2, "Secret", "Remove it")] };
        var useCase = new ReviewUseCase(new ChangesStub(changes), new ProviderStub(new("All clear", [], [])), null, new MemoryWriter(), TimeProvider.System);
        Assert.False((await useCase.ExecuteAsync("review", "base", "head", [], new(), "unused", TestContext.Current.CancellationToken)).Passed);
    }

    [Fact]
    public void RejectsFindingsOnUnchangedLines()
    {
        var result = new ReviewResult("Review", [new("BUG", "high", "sample.cs", 999, "Bug", "Details")], []);
        Assert.Equal(ErrorCode.ProviderResponse, Assert.Throws<HarnessException>(() => ReviewValidation.Validate(result, Changes, [])).Code);
    }

    [Fact]
    public void RejectsInventedFilePaths()
    {
        var result = new ReviewResult("Review", [new("BUG", "high", "../outside.cs", 2, "Bug", "Details")], []);
        Assert.Throws<HarnessException>(() => ReviewValidation.Validate(result, Changes, []));
    }

    [Fact]
    public void MissingIssueAssessmentCannotPass()
    {
        Assert.Throws<HarnessException>(() => ReviewValidation.Validate(new("Review", [], []), Changes, [new("PROJ-1", "Summary", "Text")]));
    }

    [Theory]
    [InlineData("style")]
    [InlineData("<script>")]
    [InlineData(null)]
    public void RejectsUnsupportedFindingCategories(string? category)
    {
        var finding = new Finding("BUG", "high", "sample.cs", 2, "Bug", "Details") { Category = category! };
        Assert.Throws<HarnessException>(() => ReviewValidation.Validate(new("Review", [finding], []), Changes, []));
    }

    [Fact]
    public void ArchitectureDependenciesPointInwards()
    {
        var domainReferences = typeof(Finding).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(domainReferences, a => a.Name!.StartsWith("BusinessHarness.", StringComparison.Ordinal));
        var applicationReferences = typeof(ReviewUseCase).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(applicationReferences, a => a.Name is "BusinessHarness.Infrastructure" or "BusinessHarness.Cli");
    }

    private sealed class ChangesStub(ChangeSet changes) : IChangeSource
    {
        public Task<ChangeSet> ReadChangesAsync(string baseRef, string headRef, CancellationToken cancellationToken) => Task.FromResult(changes);
    }
    private sealed class ProviderStub(ReviewResult result) : IReviewProvider
    {
        public Task<ReviewResult> ReviewAsync(ChangeSet changes, IReadOnlyList<ReferenceDocument> issues, CancellationToken cancellationToken) => Task.FromResult(result);
    }
    private sealed class IssuesStub : IIssueSource
    {
        public Task<ReferenceDocument> GetIssueAsync(string key, CancellationToken cancellationToken) => Task.FromResult(new ReferenceDocument(key, "Summary", "Requirements"));
    }
    private sealed class MemoryWriter : IReportWriter
    {
        public HarnessReport? Report { get; private set; }
        public Task WriteAsync(HarnessReport report, string outputDirectory, CancellationToken cancellationToken) { Report = report; return Task.CompletedTask; }
    }
}
