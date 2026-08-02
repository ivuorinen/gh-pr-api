using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GhPrApi.Options;
using Microsoft.Extensions.Options;

namespace GhPrApi.GitHub;

public sealed class GitHubGraphQlClient : IGitHubGraphQlClient
{
    private const int MaxLoggedErrorBodyLength = 500;

    // GitHub's GraphQL API statically rejects queries whose nested "first" values multiply out
    // past 500,000 possible nodes -- but in practice, GitHub's backend times out executing a
    // query well before that static ceiling (observed: a consistent ~10.5s-then-502 at 210,100
    // nodes, across every retry attempt, meaning it wasn't transient). Keeping this page size
    // small keeps each individual request cheap to execute; RepositoryLimit still controls the
    // total repos scanned, just spread across more, smaller requests via the existing cursor
    // pagination loop instead of fewer, heavier ones.
    private const int MaxRepositoryPageSize = 10;

    private const string OpenPullRequestsQuery = """
        query OpenPullRequests($owner: String!, $repoCursor: String, $repoPageSize: Int!, $prLimit: Int!) {
          repositoryOwner(login: $owner) {
            repositories(
              first: $repoPageSize
              after: $repoCursor
              isArchived: false
              visibility: PUBLIC
              orderBy: { field: NAME, direction: ASC }
            ) {
              pageInfo {
                hasNextPage
                endCursor
              }
              nodes {
                name
                nameWithOwner
                owner {
                  login
                }
                pullRequests(
                  first: $prLimit
                  states: OPEN
                  orderBy: { field: CREATED_AT, direction: ASC }
                ) {
                  pageInfo {
                    hasNextPage
                  }
                  nodes {
                    id
                    number
                    title
                    url
                    createdAt
                    isDraft
                    reviewDecision
                    mergeStateStatus
                    mergeable
                    headRefName
                    headRefOid
                    baseRefName
                    author {
                      login
                      __typename
                    }
                    labels(first: 20) {
                      nodes {
                        name
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private const string PullRequestStatusQuery = """
        query PullRequestStatus($owner: String!, $name: String!, $number: Int!, $baseRefQualifiedName: String!, $contextLimit: Int!) {
          repository(owner: $owner, name: $name) {
            ref(qualifiedName: $baseRefQualifiedName) {
              branchProtectionRule {
                requiresStatusChecks
                requiredStatusCheckContexts
                requiredStatusChecks {
                  context
                }
              }
            }
            pullRequest(number: $number) {
              statusCheckRollup {
                contexts(first: $contextLimit) {
                  nodes {
                    __typename
                    ... on CheckRun {
                      name
                      status
                      conclusion
                      isRequired(pullRequestNumber: $number)
                    }
                    ... on StatusContext {
                      context
                      state
                      isRequired(pullRequestNumber: $number)
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<GitHubOptions> _options;
    private readonly ILogger<GitHubGraphQlClient> _logger;
    private readonly GitHubApiHealthState _healthState;

    public GitHubGraphQlClient(
        HttpClient httpClient,
        IOptionsMonitor<GitHubOptions> options,
        ILogger<GitHubGraphQlClient> logger,
        GitHubApiHealthState healthState)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
        _healthState = healthState;
    }

    public async Task<GitHubOpenPullRequestsResult> GetOpenPullRequestsAsync(
        string owner,
        CancellationToken cancellationToken)
    {
        EnsureTokenConfigured();

        var configuredOptions = _options.CurrentValue;
        var repoPageSize = Math.Min(MaxRepositoryPageSize, configuredOptions.RepositoryLimit);
        var repositoryLimit = configuredOptions.RepositoryLimit;
        var prLimit = configuredOptions.PullRequestLimitPerRepository;

        var pullRequests = new List<GitHubPullRequest>();
        string? repoCursor = null;
        var repositoriesRead = 0;
        var hasNextPage = true;
        var truncated = false;

        while (hasNextPage && repositoriesRead < repositoryLimit)
        {
            var remainingRepositories = repositoryLimit - repositoriesRead;
            var currentPageSize = Math.Min(repoPageSize, remainingRepositories);

            var response = await ExecuteAsync<OpenPullRequestsData>(
                OpenPullRequestsQuery,
                new
                {
                    owner,
                    repoCursor,
                    repoPageSize = currentPageSize,
                    prLimit,
                },
                cancellationToken).ConfigureAwait(false);

            var repositories = response.RepositoryOwner?.Repositories;
            if (repositories is null)
            {
                // GitHub answers an unknown login with a null repositoryOwner, HTTP 200 and no
                // errors array, so this is the only place a misspelled, renamed or deleted owner
                // can be detected. Returning an empty result would make that misconfiguration
                // byte-identical to a legitimate "no open PRs" -- permanently green, permanently
                // empty, with nothing for an operator to notice.
                throw new GitHubQueryException($"GitHub owner '{owner}' was not found.");
            }

            foreach (var repository in repositories.Nodes.Where(static node => node is not null).Cast<RepositoryNode>())
            {
                repositoriesRead++;

                if (repository.PullRequests.PageInfo.HasNextPage)
                {
                    truncated = true;
                }

                foreach (var pr in repository.PullRequests.Nodes.Where(static node => node is not null).Cast<PullRequestNode>())
                {
                    pullRequests.Add(MapPullRequest(repository, pr));
                }
            }

            hasNextPage = repositories.PageInfo.HasNextPage;
            repoCursor = repositories.PageInfo.EndCursor;
        }

        if (hasNextPage)
        {
            truncated = true;
        }

        return new GitHubOpenPullRequestsResult(pullRequests, truncated);
    }

    public async Task<GitHubPullRequestStatusDetails> GetPullRequestStatusDetailsAsync(
        GitHubPullRequest pullRequest,
        CancellationToken cancellationToken)
    {
        EnsureTokenConfigured();

        var response = await ExecuteAsync<PullRequestStatusData>(
            PullRequestStatusQuery,
            new
            {
                owner = pullRequest.RepositoryOwner,
                name = pullRequest.RepositoryName,
                number = pullRequest.Number,
                baseRefQualifiedName = $"refs/heads/{pullRequest.BaseRefName}",
                contextLimit = _options.CurrentValue.StatusCheckLimitPerPullRequest,
            },
            cancellationToken).ConfigureAwait(false);

        var nodes = response.Repository?.PullRequest?.StatusCheckRollup?.Contexts?.Nodes ?? [];
        var checks = nodes
            .Where(static node => node is not null)
            .Cast<StatusCheckNode>()
            .Select(static node =>
            {
                var displayName = !string.IsNullOrWhiteSpace(node.Name)
                    ? node.Name
                    : node.Context ?? string.Empty;

                return new GitHubStatusCheck(
                    node.TypeName,
                    displayName,
                    node.Status,
                    node.Conclusion,
                    node.State,
                    node.IsRequired);
            })
            .Where(static check => !string.IsNullOrWhiteSpace(check.DisplayName))
            .ToArray();

        var branchProtectionRule = response.Repository?.Ref?.BranchProtectionRule;
        var requiredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var context in branchProtectionRule?.RequiredStatusCheckContexts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(context))
            {
                requiredNames.Add(context);
            }
        }

        foreach (var requiredStatusCheck in branchProtectionRule?.RequiredStatusChecks ?? [])
        {
            if (!string.IsNullOrWhiteSpace(requiredStatusCheck?.Context))
            {
                requiredNames.Add(requiredStatusCheck.Context);
            }
        }

        return new GitHubPullRequestStatusDetails(
            checks,
            // The HashSet above de-duplicates case-insensitively; the array keeps that result
            // while staying serializable, which IReadOnlySet is not under System.Text.Json.
            requiredNames.ToArray(),
            branchProtectionRule?.RequiresStatusChecks == true);
    }

    private async Task<TData> ExecuteAsync<TData>(
        string query,
        object variables,
        CancellationToken cancellationToken)
        where TData : class
    {
        var request = new GraphQlRequest(query, variables);
        using var message = new HttpRequestMessage(HttpMethod.Post, string.Empty)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetToken());

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "GitHub GraphQL query failed with HTTP {StatusCode}: {Body}",
                (int)response.StatusCode,
                Truncate(body, MaxLoggedErrorBodyLength));
            _healthState.RecordFailure();
            throw new GitHubQueryException($"GitHub GraphQL query failed with HTTP {(int)response.StatusCode}.");
        }

        var graphQlResponse = await response.Content
            .ReadFromJsonAsync<GraphQlResponse<TData>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (graphQlResponse is null)
        {
            _healthState.RecordFailure();
            throw new GitHubQueryException("GitHub GraphQL returned an empty response.");
        }

        if (graphQlResponse.Errors is { Count: > 0 })
        {
            var firstError = graphQlResponse.Errors[0].Message;
            _logger.LogWarning("GitHub GraphQL returned {ErrorCount} errors. First error: {FirstError}", graphQlResponse.Errors.Count, firstError);
            _healthState.RecordFailure();
            throw new GitHubQueryException($"GitHub GraphQL returned an error: {firstError}");
        }

        if (graphQlResponse.Data is null)
        {
            _healthState.RecordFailure();
            throw new GitHubQueryException("GitHub GraphQL response did not include data.");
        }

        _healthState.RecordSuccess();
        return graphQlResponse.Data;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "...(truncated)");

    private string GetToken()
    {
        var token = _options.CurrentValue.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new GitHubQueryException("GitHub token is not configured.");
        }

        return token;
    }

