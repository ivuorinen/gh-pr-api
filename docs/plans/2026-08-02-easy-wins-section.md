# Plan: "Easy wins" section

Date: 2026-08-02
Status: DRAFT — awaiting approval to implement

## Goal

Add an `Easy wins` section listing every open PR that is ready to merge with CI
passing, oldest first, rendered as the **first** section of all three existing
output formats (JSON, Markdown, HTML).

## Scope & constraints

**Definition of "easy win"** (confirmed with the user):

```text
ci      == "passing"        // NOT "unknown" — an unresolved lookup is not a green light
isDraft == false
branch  != "conflict"
review  != "changes requested"
```

Approval is deliberately **not** required: on this owner's repos Renovate PRs are
never reviewed, and requiring `approved` would make the section permanently empty.
`branch == "behind"` is deliberately **included**: GitHub merges a behind-but-clean
branch fine. `branch == "unknown"` is also included — it only means GitHub had not
computed mergeability yet, and `conflict` is the only state that actually blocks.

**Overlay, not a move** (confirmed with the user): an easy-win PR is *also* still
listed in its normal Security / Human PRs / Robots group. Existing sections keep
their current contents and ordering byte-for-byte.

Constraints:

- `PullRequestReport.TotalCount` keeps counting each PR **once**. It is the number
  of distinct open PRs, not the sum of group sizes.
- No new model type, no new DI service, no formatter changes. The section is a
  `PullRequestGroup` — `MarkdownReportFormatter` and `HtmlReportFormatter` already
  iterate `report.Groups` generically, so both render it with zero edits.
- Empty section is omitted entirely (same as the existing groups), so the JSON
  shape stays clean when nothing is mergeable.

Files touched: `NormalizedValues.cs`, `PullRequestReportBuilder.cs`,
`PullRequestReportBuilderTests.cs`, `MarkdownReportFormatterTests.cs`, `README.md`.

## Tasks

1. **Add the group constants.** — files: `src/GhPrApi/Models/NormalizedValues.cs`
   — add `EasyWinsKey = "easy-wins"` and `EasyWinsTitle = "Easy wins"` to
   `NormalizedValues.Group`.
   — verify: compiles; constants referenced by task 2.

2. **Add the `IsEasyWin` predicate.** — files:
   `src/GhPrApi/Services/PullRequestReportBuilder.cs` — one `private static bool
   IsEasyWin(PullRequestItem)` next to `GetSortRank`, comparing against
   `NormalizedValues.Ci.Passing`, `NormalizedValues.Branch.Conflict` and
   `NormalizedValues.Review.ChangesRequested` with `StringComparison.OrdinalIgnoreCase`,
   plus `!item.IsDraft`.
   — verify: unit tests in task 4 (a `passing` PR qualifies; `unknown`, `pending`,
   `failing`, draft, `conflict`, `changes requested` each disqualify).

3. **Emit the group first.** — files:
   `src/GhPrApi/Services/PullRequestReportBuilder.cs` — in `Build`, immediately
   after the `items.Length == 0` early return and **before** the security group is
   appended, add:

   ```csharp
   var easyWins = items
       .Where(IsEasyWin)
       .OrderBy(static item => item.CreatedAt)
       .ThenBy(static item => item.Id, StringComparer.Ordinal)
       .ToArray();

   if (easyWins.Length > 0)
   {
       groups.Add(new PullRequestGroup(
           NormalizedValues.Group.EasyWinsKey,
           NormalizedValues.Group.EasyWinsTitle,
           PullRequests: easyWins));
   }
   ```

   Note this is the one group that does **not** apply `GetSortRank` — the section's
   whole contract is oldest-first, and by construction no member is `failing`, so
   rank would only shuffle stale ahead of fresh and break the stated ordering.
   — verify: task 4's ordering test.

