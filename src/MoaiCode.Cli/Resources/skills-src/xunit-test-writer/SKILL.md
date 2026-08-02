---
name: xunit-test-writer
description: Use when asked to write, add, or improve unit tests for C#/.NET code with xUnit — for changed code, a new function, or to reproduce a bug before fixing it.
---

# Writing xUnit tests (C#/.NET)

Write focused, reliable xUnit tests that match the existing test project's conventions.

## Approach
1. **Find the pattern first.** Read a nearby test file in the relevant test project (e.g. `tests/*.Tests`) and match its style, namespaces, and helpers before writing.
2. **Test the behavior, not the framework.** Cover the happy path plus edge cases: null/empty inputs, boundaries (0, off-by-one, min/max), error paths, and encoding/locale where relevant.
3. **One behavior per test.** Use `[Fact]` for single cases and `[Theory]` + `[InlineData(...)]` for parameterized cases.
4. **Descriptive names:** `Method_condition_expectedResult` (e.g. `SafeDirName_blocks_path_escape`).
5. **Arrange–Act–Assert**, with clear `Assert.Equal / True / Null / Throws / Single / Contains`.

## Reliability
- **No network or shared global state** in unit tests. For filesystem tests, use a unique temp path and clean up in `finally`.
- If the target is an `internal`/`private` helper, follow the repo's convention — some helpers are made `public` specifically so they can be unit-tested (mirror that rather than adding test hacks).
- Don't over-mock; prefer testing real pure functions.

## Verify
Run the specific project and make sure it is green before reporting:
```
~/.dotnet/dotnet test tests/<Project>.Tests/<Project>.Tests.csproj -p:UseSharedCompilation=false
```
If the build hangs, add `-p:UseSharedCompilation=false` (VBCSCompiler deadlock workaround). Report the pass/fail counts honestly.
