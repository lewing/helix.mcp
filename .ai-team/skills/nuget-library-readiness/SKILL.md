---
name: "nuget-library-readiness"
description: "Checklist for evaluating whether a .NET project is ready to publish as a standalone NuGet library"
domain: "dotnet-packaging"
confidence: "high"
source: "earned"
---

## Context
When a shared library project (`*.Core`, `*.Abstractions`, etc.) is consumed internally by sibling projects in a solution, it may accumulate dependencies and code that are inappropriate for external NuGet publication. This skill provides a systematic evaluation checklist.

## Patterns

### Dependency Audit
Check each `PackageReference` in the csproj. For every dependency ask: "Would an external consumer who only wants the library's core functionality need this?" If no, the code causing that dependency should move to a separate project.

Common offenders:
- Presentation framework packages (MCP SDK, Spectre.Console, ConsoleAppFramework) leaking into core libraries
- Hosting packages (`Microsoft.Extensions.Hosting`) in libraries that should only need DI abstractions
- Test-only or tool-only packages

### Nested Type Extraction
Public record/class types nested inside service classes are a library anti-pattern:
- Harder to discover in IntelliSense
- Forces `ContainingType.NestedType` syntax
- Signals "implementation detail" when they're actually part of the public API contract

Extract to top-level types in a `Models/` folder before publishing. This is source-breaking but binary-compatible.

### Internal→Public Visibility Audit
When splitting projects, `internal` members that were accessible via `InternalsVisibleTo` across sibling projects may need to become `public`. Audit all `internal static` methods that are called cross-project.

### Decorator-Based Optional Features
Features like caching should use the decorator pattern so consumers can opt in/out. If the decorator's backing store requires a heavy dependency (e.g., `Microsoft.Data.Sqlite`), consider whether it should be a separate package. Rule of thumb: if the dependency is < 1MB and widely used, keep it together.

### NuGet Metadata Checklist
Required for a publishable library:
- `<PackageId>` — consistent naming with other packages from the same author
- `<Description>` — what the library does, not what the tool does
- `<Authors>`
- `<PackageLicenseExpression>`
- `<PackageTags>`
- `<PackageReadmeFile>` — library-focused README, not CLI usage docs
- `<GenerateDocumentationFile>true</GenerateDocumentationFile>` — enables XML docs in IntelliSense
- No `<PackAsTool>` (that's for CLI tools)

### Versioning
For monorepos with a core library + apps: use a single version in `Directory.Build.props`. Independent versioning adds process complexity that rarely justifies itself until the library has external consumers with different release cadences.

## Anti-Patterns
- **Publishing a library with presentation-layer dependencies** — forces consumers to pull MCP SDK, Spectre.Console, etc.
- **Creating too many packages too early** — splitting `Core` into `Core.Caching`, `Core.Auth` before there's demand creates package management overhead
- **Breaking nested type extraction into multiple PRs** — do it all at once; the mechanical refactor is low-risk and a partial migration is confusing

## Execution Notes (validated 2026-03-03)
- When moving files to a new project, keeping the original namespace avoids call-site `using` changes. The project name conveys architectural intent; the namespace conveys API surface.
- Nested record extraction produced zero call-site changes when callers used `var` / type inference exclusively (common in C#). Check with `grep -rn 'ClassName\.NestedType'` before estimating scope.
- `GenerateDocumentationFile=true` will surface CS1591 warnings for all undocumented public members — treat as a backlog item, not a blocker.
- CI workflows that validate version from csproj need updating when version moves to `Directory.Build.props`.
