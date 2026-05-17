# Issue 01 — Repository & Solution Setup

## Summary

Create the repository, solution file, and project structure for Hyperwyc. This establishes the foundation all other issues build on.

## Background

Hyperwyc ships as two NuGet packages:
- **`Hyperwyc`** — core package with interfaces, handler, and in-memory store. No storage dependency.
- **`Hyperwyc.Cabinet`** — storage provider backed by Cabinet. Depends on `Hyperwyc`.

A sample solution (`Hyperwyc.Sample`) is also needed for the POC but is tracked separately.

## Acceptance Criteria

- [x] Solution file `Hyperwyc.sln` exists at the repo root.
- [x] `src/Hyperwyc/Hyperwyc.csproj` — class library targeting `netstandard2.1` and/or `net10.0` (TBD).
- [x] `src/Hyperwyc.Cabinet/Hyperwyc.Cabinet.csproj` — class library; project reference to `Hyperwyc`.
- [x] `tests/Hyperwyc.Tests/Hyperwyc.Tests.csproj` — xUnit test project; project reference to `Hyperwyc`.
- [x] `tests/Hyperwyc.Cabinet.Tests/Hyperwyc.Cabinet.Tests.csproj` — xUnit test project; project reference to `Hyperwyc.Cabinet`.
- [x] `.editorconfig` and `Directory.Build.props` for consistent build settings.
- [x] `.gitignore` appropriate for a .NET solution.
- [x] `README.md` at the repo root (can start as a copy of the design-doc version).
- [x] CI pipeline (GitHub Actions) that restores, builds, and runs tests on push.

## Notes

- Target framework choices should support .NET MAUI, Blazor WASM, and plain ASP.NET Core where practical.
- The `Hyperwyc` package must have **no** transitive dependency on Cabinet or any storage library.
