# gh-pr-api

Small JSON/Markdown API for listing currently open pull requests across public, non-archived GitHub repositories owned by a configured GitHub account.

The service replaces an AI prompt that shells out to `gh`. It queries GitHub GraphQL directly, normalizes review, CI, branch, security and robot/dependency metadata, and returns a stable API response.

## Requirements

- .NET SDK 10.0+ for local development
- Docker for containerized execution
- GitHub token with enough access to read the target public repositories and pull requests

GitHub GraphQL requires authentication. For public-only repositories, use a classic token with minimal read access or a fine-grained token that can read public repository metadata.

## Configuration

Use environment variables in production:

```bash
export GitHub__Owner=ivuorinen
export GitHub__Token=github_pat_or_ghp_token
export GitHub__CacheTtlSeconds=300
```

Configuration keys:

| Key | Default | Description |
|---|---:|---|
| `GitHub:Owner` | `ivuorinen` | GitHub user or organization login whose public repositories are scanned. |
| `GitHub:Token` | empty | GitHub API token. Required. |
| `GitHub:CacheTtlSeconds` | `300` | TTL for the repository and pull-request listing. Per-PR check status has its own adaptive TTL. |
| `GitHub:CachePath` | `cache.db` | SQLite file backing the durable cache tier. The container image sets this to `/data/cache.db`. |
| `GitHub:StatusCacheTtlSeconds` | `30` | TTL for a pull request whose checks are still running. A failing verdict uses twice this; an all-green verdict is cached for 6 hours. |
| `GitHub:RepositoryLimit` | `1000` | Maximum number of repositories to inspect. |
| `GitHub:PullRequestLimitPerRepository` | `100` | Maximum open PRs read per repository. Mirrors the original prompt. |
| `GitHub:StatusCheckLimitPerPullRequest` | `100` | Maximum status/check contexts read per pull request. |

The API does not require a separate inbound API key. The static token is the upstream GitHub token and is read from configuration/environment only. It is never included in responses.

## Run locally

```bash
dotnet run --project src/GhPrApi/GhPrApi.csproj
```

Then call JSON output:

```bash
curl http://localhost:5000/api/github/open-pull-requests
```

Or Markdown output:

```bash
curl "http://localhost:5000/api/github/open-pull-requests?format=markdown"
```

With explicit owner and cache refresh:

```bash
curl "http://localhost:5000/api/github/open-pull-requests?owner=ivuorinen&refresh=true"
```

## Endpoints

### `GET /api/github/open-pull-requests`

Query parameters:

| Name | Required | Description |
|---|---:|---|
| `owner` | no | Must match the configured `GitHub:Owner` (case-insensitive). Any other value returns `400 Unsupported owner.` The endpoint is unauthenticated and spends the operator's GitHub token, so it will not scan an arbitrary account on request. |
| `refresh` | no | If `true`, bypasses the current in-memory cache. |
| `format` | no | `json`, `markdown`, `md`, or `html`. Defaults to `json`. |

Two format-fixed variants of the same endpoint are also available, ignoring `?format=`:

- `GET /api/github/open-pull-requests.json` — always JSON.
- `GET /api/github/open-pull-requests.html` — always renders the listing as an HTML page.

All three accept the same `owner`/`refresh` query parameters.

The endpoint is rate-limited to 10 requests per minute (fixed window). The limit is **process-global and shared by all callers**, not per client — it exists to cap total load on the single upstream GitHub token, so one busy caller can exhaust the window for everyone. A caller over the limit gets `429 Too Many Requests`. This mainly protects `refresh=true`, which bypasses the cache and re-queries GitHub in full.

Calls to the GitHub GraphQL API automatically retry transient failures (5xx, timeouts) with exponential backoff, up to a 70s total budget per call; a sustained GitHub outage still surfaces as `503 Unable to query GitHub.` once that budget is exhausted.

### JSON response