4. **New builder tests.** — files:
   `tests/GhPrApi.Tests/PullRequestReportBuilderTests.cs` — add:
   - `Easy_wins_is_the_first_group_and_lists_oldest_first` — three qualifying PRs
     created 1d/5d/3d ago; assert `Groups[0].Key == "easy-wins"` and the numbers
     come back in 5d, 3d, 1d order.
   - `Easy_wins_excludes_pull_requests_that_are_not_ready` — one PR each for
     failing CI, `StatusUnresolved` (⇒ `unknown`), pending CI, `isDraft: true`,
     `mergeable: "CONFLICTING"`, `reviewDecision: "CHANGES_REQUESTED"`; assert no
     `easy-wins` group exists at all.
   - `Easy_wins_does_not_remove_the_pull_request_from_its_normal_group` — one
     qualifying robot PR; assert it appears under both `easy-wins` and `robots`,
     and that `TotalCount == 1`.
   — verify: `dotnet test` — all three pass.

5. **Repair the two tests this breaks.** — files:
   `tests/GhPrApi.Tests/PullRequestReportBuilderTests.cs` — both break because
   `TestPullRequests.Create` defaults (`CLEAN`/`MERGEABLE`, one successful required
   check, not draft) qualify as easy wins:
   - `Build_groups_security_before_human_and_robots` (line 100) — its
     `Assert.Collection` over exactly three groups now sees four. Fix by adding an
     `easyWins => Assert.Equal(NormalizedValues.Group.EasyWinsKey, easyWins.Key)`
     as the first element; the remaining three assertions stay, which is exactly the
     regression guard we want on section ordering.
   - `Build_groups_same_dependency_across_repositories` (line 149) — its
     `Assert.Single(report.Groups)` now sees two. Fix by selecting the robots group
     by key (`report.Groups.Single(g => g.Key == NormalizedValues.Group.RobotsKey)`)
     instead of assuming it is the only one.
   — verify: `dotnet test` green with **no** assertion weakened — both keep asserting
   the same thing about the robots/security/human grouping.

6. **Markdown ordering test.** — files:
   `tests/GhPrApi.Tests/MarkdownReportFormatterTests.cs` — the existing
   `Format_outputs_compact_markdown_with_groups_dependency_groups_and_prefixes` is
   `Assert.Contains`-based and still passes, but proves nothing about position. Add
   `Format_puts_easy_wins_first` asserting the rendered markdown **starts with**
   `## Easy wins` and that `IndexOf("## Easy wins") < IndexOf("## Human PRs")`.
   — verify: `dotnet test`.

7. **README.** — files: `README.md` — document in four places:
   - "Normalization rules": a new `Easy wins` subsection stating the four criteria
     verbatim, that `unknown` CI is excluded, and that `behind` is included.
   - "JSON response": note the `easy-wins` group is an **overlay** — its PRs are
     repeated in their normal group, and `totalCount` counts each PR once, so
     summing group sizes over-counts.
   - "Markdown response" + "HTML response": note `## Easy wins` / `<h2>Easy wins</h2>`
     renders first when non-empty.
   — verify: read back against the implemented predicate; every stated criterion
   maps to a line in `IsEasyWin`.

## Adversarial hardening

- **complexity** — examined whether this needs an `EasyWinDetector` service (like
  `RobotDetector`/`SecurityDetector`) and a new `EasyWins` field on
  `PullRequestReport`. **Cut both.** The other detectors exist to normalize raw
  upstream GitHub values; this is a pure predicate over four already-normalized
  strings, so it is a `private static` in the builder — no new file, no DI
  registration in `Program.cs`. Reusing `PullRequestGroup` rather than adding a
  top-level field is what makes both formatters zero-change; a dedicated field
  would have forced edits to `MarkdownReportFormatter.Format` and
  `HtmlReportFormatter.Format` for no gain. Net production diff: ~15 lines across
  2 files.
