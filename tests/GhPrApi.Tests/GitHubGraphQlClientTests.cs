using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using GhPrApi.GitHub;
using GhPrApi.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GhPrApi.Tests;

public sealed class GitHubGraphQlClientTests
{
    // GitHub's GraphQL API statically rejects queries requesting more than 500,000 possible
    // nodes -- this is a hard ceiling guard, not a performance guarantee (GitHub's backend can
    // still time out executing a query well under this limit; that can only be observed against
    // the real API, not asserted here). Reads MaxRepositoryPageSize and the labels(first: N)
    // clause from the live client/query instead of duplicating the numbers, so this can't
    // silently go stale the way it did once already (labels(first: 50), 510,100 nodes).
    [Fact]
    public void OpenPullRequestsQuery_worst_case_node_count_stays_under_githubs_500000_limit()
    {
        const int maxPrLimit = 100; // GitHub:PullRequestLimitPerRepository's validated max (Program.cs)

        var maxRepoPageSizeField = typeof(GitHubGraphQlClient).GetField("MaxRepositoryPageSize", BindingFlags.NonPublic | BindingFlags.Static);
        var maxRepoPageSize = Assert.IsType<int>(maxRepoPageSizeField?.GetValue(null));

        var queryField = typeof(GitHubGraphQlClient).GetField("OpenPullRequestsQuery", BindingFlags.NonPublic | BindingFlags.Static);
        var query = Assert.IsType<string>(queryField?.GetValue(null));
        var labelsMatch = Regex.Match(query, @"labels\(first:\s*(\d+)\)");
        Assert.True(labelsMatch.Success, "Expected to find a labels(first: N) clause in OpenPullRequestsQuery.");
        var labelsLimit = int.Parse(labelsMatch.Groups[1].Value);

        var worstCaseNodeCount = maxRepoPageSize
            + (maxRepoPageSize * maxPrLimit)
            + (maxRepoPageSize * maxPrLimit * labelsLimit);

        Assert.True(
            worstCaseNodeCount <= 500_000,
            $"OpenPullRequestsQuery can request {worstCaseNodeCount} nodes at max page sizes, exceeding GitHub's 500,000 limit.");
    }

    private const string SinglePageResponse = """
        {
          "data": {
            "repositoryOwner": {
              "repositories": {
                "pageInfo": { "hasNextPage": false, "endCursor": null },
                "nodes": [
                  {
                    "name": "example",
                    "nameWithOwner": "ivuorinen/example",
                    "owner": { "login": "ivuorinen" },
                    "pullRequests": {
                      "pageInfo": { "hasNextPage": false },
                      "nodes": [
                        {
                          "id": "PR_1",
                          "number": 1,
                          "title": "Add feature",
                          "url": "https://github.com/ivuorinen/example/pull/1",
                          "createdAt": "2026-07-06T10:00:00Z",
                          "isDraft": false,
                          "reviewDecision": null,
                          "mergeStateStatus": "CLEAN",
                          "mergeable": "MERGEABLE",
                          "headRefName": "feature/example",
                          "headRefOid": "abc123def456",
                          "baseRefName": "main",
                          "author": { "login": "ivuorinen", "__typename": "User" },
                          "labels": { "nodes": [] }
                        }
                      ]
                    }
                  }
                ]
              }
            }
          }
        }
        """;

    [Fact]
    public async Task GetOpenPullRequestsAsync_maps_a_single_page_with_no_truncation()
    {
        var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(SinglePageResponse));
        var healthState = new GitHubApiHealthState();
        var client = CreateClient(handler, healthState);

        var result = await client.GetOpenPullRequestsAsync("ivuorinen", CancellationToken.None);

        var pullRequest = Assert.Single(result.PullRequests);
        Assert.Equal("ivuorinen/example#1", $"{pullRequest.RepositoryNameWithOwner}#{pullRequest.Number}");
        Assert.False(result.Truncated);
        Assert.True(healthState.IsHealthy);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetOpenPullRequestsAsync_maps_the_head_commit_sha()
    {
        var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(SinglePageResponse));
        var client = CreateClient(handler);

        var result = await client.GetOpenPullRequestsAsync("ivuorinen", CancellationToken.None);