```json
{
  "owner": "ivuorinen",
  "generatedAt": "2026-07-06T15:00:00+00:00",
  "totalCount": 1,
  "truncated": false,
  "groups": [
    {
      "key": "robots",
      "title": "Robots",
      "dependencyGroups": [
        {
          "dependencyName": "dotnet-sdk",
          "pullRequests": [
            {
              "id": "ivuorinen/example#12",
              "repo": "ivuorinen/example",
              "number": 12,
              "title": "chore(deps): update dotnet-sdk to v10",
              "author": "renovate[bot]",
              "authorType": "Bot",
              "createdAt": "2026-07-01T10:00:00+00:00",
              "age": "5d",
              "ageDays": 5,
              "isDraft": false,
              "isSecurity": false,
              "isRobot": true,
              "dependencyName": "dotnet-sdk",
              "dependencyConfidence": "high",
              "review": "awaiting review",
              "ci": "passing",
              "branch": "up to date",
              "headRefName": "renovate/dotnet-sdk-10.x",
              "labels": ["dependencies"],
              "prefixes": ["STALE"],
              "url": "https://github.com/ivuorinen/example/pull/12"
            }
          ]
        }
      ]
    }
  ]
}
```

`truncated` is `true` if either `GitHub:RepositoryLimit` or `GitHub:PullRequestLimitPerRepository` cut off results before the true end of the data (i.e. the response may not list every currently open PR).

If no open PRs exist, the JSON response is HTTP 200 with `totalCount: 0`, an empty `groups` array, and `message: "No open PRs."`.

If GitHub cannot be queried at all, JSON output returns HTTP 503 Problem Details with title `Unable to query GitHub.`.

### Partial results

If the listing succeeds but the check status of some pull requests cannot be fetched, the response is still HTTP 200 and carries `"degraded": true` plus `"unresolved": ["ivuorinen/example#1"]`. Those pull requests report `"ci": "unknown"`, which is distinct from `pending` — it means the lookup failed, not that checks are running. `degraded` and `truncated` are independent and may both be true.

### Caching

Results are cached in two tiers rather than as one report:

| Unit | Key | TTL |
|---|---|---|
| Repository and PR listing | per owner | `GitHub:CacheTtlSeconds` (300s) |
| Per-PR check status | per PR **and head commit SHA** | adaptive, below |

Because the status key includes the head commit SHA, a push invalidates it by itself. The TTL then depends on what the checks say: still running uses `GitHub:StatusCacheTtlSeconds` (30s), a failing verdict uses twice that (a re-run keeps the same SHA, and people only re-run red), and an all-green verdict is cached for 6 hours.

The consequence is that a refresh only re-queries what can actually have changed, and a failure part-way through keeps everything that already succeeded — a retry fetches only the missing pieces instead of starting over.

The second tier is a SQLite file at `GitHub:CachePath`, so cached work survives a restart or redeploy. If that path is not writable the service logs a warning once and runs with the in-memory tier only: a missing volume costs a cold start, never availability.

### Markdown response

```markdown
## Robots
### dotnet-sdk
- [STALE] ivuorinen/example#12: chore(deps): update dotnet-sdk to v10 — renovate[bot] — open 5d — review: awaiting review — ci: passing — branch: up to date — https://github.com/ivuorinen/example/pull/12
```

If no open PRs exist, Markdown output is exactly:

```text
No open PRs.
```

If GitHub cannot be queried, Markdown output is exactly:

```text
Unable to query GitHub.
```

### HTML response

`format=html` (or `GET /api/github/open-pull-requests.html`) renders the same grouped listing as a minimal, self-contained HTML page: an `<h1>` with the owner, an `<h2>`/`<h3>` per group and dependency group, and a `<table>` per group (Flags, PR, Author, Age, Review, CI, Branch) with one `<tr>` per PR, its title linking to the GitHub URL. All GitHub-sourced text (titles, authors) is HTML-encoded.

If GitHub cannot be queried, HTML output is an HTML page with the same `Unable to query GitHub.` message, at HTTP 503.

### `GET /health/live`

Returns process liveness.

### `GET /health/ready`

Returns readiness. This validates that a GitHub token is configured; it does not proactively call GitHub.

GitHub reachability is **reported, not gated on**: the response body carries `gitHubReachable` reflecting whether the last GitHub API call (if any) succeeded, but an unreachable GitHub still returns `200 ready`. Gating on it deadlocks — only a served request can clear the flag, so an orchestrator that pulls the container out of rotation on a 503 removes the only thing that could restore it. A GitHub outage surfaces to callers as `503 Unable to query GitHub.` on the report endpoints, which is the correct layer for it.

