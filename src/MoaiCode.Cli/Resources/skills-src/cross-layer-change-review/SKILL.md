---
name: cross-layer-change-review
description: Review commits, pull requests, or commit ranges for correctness, regressions, security, and incomplete cross-layer wiring. Use when reviewing feature work, bug fixes, refactors, UI/API changes, authentication, configuration, database, LLM/tool, or multi-platform changes.
license: zaiCode original — authored for the zaiCode workspace; no external content
---

# Cross-layer change review

Review the change against the repository's actual code and tests — do not implement anything.

## Workflow

1. Read the repo's `AGENTS.md` / `CLAUDE.md`, contribution scripts, and test setup before judging conventions.
2. Get the diff: `git show <commit>` / `git diff <base>..<head>`, or the files the user named.
3. For each changed symbol, search its callers, consumers, related APIs, services, and schema files.
4. Identify duplicate paths the change might have missed (new vs existing flows, platform variants, payload formats).
5. Check that existing behavior, settings, permissions, and data contracts are preserved.
6. Run the relevant tests, lint, and typecheck. State explicitly what you could not run or verify.

## Required checks

### Cross-layer completeness
Verify the full chain stays wired end to end:
`UI/state → props/types → request payload → API route → service/handler → DB/LLM → persistence/display`
A new option or flag must be plumbed through every calling path and wire format.

### Duplicate paths
Same behavior copied across components, platforms, or endpoints — was only one copy updated?
Repeated follow-up fixes in one area signal a missing shared helper or component; recommend extraction.

### Data boundaries
Internal markers, thinking blocks, citation metadata, and special formats must not leak into stored titles, message bodies, copy, or export paths.

### Security and configuration
Auth/permission/MFA/lockout/API-key/PII changes must be consistent across every auth path.
Existing settings values, per-user permissions, org scoping, and custom patterns must still be honored at the execution path. Failure counters and key rotation must be safe under concurrent requests.

### Async and error handling
Loading, double-click, cancellation, timeout, network failure, permission errors, partial success. Errors swallowed into empty results need a log line and user feedback.

### LLM and tool calls
Follow-up LLM requests must keep tool name, tool ID, tool result, and tool definitions consistent. Review multi-tool and multi-round behavior.

### UI, i18n, accessibility
Responsive layout, hover/focus-visible, `aria-*`, button types, loading states, and translation keys across all supported locales.

### Tests and regression coverage
Cover not just the happy path: false positives, boundary values, empty input, permission denial, failure responses, and new-vs-existing paths. Repeated follow-up fixes mark mandatory regression-test candidates.

## Findings format

Order findings by priority. Each one:

- `[P0/P1/P2/P3] title`
- file and exact line
- reproduction condition and actual impact
- suggested fix direction

P0 blocks immediately (critical defect); P1 is a major functional or security flaw; P2 a normal regression; P3 a low-impact improvement.

Do not report speculation you did not verify. When there are no findings, say "no findings" explicitly and summarize what you verified and what risk remains.

## Commit-range review

When reviewing multiple commits, aggregate patterns in addition to individual bugs:

- follow-up fixes to the same feature
- repeatedly missed layers
- duplicate implementations
- change types with no tests
- shared-helper or architecture improvement candidates