        var pullRequest = Assert.Single(result.PullRequests);
        Assert.Equal("abc123def456", pullRequest.HeadRefOid);
    }

    [Fact]
    public void OpenPullRequestsQuery_requests_headRefOid()
    {
        var queryField = typeof(GitHubGraphQlClient).GetField(
            "OpenPullRequestsQuery",
            BindingFlags.NonPublic | BindingFlags.Static);
        var query = Assert.IsType<string>(queryField?.GetValue(null));

        Assert.Contains("headRefOid", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOpenPullRequestsAsync_stitches_together_multiple_repository_pages()
    {
        var page1 = RepositoryPageResponse("repo-1", hasNextPage: true, endCursor: "cursor-1");
        var page2 = RepositoryPageResponse("repo-2", hasNextPage: false, endCursor: null);
        var handler = new FakeHttpMessageHandler((_, callIndex) => JsonResponse(callIndex == 1 ? page1 : page2));
        var client = CreateClient(handler);

        var result = await client.GetOpenPullRequestsAsync("ivuorinen", CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(["ivuorinen/repo-1", "ivuorinen/repo-2"], result.PullRequests.Select(static pr => pr.RepositoryNameWithOwner));
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task GetOpenPullRequestsAsync_is_truncated_when_a_repository_has_more_prs_than_the_per_repo_limit()
    {
        var response = RepositoryPageResponse("example", hasNextPage: false, endCursor: null, prHasNextPage: true);
        var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(response));
        var client = CreateClient(handler);

        var result = await client.GetOpenPullRequestsAsync("ivuorinen", CancellationToken.None);

        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task GetOpenPullRequestsAsync_is_truncated_when_the_repository_limit_is_hit_before_the_last_page()
    {
        var page = RepositoryPageResponse("example", hasNextPage: true, endCursor: "cursor-1");
        var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(page));
        var client = CreateClient(handler, repositoryLimit: 1);

        var result = await client.GetOpenPullRequestsAsync("ivuorinen", CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetOpenPullRequestsAsync_throws_and_marks_unhealthy_on_non_success_http_status()
    {
        var handler = new FakeHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom", Encoding.UTF8, "text/plain"),
        });
        var healthState = new GitHubApiHealthState();
        var client = CreateClient(handler, healthState);

        await Assert.ThrowsAsync<GitHubQueryException>(() => client.GetOpenPullRequestsAsync("ivuorinen", CancellationToken.None));

        Assert.False(healthState.IsHealthy);
    }

    [Fact]
    public async Task GetOpenPullRequestsAsync_throws_and_marks_unhealthy_on_graphql_errors()
    {
        const string errorResponse = """{ "data": null, "errors": [ { "message": "not found" } ] }""";
        var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(errorResponse));
        var healthState = new GitHubApiHealthState();
        var client = CreateClient(handler, healthState);

        await Assert.ThrowsAsync<GitHubQueryException>(() => client.GetOpenPullRequestsAsync("ivuorinen", CancellationToken.None));

        Assert.False(healthState.IsHealthy);
    }

    [Fact]
    public async Task GetOpenPullRequestsAsync_recovers_health_state_after_a_subsequent_success()
    {
        var healthState = new GitHubApiHealthState();
        healthState.RecordFailure();
        var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(SinglePageResponse));
        var client = CreateClient(handler, healthState);

        await client.GetOpenPullRequestsAsync("ivuorinen", CancellationToken.None);

        Assert.True(healthState.IsHealthy);
    }

    [Fact]
    public async Task GetOpenPullRequestsAsync_throws_when_the_owner_does_not_exist()
    {
        // GitHub answers an unknown login with a null repositoryOwner, HTTP 200 and no errors
        // array. Reporting that as an empty result made a misspelled GitHub:Owner permanently
        // indistinguishable from "this account has no open PRs".
        var handler = new FakeHttpMessageHandler((_, _) => JsonResponse("""{ "data": { "repositoryOwner": null } }"""));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubQueryException>(
            () => client.GetOpenPullRequestsAsync("ivuorinnen", CancellationToken.None));

        Assert.Contains("ivuorinnen", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOpenPullRequestsAsync_throws_before_calling_github_when_no_token_is_configured()
    {
        var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(SinglePageResponse));
        var client = CreateClient(handler, token: "");

        await Assert.ThrowsAsync<GitHubQueryException>(
            () => client.GetOpenPullRequestsAsync("ivuorinen", CancellationToken.None));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetPullRequestStatusDetailsAsync_maps_check_runs_and_status_contexts()
    {
        // CheckRun carries `name`, StatusContext carries `context`, and a node with neither is
        // unusable. Nothing covered this mapping before.
        const string response = """
            {
              "data": {
                "repository": {
                  "ref": { "branchProtectionRule": null },
                  "pullRequest": {
                    "statusCheckRollup": {
                      "contexts": {
                        "nodes": [
                          { "__typename": "CheckRun", "name": "build", "status": "COMPLETED", "conclusion": "SUCCESS", "isRequired": true },
                          { "__typename": "StatusContext", "context": "legacy/ci", "state": "SUCCESS", "isRequired": false },
                          { "__typename": "CheckRun", "name": "", "status": "COMPLETED", "conclusion": "SUCCESS", "isRequired": true }
                        ]
                      }
                    }
                  }
                }
              }
            }
            """;
        var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(response));
        var client = CreateClient(handler);

        var details = await client.GetPullRequestStatusDetailsAsync(TestPullRequests.Create(), CancellationToken.None);

        Assert.Equal(["build", "legacy/ci"], details.StatusChecks.Select(static check => check.DisplayName));
        Assert.True(details.StatusChecks[0].IsRequired);
        Assert.False(details.StatusChecks[1].IsRequired);
    }

    [Fact]
    public async Task GetPullRequestStatusDetailsAsync_merges_both_required_check_sources()
    {
        // GitHub populates requiredStatusCheckContexts (strings) or requiredStatusChecks
        // (objects) depending on how the rule was created, and casing between them is not
        // guaranteed. Both feed one case-insensitive set.
        const string response = """
            {
              "data": {
                "repository": {
                  "ref": {
                    "branchProtectionRule": {
                      "requiresStatusChecks": true,
                      "requiredStatusCheckContexts": ["Build"],
                      "requiredStatusChecks": [ { "context": "build" }, { "context": "test" } ]
                    }
                  },
                  "pullRequest": { "statusCheckRollup": null }
                }
              }
            }
            """;
        var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(response));
        var client = CreateClient(handler);

        var details = await client.GetPullRequestStatusDetailsAsync(TestPullRequests.Create(), CancellationToken.None);

        Assert.True(details.RequiresStatusChecks);
        Assert.Equal(2, details.RequiredStatusCheckNames.Count);
        Assert.Contains("test", details.RequiredStatusCheckNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPullRequestStatusDetailsAsync_handles_a_repository_with_no_branch_protection()
    {
        const string response = """
            {
              "data": {
                "repository": {
                  "ref": null,
                  "pullRequest": { "statusCheckRollup": { "contexts": { "nodes": [] } } }
                }
              }
            }
            """;
        var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(response));
        var client = CreateClient(handler);

        var details = await client.GetPullRequestStatusDetailsAsync(TestPullRequests.Create(), CancellationToken.None);

        Assert.False(details.RequiresStatusChecks);
        Assert.Empty(details.RequiredStatusCheckNames);
        Assert.Empty(details.StatusChecks);
    }

    private static string RepositoryPageResponse(string repoName, bool hasNextPage, string? endCursor, bool prHasNextPage = false) => $$"""
        {
          "data": {
            "repositoryOwner": {
              "repositories": {
                "pageInfo": { "hasNextPage": {{(hasNextPage ? "true" : "false")}}, "endCursor": {{(endCursor is null ? "null" : $"\"{endCursor}\"")}} },
                "nodes": [
                  {
                    "name": "{{repoName}}",
                    "nameWithOwner": "ivuorinen/{{repoName}}",
                    "owner": { "login": "ivuorinen" },
                    "pullRequests": {
                      "pageInfo": { "hasNextPage": {{(prHasNextPage ? "true" : "false")}} },
                      "nodes": [
                        {
                          "id": "PR_{{repoName}}",
                          "number": 1,
                          "title": "Add feature",
                          "url": "https://github.com/ivuorinen/{{repoName}}/pull/1",
                          "createdAt": "2026-07-06T10:00:00Z",
                          "isDraft": false,
                          "reviewDecision": null,
                          "mergeStateStatus": "CLEAN",
                          "mergeable": "MERGEABLE",
                          "headRefName": "feature/example",
                          "headRefOid": "abc123def456",
                          "baseRefName": "main",
                          "author": { "login": "ivuorinen", "__typename": "User" },
                          "labels": { "nodes": [] }
                        }
                      ]
                    }
                  }
                ]
              }
            }
          }
        }
        """;

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static GitHubGraphQlClient CreateClient(
        FakeHttpMessageHandler handler,
        GitHubApiHealthState? healthState = null,
        int repositoryLimit = 1000,
        int pullRequestLimitPerRepository = 100,
        string? token = "test-token")
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.test/graphql") };
        var options = new FakeOptionsMonitor<GitHubOptions>(new GitHubOptions
        {
            Owner = "ivuorinen",
            Token = token,
            RepositoryLimit = repositoryLimit,
            PullRequestLimitPerRepository = pullRequestLimitPerRepository,
            StatusCheckLimitPerPullRequest = 100,
        });

        return new GitHubGraphQlClient(httpClient, options, NullLogger<GitHubGraphQlClient>.Instance, healthState ?? new GitHubApiHealthState());
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
        private int _callCount;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var callIndex = Interlocked.Increment(ref _callCount);
            return Task.FromResult(_responder(request, callIndex));
        }
    }
}
