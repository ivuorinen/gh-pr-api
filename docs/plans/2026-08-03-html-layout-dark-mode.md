# Plan: HTML report — column reorder, fixed column widths, dark mode

Date: 2026-08-03
Status: DRAFT — awaiting approval to implement

## Goal

Make the HTML report's tables readable: move `Flags` from the first column to the
second (after `PR`), give every column except `PR` a fixed width so the several
tables on one page line up with each other, and follow the browser's
light/dark preference.

## Scope & constraints

**Touches**

- `src/GhPrApi/Services/HtmlReportFormatter.cs` — the only producer of this markup.
- `tests/GhPrApi.Tests/HtmlReportFormatterTests.cs` — new assertions.
- `README.md:214` — documents the column order, so it changes with the code.

**Must not change**

- JSON and Markdown output. `PullRequestItem` and the report model are untouched;
  this is presentation only.
- The encoding boundary. Every cell value keeps flowing through `Encode`.
- The accessibility work from the previous PR: `tabindex="0"`, `role="region"`,
  `aria-label` per scroll region, and a visible focus indicator.

**Root cause of "tables are not aligned"**

Each `<table>` currently uses the CSS *auto* table layout, which sizes columns from
that table's own content. The Easy wins table and the `eslint` dependency table
therefore get different column positions on the same page. Fixed widths alone do not
fix this — under auto layout `width` is a suggestion the algorithm may override.
`table-layout: fixed` is what makes the widths binding and every table identical.

## Tasks

1. **Reorder the columns.** Swap the header entries and the two matching `<td>`
   emissions so the order is `PR, Flags, Author, Age, Review, CI, Branch`.
   — files: `HtmlReportFormatter.cs`
   — verify: new test asserts the full `<th>` sequence *and* that within one `<tr>`
     the PR cell precedes the `prefixes` cell. No test asserts column order today,
     so this reorder would otherwise pass silently.

2. **Switch to a fixed layout and set widths.** Add `table-layout: fixed` and
   per-column widths via `th:nth-child(n)` in the shared stylesheet. `PR` gets no
   width and absorbs the remainder.
   — files: `HtmlReportFormatter.cs` (style block)
   — proposed: Flags `7.5rem`, Author `10rem`, Age `4rem`, Review `9rem`,
     CI `5.5rem`, Branch `7.5rem`; `PR` auto.
   — verify: test asserts `table-layout: fixed` and the nth-child width rules are
     emitted. Rendered alignment cannot be unit-tested — checked by eye once.

3. **Let the Flags cell wrap.** Drop `white-space: nowrap` from `.prefixes`.
   — files: `HtmlReportFormatter.cs` (style block)
   — why: a PR can carry all four flags — `SECURITY FAILING STALE DRAFT`, 28
     characters. Inside a fixed `7.5rem` column, `nowrap` overflows the cell and
     reintroduces the misalignment this change exists to remove. `vertical-align:
     top` is already set, so a wrapped cell still reads correctly.
   — verify: covered by the task 2 test asserting the rule is gone.

4. **Add dark mode.** Move the palette into CSS custom properties on `:root`,
   override them under `@media (prefers-color-scheme: dark)`, and declare
   `color-scheme: light dark`.
   — files: `HtmlReportFormatter.cs` (style block)
   — `color-scheme` matters specifically here: the `.table-wrap` overflow region is
     focusable and scrollable, and without it the browser paints a bright scrollbar
     against the dark table.
   — verify: test asserts the media query, `color-scheme`, and the dark link colour
     are emitted. Contrast is verified by the computation below, not by a test.

5. **Update the README.** Correct the column order at `README.md:214` and add one
   sentence stating the page follows `prefers-color-scheme`.
   — files: `README.md`
   — verify: `rg 'Flags, PR'` returns nothing; the documented order matches
     `TableHeaders`.

Order matters: task 1 before 2 (widths are keyed to column position), and 3 before
any visual check of 2.

## Palette (measured, not estimated)

Ratios computed with the WCAG relative-luminance formula. Text needs 4.5:1 (SC
1.4.3); the focus outline is a non-text UI component needing 3:1 (SC 1.4.11).

