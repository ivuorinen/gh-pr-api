using GhPrApi.Services;
using Xunit;

namespace GhPrApi.Tests;

public sealed class RobotDetectorTests
{
    private readonly RobotDetector _detector = new();

    [Theory]
    [InlineData("renovate[bot]", "Bot")]
    [InlineData("dependabot[bot]", "User")]
    [InlineData("github-actions[bot]", "User")]
    public void IsRobot_detects_automation(string login, string typeName)
    {
        var pr = TestPullRequests.Create(authorLogin: login, authorType: typeName);

        Assert.True(_detector.IsRobot(pr));
    }

    [Fact]
    public void IsRobot_returns_false_for_user()
    {
        var pr = TestPullRequests.Create(authorLogin: "ivuorinen", authorType: "User");

        Assert.False(_detector.IsRobot(pr));
    }
}
