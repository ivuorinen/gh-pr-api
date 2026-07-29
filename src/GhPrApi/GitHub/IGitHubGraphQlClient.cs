namespace GhPrApi.GitHub;

public interface IGitHubGraphQlClient
{
    Task<GitHubOpenPullRequestsResult> GetOpenPullRequestsAsync(
        string owner,
        CancellationToken cancellationToken);

    Task<GitHubPullRequestStatusDetails> GetPullRequestStatusDetailsAsync(
        GitHubPullRequest pullRequest,
        CancellationToken cancellationToken);
}