| Role | Light on `#ffffff` | Dark on `#12141a` |
| --- | --- | --- |
| Body text | `#1a1a1a` — 17.40:1 | `#e6e6e6` — 14.75:1 |
| Muted (h3, th, meta) | `#555555` — 7.46:1 | `#a8b0bd` — 8.42:1 |
| `.prefixes` | `#b3261e` — 6.54:1 | `#ff8a80` — 8.06:1 |
| Link | UA default — 9.40:1 | `#8ab4f8` — 8.74:1 |
| Focus outline | `#1a73e8` — 4.51:1 | `#8ab4f8` — 8.74:1 |

Row borders are decorative separators and carry no contrast requirement.

## Adversarial hardening

- **a11y** — the lens that changed this plan most. The page sets no link colour
  today; it inherits the user agent's. A naive dark mode that flips only background
  and text would leave links at **1.96:1** and visited links at **1.67:1** against
  `#12141a` — both far below the 4.5:1 floor, and a regression of the accessibility
  work merged in the previous PR. Task 4 therefore sets `a` and `a:visited`
  explicitly, and recolours the focus outline, all re-measured above. Also added
  `color-scheme` so the focusable scroll region's scrollbar follows the theme.
- **complexity** — cut a manual light/dark toggle: it needs JS, a persisted
  preference and a control, none of it asked for, and `prefers-color-scheme` alone
  is the request. Cut `<colgroup>` markup in favour of one `nth-child` rule, so
  per-table bytes stay flat. Kept CSS custom properties: nine values across eight
  selectors, and duplicating selectors inside the media query is exactly where a
  missed value hides.
- **review** — edge cases examined on the Flags cell: four flags at once (28 chars,
  resolved by task 3) and the common empty cell, which now renders as an empty
  fixed-width cell, which is the alignment goal rather than a defect. Long PR titles
  are unaffected: `PR` stays the flexible column.
- **contract** — the HTML column order is documented at `README.md:214`. This is a
  human-facing page, not a versioned API, so no semver implication; but the audit
  just closed a batch of doc drift, so task 5 moves the docs in the same change.
- **tests** — the suite has no column-order assertion at all today, so this reorder
  would pass green without task 1's test. Recorded limitation: neither rendered
  alignment nor computed contrast is unit-testable here; the tests assert that the
  rules are *emitted*, and correctness of the values rests on the measurements above.
- **security** — no trust boundary moves. Every cell value still passes through
  `Encode`; the added CSS is static with no interpolated values; `aria-label` is
  already encoded. No new input reaches a sink.
- **arch** — `HtmlReportFormatter` stays a pure, dependency-free string builder;
  no layer boundary is crossed.
- **perf** — adds roughly 1 KB of CSS emitted once per response. Per-row work is
  unchanged; no new per-PR allocation.
- **errors / leaks** — N/A: no new operation acquires a resource or can fail; the
  method builds a string and returns it.
- **concurrency** — N/A: no shared state added; the formatter is a stateless
  singleton and the style block is a constant.
- **migrations** — N/A: no schema or persisted data.
- **config** — N/A: no new option; the theme follows the browser, not configuration.
- **privacy** — N/A: the change adds no data to the page; the same public PR
  metadata is rendered.
- **i18n** — N/A: single-locale by declared scope, and no new user-facing strings.
- **observability** — N/A: no runtime signal surface in a formatter.

## Rollback / abort

One commit touching one source file and the README. `git revert` restores the prior
markup; the endpoint is stateless, nothing is persisted, no cached value embeds the
HTML (the report is assembled per request and deliberately not cached). No deploy
ordering concerns.

## Open questions & accepted risks

- **Width values are a first pass**, tuned for a 16px root and `system-ui`. If
  `changes requested` or `up to date` wraps awkwardly they need a nudge — a
  one-line change, so not worth blocking the plan on.
- **Four-flag PRs will wrap to two lines** rather than widening the column.
  Accepted: overflow would break the alignment this change exists to deliver.
- **The dark palette is a neutral slate**, matched to no brand, because the project
  has none.
- **`prefers-color-scheme` only** — no manual override and no persistence. This is
  exactly what was asked for; a toggle can be added later without redoing this work.
- **Alignment is verified by eye, once.** No automated check can assert that two
  tables' columns line up in a rendered browser.
