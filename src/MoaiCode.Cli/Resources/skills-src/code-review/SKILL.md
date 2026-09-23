---
name: code-review
description: Use when asked to review code changes — a diff, a pull request, a commit, or code the agent just wrote. Covers correctness, security, performance, compatibility, and test coverage.
license: zaiCode original — principles referenced from getsentry/skills code-review (Apache-2.0); no verbatim content
---

# Code review

Review the change itself, not the author. Ground every finding in the code you actually read — open each changed file before commenting on it.

## 1. Orient first
- Get the change set: `git diff`, `git show <commit>`, or the files the user named. For work-in-progress, `git status` + `git diff` together.
- Read the surrounding code, not just the diff hunks — a one-line change can be wrong because of what surrounds it.
- State what the change is *trying* to do before judging it. If the intent is unclear, ask.

## 2. Review dimensions (in order)
1. **Correctness** — does it do what it claims? Check edge cases: null/empty, zero/negative, off-by-one, error paths, concurrency, resource disposal. Trace data flow across function boundaries.
2. **Security** — injected input reaching dangerous sinks (SQL, shell, paths, deserialization), secrets/tokens committed or logged, missing authorization on new endpoints, OWASP classics.
3. **Compatibility** — public API/signature changes and their callers, config/schema format changes and existing stored data, platform assumptions (path separators, encodings, case sensitivity).
4. **Performance** — only when it matters: N+1 queries, repeated work in loops, unbounded collections, sync I/O on hot paths. Don't demand micro-optimizations.
5. **Tests** — does the change have test coverage? Do existing tests still pass (`dotnet test`)? Is there a test that would have caught this bug?

## 3. Style and scope
- Match the project's existing conventions — do not flag style the codebase already uses.
- Distinguish blocking issues from nits. Use a severity label per finding (blocker / should-fix / nit).
- Stay inside the change: adjacent pre-existing problems are worth one summary mention, not a findings list.

## 4. Report format
- One-line verdict first (safe to merge / needs changes), then findings ordered by severity.
- Each finding: `path:line` reference, what is wrong, why it matters, and the smallest suggested fix.
- Verify claims before making them: run the tests, reproduce the crash, compile the branch. If you couldn't verify something, say so explicitly.
