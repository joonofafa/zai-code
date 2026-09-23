---
name: source-driven-development
description: Use when working with a library, framework, or API you are not fully sure about — verify usage against official documentation or the installed source before writing code, instead of relying on memory.
---

# Source-driven development

Your training data contains outdated and hallucinated APIs. Before writing code against any API you cannot fully specify from memory, verify it against a primary source.

## 1. When to verify
- New/unfamiliar library, or a familiar library's major-version boundary where APIs get renamed or signatures change.
- Any pattern where you are composing the call from memory rather than recognition: the exact parameter names/order, units, return shape, null semantics, or error behavior.
- Build/CI configuration (MSBuild properties, csproj syntax, Dockerfile, GitHub Actions keys) — these churn constantly.
- You do NOT need to verify: the language standard library you use daily, or code already in this repo that you just read.

## 2. Prefer sources in this order
1. **Installed source** — the package's XML docs and source in the local NuGet cache/obj, or the vendored copy in this repo. This reflects the exact version in use, which docs pages may not. Check the project's `Directory.Packages.props` for the pinned version.
2. **Official docs** — the vendor's documentation site for the version in use. Use the WebSearch tool to find it, then read the specific page.
3. **The repo's own usage** — `Grep` for existing call sites in this workspace; working code beats remembered code, and it also tells you the project's conventions.

## 3. Workflow
- Verify the exact signature you intend to call — write the call only after the source confirms it.
- Note the version you verified against; if it differs from the project's pinned version, check the release notes/changelog for breaking changes.
- When docs and installed source disagree, the installed source wins — it is what will actually compile and run.
- If verification fails (no docs access, source unavailable), say what you could not verify and prefer an alternative you CAN verify, or ask the user.

## 4. After writing
- Compile early: `dotnet build` catches hallucinated signatures faster than review. A CS name-not-found on an API you "know" exists means you hallucinated it — go verify, don't cast around.
- Attribute non-obvious usage in a one-line comment with the doc/source you followed (what + version), so the next reader can re-verify.
