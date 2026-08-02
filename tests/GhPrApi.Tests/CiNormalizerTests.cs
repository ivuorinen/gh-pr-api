using GhPrApi.GitHub;
using GhPrApi.Models;
using GhPrApi.Services;
using Xunit;

namespace GhPrApi.Tests;

public sealed class CiNormalizerTests
{
    private readonly CiNormalizer _normalizer = new();

    [Fact]
    public void Normalize_returns_pending_when_status_details_are_missing()
    {
        Assert.Equal(NormalizedValues.Ci.Pending, _normalizer.Normalize(null));
    }

    [Fact]
    public void Normalize_returns_failing_when_required_check_failed()
    {
        var details = Details(
            Required("build"),
            CheckRun("build", status: "COMPLETED", conclusion: "FAILURE"));

        Assert.Equal(NormalizedValues.Ci.Failing, _normalizer.Normalize(details));
    }

    [Fact]
    public void Normalize_returns_pending_when_required_check_is_queued()
    {
        var details = Details(
            Required("build"),
            CheckRun("build", status: "QUEUED", conclusion: null));

        Assert.Equal(NormalizedValues.Ci.Pending, _normalizer.Normalize(details));
    }

    [Fact]
    public void Normalize_returns_pending_when_required_check_is_missing()
    {
        var details = Details(
            Required("build", "test"),
            CheckRun("build", status: "COMPLETED", conclusion: "SUCCESS"));

        Assert.Equal(NormalizedValues.Ci.Pending, _normalizer.Normalize(details));
    }

    [Fact]
    public void Normalize_returns_passing_when_all_required_checks_passed()
    {
        var details = Details(
            Required("build", "test"),
            CheckRun("build", status: "COMPLETED", conclusion: "SUCCESS"),
            StatusContext("test", state: "SUCCESS"));

        Assert.Equal(NormalizedValues.Ci.Passing, _normalizer.Normalize(details));
    }

    [Fact]
    public void Normalize_uses_is_required_when_branch_protection_contexts_are_unavailable()
    {
        var details = new GitHubPullRequestStatusDetails(
            [
                CheckRun("optional", status: "COMPLETED", conclusion: "FAILURE", isRequired: false),
                CheckRun("build", status: "COMPLETED", conclusion: "SUCCESS", isRequired: true),
            ],
            [],
            RequiresStatusChecks: false);

        Assert.Equal(NormalizedValues.Ci.Passing, _normalizer.Normalize(details));
    }

    [Fact]
    public void Normalize_returns_failing_for_a_red_check_in_a_repository_with_no_branch_protection()
    {
        // Branch protection is off by default, so nothing is "required" and nothing blocks the
        // merge -- but a red build is not passing, and the Easy wins section acts on this value.
        var details = new GitHubPullRequestStatusDetails(
            [CheckRun("build", status: "COMPLETED", conclusion: "FAILURE", isRequired: false)],
            [],
            RequiresStatusChecks: false);

        Assert.Equal(NormalizedValues.Ci.Failing, _normalizer.Normalize(details));
    }

    [Fact]
    public void Normalize_returns_passing_when_a_repository_has_no_checks_at_all()
    {
        // Nothing ran and nothing is required: there is genuinely nothing in the way.
        var details = new GitHubPullRequestStatusDetails([], [], RequiresStatusChecks: false);

        Assert.Equal(NormalizedValues.Ci.Passing, _normalizer.Normalize(details));
    }

    [Fact]
    public void Normalize_returns_pending_for_an_unfinished_check_with_no_branch_protection()
    {
        var details = new GitHubPullRequestStatusDetails(
            [CheckRun("build", status: "IN_PROGRESS", conclusion: null, isRequired: false)],
            [],
            RequiresStatusChecks: false);

        Assert.Equal(NormalizedValues.Ci.Pending, _normalizer.Normalize(details));
    }

    private static GitHubPullRequestStatusDetails Details(
        IReadOnlyList<string> requiredNames,
        params GitHubStatusCheck[] checks) => new(checks, requiredNames, RequiresStatusChecks: requiredNames.Count > 0);

    private static IReadOnlyList<string> Required(params string[] names) => names;

    private static GitHubStatusCheck CheckRun(
        string name,
        string? status,
        string? conclusion,
        bool isRequired = true) => new(
            "CheckRun",
            name,
            status,
            conclusion,
            State: null,
            isRequired);

    private static GitHubStatusCheck StatusContext(
        string name,
        string? state,
        bool isRequired = true) => new(
            "StatusContext",
            name,
            Status: null,
            Conclusion: null,
            state,
            isRequired);
}