```json
{ "status": "ready", "gitHubReachable": true }
```

### `GET /openapi/v1.json`

Generated OpenAPI document.

## Normalization rules

Review:

- `APPROVED` -> `approved`
- `CHANGES_REQUESTED` -> `changes requested`
- anything else -> `awaiting review`

CI:

- any failed/error/cancelled/timed-out required check -> `failing`
- any queued/in-progress/waiting/expected/missing required check -> `pending`
- all required checks successful -> `passing`
- unclear -> `pending`

Branch:

- merge conflict / not cleanly mergeable -> `conflict`
- mergeable but behind base -> `behind`
- mergeable and current -> `up to date`
- unclear -> `unknown`

Security detection checks title, labels, branch name, author metadata and dependency metadata for vulnerability/security/CVE/GHSA/advisory-related indicators.

## Test

```bash
dotnet test
```

## Docker

Build locally:

```bash
docker build -t gh-pr-api .
```

Run locally:

```bash
docker run --rm -p 8080:8080 \
  -v gh-pr-api-cache:/data \
  -e GitHub__Owner=ivuorinen \
  -e GitHub__Token="$GitHub__Token" \
  gh-pr-api
```

The `-v` is optional. Without it the durable cache tier lives inside the container and is lost when it exits; the service works either way.

Call:

```bash
curl http://localhost:8080/api/github/open-pull-requests
curl "http://localhost:8080/api/github/open-pull-requests?format=markdown"
```

## Docker Compose

Create `.env` from `.env.example`:

```bash
cp .env.example .env
```

Set `GITHUB_TOKEN` in `.env`, then run:

```bash
docker compose up --build
```

`compose.yml` deliberately does **not** publish a host port — it declares `expose: 8080` so a
reverse proxy (Coolify's, in the deployment above) reaches the container on the service network
and terminates TLS there. Publishing `8080:8080` collides with anything else already bound to
the host's port 8080, including the previous revision during a redeploy, and serves the API in
cleartext outside the proxy.

For host access during local development, use the `docker run -p 8080:8080` flow above, or add
an untracked `compose.override.yml`:

```yaml
services:
  gh-pr-api:
    ports:
      - "8080:8080"
```

Every setting in `compose.yml` is overridable from `.env` — see `.env.example` for the full list
(`GITHUB_OWNER`, `GITHUB_CACHE_TTL_SECONDS`, `IMAGE_TAG`, …). Defaults match the in-code defaults,
so an empty `.env` beyond `GITHUB_TOKEN` behaves exactly as documented.

## GitHub Container Registry

The repository contains `.github/workflows/container.yml`.

It publishes a multi-architecture image to:

```text
ghcr.io/<owner>/<repository>
```

For example, if the repository is `ivuorinen/gh-pr-api`, the image is:

```text
ghcr.io/ivuorinen/gh-pr-api:latest
```

Published tags:

- `latest` on the default branch
- branch name tags
- git tag tags such as `v1.0.0`
- short SHA tags such as `sha-abc1234`

Required repository setting:

- GitHub Actions must have permission to write packages.
- The workflow already requests `packages: write`.

## Coolify deployment

Recommended Coolify setup:

| Setting | Value |
|---|---|
| Source | Git repository with Dockerfile, or GHCR image |
| Image | `ghcr.io/ivuorinen/gh-pr-api:latest` |
| Port | `8080` |
| Health path | `/health/ready` |
| Domain path | `/` |

Environment variables:

```text
ASPNETCORE_URLS=http://+:8080
GitHub__Owner=ivuorinen
GitHub__Token=<github token>
GitHub__CacheTtlSeconds=300
GitHub__RepositoryLimit=1000
GitHub__PullRequestLimitPerRepository=100
GitHub__StatusCheckLimitPerPullRequest=100
GitHub__CachePath=/data/cache.db
GitHub__StatusCacheTtlSeconds=30
```

Add a Coolify persistent volume mapped to `/data` so the cache survives redeploys. Without it the service still runs, logs one warning, and pays a full cold fetch after every deploy.

If the GHCR package is private, configure Coolify with registry credentials. If the package is public, no registry credentials are required.
