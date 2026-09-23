---
name: systematic-debugging
description: Use when fixing any bug, crash, test failure, or unexpected behavior — reproduce first, find the root cause, then fix. Also use when a fix attempt has already failed once.
---

# Systematic debugging

Never fix from a guess. Each debugging cycle: reproduce → isolate → root cause → minimal fix → verify the reproduction is gone.

## 1. Reproduce first
- Get an exact reproduction before changing anything: a failing test, a command, a minimal input. If you cannot reproduce it, say so and stop — do not "fix" what you cannot see.
- For a failing test, run it in isolation first: a test that fails only in the full suite is telling you about test interaction (shared state, ordering, timing), not necessarily about the code under test.
- Capture the full error: message, stack trace, exit code. Read the whole trace before hypothesizing.

## 2. Isolate and gather evidence
- Find the smallest input/session that still fails. Bisect: drop halves of the input, or `git stash` / `git bisect` the change history, until the trigger is narrow.
- Check the boring causes first — they are the most common: wrong env, stale build artifacts, missing config, wrong file being edited, cache, version mismatch.
- Instrument before theorizing: add temporary logging, run with `--verbosity detailed`, print intermediate values. Prefer one observed fact over three hypotheses.

## 3. Root cause, not symptom
- Ask "why" until you reach code you could change to make this class of bug impossible, not just this instance.
- When a fix fails, do not retry the same fix harder — the failed fix is evidence that the theory was wrong. Form a new theory that explains both the bug AND the failed fix.
- Watch for two bugs looking like one: confirm each fix against the full symptom list.

## 4. Fix and verify
- Make the minimal fix at the root cause. No drive-by refactors while debugging — they contaminate the diff.
- Turn the reproduction into a regression test (see the xunit-test-writer skill for C#/.NET conventions) and watch it fail against the unfixed code if feasible, then pass.
- Re-run the originally failing scenario end-to-end, then the surrounding test suite for regressions.
- Clean up your debug instrumentation (logs, prints, commented code) before committing.
