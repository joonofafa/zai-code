# Repository Guidelines

## Project Structure & Module Organization

This is a C#/.NET 10 solution for the `zaiCode` coding agent. Runtime code is under `src/`: `MoaiCode.Core` contains the agent loop and contracts; `Providers`, `Tools`, and `Tools.Bash` provide models and tools; `Mcp`, `Config`, `Persistence`, `Localization`, and `Tui` provide supporting services and UI; `Cli` is the executable entry point; and `Sdk` contains public client types. Tests are in `tests/` (`Core.Tests`, `OpenXml.Tests`, and Windows-only `Office.Tests`). Use `demo/` for the sample program and `docs/` for design and format notes. Build output in `bin/`, `obj/`, `dist/`, and `publish/` is generated and ignored.

## Build, Test, and Development Commands

The pinned SDK is .NET 10.0.100 (`global.json`). If `dotnet` is not on `PATH`, use `~/.dotnet/dotnet`.

```bash
dotnet build MoaiCode.sln
dotnet test tests/MoaiCode.Core.Tests
dotnet test tests/MoaiCode.OpenXml.Tests
dotnet run --project src/MoaiCode.Cli
dotnet run --project src/MoaiCode.Cli -- run "prompt"
./publish-linux.sh                 # dist/linux-x64/zaiCode
./publish-win.sh win-x64            # dist/win-x64/zaiCode.exe
```

Run `dotnet test MoaiCode.sln` for the full suite when on Windows; `Office.Tests` targets `net10.0-windows` and requires Windows/Office-specific validation where applicable. Use `dotnet test ... --filter "FullyQualifiedName~TestName"` for focused runs.

## Coding Style & Naming Conventions

Follow standard C# style: four-space indentation, nullable reference types enabled, implicit usings enabled, PascalCase for types and public members, camelCase for locals/parameters, and `I`-prefixed interfaces. Keep package versions centralized in `Directory.Packages.props`. Reuse existing helpers and service boundaries before adding parallel implementations. User-facing source strings are English; add Korean translations in `src/MoaiCode.Localization/Resources/ko.json` while keeping keys synchronized with `en.json`. Log messages must be ASCII English and must not include raw user input.

## Testing Guidelines

Tests use xUnit `[Fact]` and `[Theory]`. Place tests in the project matching the behavior and use descriptive `MethodName_condition_expectedResult`-style names. Add regression coverage for security, tool dispatch, persistence, localization, and document changes. No repository-wide coverage threshold is configured.

## Commit & Pull Request Guidelines

Use concise, imperative Conventional Commit-style subjects such as `feat(cli): ...`, `fix(tui): ...`, `docs: ...`, or `chore: ...`. Keep commits focused. Pull requests should explain the behavior change, list validation commands and platform-specific limitations, link an issue when applicable, and include terminal/UI evidence for user-visible changes. Do not commit credentials, local settings, or generated publish artifacts.
