using GhPrApi.GitHub;

namespace GhPrApi.Tests;

internal static class TestPullRequests
{
    public static GitHubPullRequest Create(
        string repositoryNameWithOwner = "ivuorinen/example",
        int number = 1,
        string title = "Add feature",
        DateTimeOffset? createdAt = null,
        bool isDraft = false,
        string reviewDecision = "REVIEW_REQUIRED",
        string mergeStateStatus = "CLEAN",
        string mergeable = "MERGEABLE",
        string headRefName = "feature/example",
        string headRefOid = "sha-default",
        string baseRefName = "main",
        string authorLogin = "ivuorinen",
        string authorType = "User",
        IReadOnlyList<string>? labels = null,
        GitHubPullRequestStatusDetails? statusDetails = null)
    {
        var ownerAndRepo = repositoryNameWithOwner.Split('/', 2, StringSplitOptions.TrimEntries);
        var owner = ownerAndRepo.Length > 0 ? ownerAndRepo[0] : "ivuorinen";
        var repo = ownerAndRepo.Length > 1 ? ownerAndRepo[1] : "example";

        return new GitHubPullRequest(
            Id: $"PR_{number}",
            RepositoryNameWithOwner: repositoryNameWithOwner,
            RepositoryOwner: owner,
            RepositoryName: repo,
            Number: number,
            Title: title,
            Url: new Uri($"https://github.com/{repositoryNameWithOwner}/pull/{number}", UriKind.Absolute),
            CreatedAt: createdAt ?? new DateTimeOffset(2026, 7, 6, 10, 0, 0, TimeSpan.Zero),
            IsDraft: isDraft,
            ReviewDecision: reviewDecision,
            MergeStateStatus: mergeStateStatus,
            Mergeable: mergeable,
            HeadRefName: headRefName,
            HeadRefOid: headRefOid,
            BaseRefName: baseRefName,
            Author: new GitHubActor(authorLogin, authorType),
            Labels: labels ?? [],
            StatusDetails: statusDetails ?? new GitHubPullRequestStatusDetails(
                [new GitHubStatusCheck("CheckRun", "build", "COMPLETED", "SUCCESS", State: null, IsRequired: true)],
                ["build"],
                RequiresStatusChecks: true));
    }
}