    private void EnsureTokenConfigured() => _ = GetToken();

    private static GitHubPullRequest MapPullRequest(RepositoryNode repository, PullRequestNode pullRequest)
    {
        return new GitHubPullRequest(
            pullRequest.Id,
            repository.NameWithOwner,
            repository.Owner.Login,
            repository.Name,
            pullRequest.Number,
            pullRequest.Title,
            new Uri(pullRequest.Url, UriKind.Absolute),
            pullRequest.CreatedAt,
            pullRequest.IsDraft,
            pullRequest.ReviewDecision,
            pullRequest.MergeStateStatus,
            pullRequest.Mergeable,
            pullRequest.HeadRefName,
            pullRequest.HeadRefOid,
            pullRequest.BaseRefName,
            pullRequest.Author is null
                ? null
                : new GitHubActor(pullRequest.Author.Login, pullRequest.Author.TypeName),
            pullRequest.Labels?.Nodes
                .Where(static label => label is not null && !string.IsNullOrWhiteSpace(label.Name))
                .Select(static label => label!.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static label => label, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? []);
    }

    private sealed record GraphQlRequest(string Query, object Variables);

    private sealed record GraphQlResponse<TData>(
        TData? Data,
        IReadOnlyList<GraphQlError>? Errors);

    private sealed record GraphQlError(string Message);

    private sealed record OpenPullRequestsData(RepositoryOwnerNode? RepositoryOwner);

    private sealed record RepositoryOwnerNode(RepositoryConnection Repositories);

    private sealed record RepositoryConnection(PageInfo PageInfo, IReadOnlyList<RepositoryNode?> Nodes);

    private sealed record PageInfo(bool HasNextPage, string? EndCursor = null);

    private sealed record RepositoryNode(
        string Name,
        string NameWithOwner,
        RepositoryOwnerLogin Owner,
        PullRequestConnection PullRequests);

    private sealed record RepositoryOwnerLogin(string Login);

    private sealed record PullRequestConnection(PageInfo PageInfo, IReadOnlyList<PullRequestNode?> Nodes);

    private sealed record PullRequestNode(
        string Id,
        int Number,
        string Title,
        string Url,
        DateTimeOffset CreatedAt,
        bool IsDraft,
        string? ReviewDecision,
        string? MergeStateStatus,
        string? Mergeable,
        string HeadRefName,
        string HeadRefOid,
        string BaseRefName,
        ActorNode? Author,
        LabelConnection? Labels);

    private sealed record ActorNode(
        string Login,
        [property: JsonPropertyName("__typename")] string TypeName);

    private sealed record LabelConnection(IReadOnlyList<LabelNode?> Nodes);

    private sealed record LabelNode(string Name);

    private sealed record PullRequestStatusData(StatusRepository? Repository);

    private sealed record StatusRepository(
        StatusRef? Ref,
        StatusPullRequest? PullRequest);

    private sealed record StatusRef(BranchProtectionRuleNode? BranchProtectionRule);

    private sealed record BranchProtectionRuleNode(
        bool RequiresStatusChecks,
        IReadOnlyList<string>? RequiredStatusCheckContexts,
        IReadOnlyList<RequiredStatusCheckDescriptionNode?>? RequiredStatusChecks);

    private sealed record RequiredStatusCheckDescriptionNode(string Context);

    private sealed record StatusPullRequest(StatusCheckRollup? StatusCheckRollup);

    private sealed record StatusCheckRollup(StatusCheckContextConnection? Contexts);

    private sealed record StatusCheckContextConnection(IReadOnlyList<StatusCheckNode?> Nodes);

    private sealed record StatusCheckNode(
        [property: JsonPropertyName("__typename")] string TypeName,
        string? Name,
        string? Context,
        string? Status,
        string? Conclusion,
        string? State,
        bool IsRequired);
}