- **review** — edge cases forced onto task 2/3: (a) `ci == "unknown"` from a failed
  status lookup must **not** qualify — the predicate tests equality against
  `Passing`, never inequality against `Failing`, so `unknown` and `pending` fall out
  by construction; (b) empty easy-win set must omit the group, not emit an empty
  one (task 3's `if`); (c) `TotalCount` must not double-count — task 3 adds to
  `groups` only, never to `items`; (d) tie on `CreatedAt` — added the
  `.ThenBy(Id, Ordinal)` in task 3 so two PRs created in the same second cannot
  produce a nondeterministic order and a flaky test.
- **errors** — examined the degraded path in `PullRequestReportService`: a PR whose
  status fetch throws is marked `StatusUnresolved`, and `BuildItem` maps that to
  `Ci.Unknown`. Confirmed the predicate treats that as *not* an easy win, so a
  GitHub status outage cannot invent a "ready to merge" recommendation out of
  missing data. Task 4's exclusion test covers `StatusUnresolved` explicitly for
  this reason. No new resources acquired, so `leaks` does not apply.
- **security** — no new trust boundary. The section is derived entirely from fields
  the builder already computes; no new input reaches a sink, no new GitHub call, no
  new query parameter. The HTML path still goes through `HtmlReportFormatter`'s
  existing `WebUtility.HtmlEncode` on every cell — unchanged, because the new group
  reuses `AppendTable`.
- **contract** — `PullRequestReport`'s JSON shape is unchanged; only a new value of
  the existing `groups[].key` string appears. Additive ⇒ **minor** version bump, and
  `Produces<PullRequestReport>()` in `Program.cs` needs no edit, so the OpenAPI
  document stays correct. The one real consumer-visible change is that iterating
  `groups` and counting PRs now double-counts; that is why task 7 documents it
  against `totalCount` explicitly rather than leaving it to be discovered.
- **arch** — grouping decisions live in `PullRequestReportBuilder` and nowhere else;
  formatters stay dumb renderers over `report.Groups`. This change respects that
  boundary — the fact that no formatter file is touched *is* the check that it does.
- **perf** — one extra `Where`/`OrderBy` over the already-materialized `items`
  array, in memory, O(n log n) on a list capped by `RepositoryLimit` ×
  `PullRequestLimitPerRepository`. No I/O, no GitHub call, no cache key. The report
  is deliberately not cached (see `PullRequestReportService`), so nothing new needs
  invalidating.
- **tests** — every task's verification is a behavioral assertion, not a tautology:
  ordering is asserted by comparing string positions in real rendered markdown, and
  exclusion is asserted by the *absence* of a group given genuinely disqualified
  inputs. Task 5 repairs the two breaking tests by making their assertions more
  specific (select-by-key), never by deleting or loosening them.
- **docs** — task 7 is a task, not a footnote, because README currently documents
  the full group list and normalization rules; leaving it stale would make the
  documented contract wrong on merge.
- **migrations / concurrency / config / privacy / i18n / observability** — not
  applicable: no schema or data migration, no new shared mutable state or ordering
  assumption (the builder is a stateless singleton operating on a local array), no
  new configuration key, no personal data beyond the PR author login already
  returned, no localization scope declared in this repo, and no new log, metric or
  alert (the section is derived, so it has no failure of its own to report).
- **a11y** — the HTML section reuses `AppendTable`, which already emits
  `<th scope="col">` headers inside a `<div class="table-wrap">`; no new markup
  pattern is introduced, so the existing table semantics carry over unchanged.

## Rollback / abort

Single-commit, pure-addition change with no persisted state: revert the commit and
the section disappears. The SQLite cache at `GitHub:CachePath` stores the *listing*
and *per-PR status*, never the assembled report, so no cache invalidation or key
version bump is needed on either deploy or rollback — an old and a new instance can
serve from the same cache file simultaneously.

## Open questions & accepted risks

- **Accepted:** `groups` now double-counts PRs for any consumer that sums group
  sizes. Accepted because the user chose the overlay behavior explicitly, it keeps
  every existing section unchanged, and `totalCount` remains the authoritative
  count. Documented in README (task 7).
- **Accepted:** `branch == "unknown"` qualifies. `BranchNormalizer` returns
  `unknown` when GitHub has not yet computed mergeability, which is a timing
  artifact rather than a merge blocker. Worst case is one PR listed that turns out
  to conflict; the alternative silently hides genuinely mergeable PRs on every cold
  fetch, which is worse for a section whose whole point is "what can I merge now".
- **Accepted:** an unapproved human PR with green CI appears as an easy win. Correct
  for this single-owner repo, and the item still carries `review: awaiting review`
  in every format, so the caller can see it.
- **Assumption:** "oldest first" means by `CreatedAt`, matching the existing `Age` /
  `ageDays` fields and the `STALE` prefix. Not by last-updated — GitHub's
  `updatedAt` is not currently fetched by `GitHubGraphQlClient`, and adding it would
  mean a query change plus a cache key bump for no stated benefit.
