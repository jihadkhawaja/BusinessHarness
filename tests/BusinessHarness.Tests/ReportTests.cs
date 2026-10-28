using System.Text.Json;
using BusinessHarness.Core;
using BusinessHarness.Infrastructure;
using Xunit;

namespace BusinessHarness.Tests;

public sealed class ReportTests
{
    [Fact]
    public void TemplateOrdersFindingsAndDistinguishesMissingSpecsFromSuccess()
    {
        var report = Report() with
        {
            Findings = [new("M1", "medium", "src/b.cs", 9, "Concrete change risk", "Trigger, impact and minimal fix.") { Category = "maintainability" },
                new("S1", "high", "src/a.cs", 3, "Authorization bypass", "Missing check exposes protected data. Require authorization.") { Category = "security" }]
        };
        var markdown = ReportWriter.Markdown(report);
        Assert.Contains("## BusinessHarness review", markdown);
        Assert.Contains("2 findings to address", markdown);
        Assert.True(markdown.IndexOf("Authorization bypass", StringComparison.Ordinal) < markdown.IndexOf("Concrete change risk", StringComparison.Ordinal));
        Assert.Contains("src/a.cs:3", markdown);
        Assert.Contains("No linked specifications supplied; coverage was not assessed.", markdown);
        Assert.Contains("Findings below the configured severity threshold", markdown);
        Assert.Contains("<details><summary>Scope and gate</summary>", markdown);
    }

    [Fact]
    public void TemplateRetainsEveryReferenceAssessmentAndEscapesUntrustedContent()
    {
        var report = Report() with
        {
            Summary = "<script> @everyone [link](https://evil.invalid)",
            Passed = false,
            Findings = [new("S1", "high", "src/a.cs", 3, "<img> @owner", "[click](https://evil.invalid)") { Category = "security" }],
            Requirements = [new("SPEC-1", "met", "Evidence"), new("SPEC-2", "not_verifiable", "Needs runtime evidence")],
            ExcludedFiles = ["<private>@owner.key"]
        };
        var markdown = ReportWriter.Markdown(report);
        Assert.Contains("SPEC\\-1", markdown);
        Assert.Contains("not\\_verifiable", markdown);
        Assert.DoesNotContain("<script>", markdown);
        Assert.DoesNotContain("<img>", markdown);
        Assert.DoesNotContain("@everyone", markdown);
        Assert.DoesNotContain("@owner", markdown);
        Assert.DoesNotContain("[click](", markdown);
    }

    [Fact]
    public void MissingCategoryDefaultsToCorrectnessAndSchemaRequestsExplicitCategory()
    {
        var finding = JsonSerializer.Deserialize<Finding>("""{"ruleId":"BUG","severity":"medium","path":"a.cs","line":1,"title":"Bug","description":"Fix"}""", JsonDefaults.Options);
        Assert.Equal("correctness", finding!.Category);
        var item = ReviewContract.Schema.GetProperty("properties").GetProperty("findings").GetProperty("items");
        Assert.Contains(item.GetProperty("required").EnumerateArray(), element => element.GetString() == "category");
        Assert.Equal(3, item.GetProperty("properties").GetProperty("category").GetProperty("enum").GetArrayLength());
    }

    private static HarnessReport Report() => new("1.0", "review", "openai", "test", new string('a', 40), new string('b', 40),
        DateTimeOffset.UnixEpoch, true, "Review summary.", ["src/a.cs", "src/b.cs"], [], [], []);
}
