using GhPrApi.Services;
using Xunit;

namespace GhPrApi.Tests;

public sealed class SecurityDetectorTests
{
    private readonly SecurityDetector _detector = new();

    [Theory]
    [InlineData("fix CVE-2026-12345 in dependency")]
    [InlineData("GHSA-abcd-1234-efgh remediation")]
    [InlineData("Dependabot security update")]
    [InlineData("Renovate vulnerability alert")]
    public void IsSecurity_detects_security_indicators_from_title(string title)
    {
        var pr = TestPullRequests.Create(title: title);

        Assert.True(_detector.IsSecurity(pr, dependencyName: null));
    }

    [Fact]
    public void IsSecurity_detects_security_label()
    {
        var pr = TestPullRequests.Create(labels: ["dependencies", "security"]);

        Assert.True(_detector.IsSecurity(pr, dependencyName: null));
    }

    [Fact]
    public void IsSecurity_returns_false_for_normal_dependency_update()
    {
        var pr = TestPullRequests.Create(
            title: "chore(deps): update eslint to v10",
            labels: ["dependencies"]);

        Assert.False(_detector.IsSecurity(pr, dependencyName: "eslint"));
    }
}
