# Skill: C# Central Package Management Migration

**When to use:** Migrating a multi-project .NET solution from per-csproj `<PackageReference Version="...">` to a single `Directory.Packages.props` so future package bumps are one-line edits.

## Steps

1. **Inventory.** From repo root: `find . -name "*.csproj" -not -path "*/bin/*" -not -path "*/obj/*"` then `cat` each one. Build a table of every distinct `(PackageId, Version)` pair and which projects use it.

2. **Resolve drift.** For any package referenced at multiple versions, pick the higher one (or the version actually being tested in CI). Surface these explicitly in the commit message — CPM consolidation is the moment to fix drift, not hide it.

3. **Create `Directory.Packages.props`** at the repo root (sibling of the `.sln`/`.slnx`):

   ```xml
   <Project>
     <PropertyGroup>
       <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
       <!-- Add only if any project pins a floating version like 17.* or 10.*-* -->
       <CentralPackageFloatingVersionsEnabled>true</CentralPackageFloatingVersionsEnabled>
     </PropertyGroup>
     <ItemGroup>
       <PackageVersion Include="PackageId" Version="x.y.z" />
       <!-- ... one entry per distinct package ... -->
     </ItemGroup>
   </Project>
   ```

   MSBuild auto-imports any `Directory.Packages.props` from each project up to the root — no `<Import>` needed.

4. **Strip `Version=` from every `PackageReference`** in every csproj. Keep `Include=` and any sibling attributes (`PrivateAssets`, `OutputItemType`, `IncludeAssets`, etc.). Example:
   - Before: `<PackageReference Include="xunit" Version="2.*" />`
   - After:  `<PackageReference Include="xunit" />`

5. **Include analyzer/source-generator projects too.** netstandard2.0 source generator csprojs (e.g. with `Microsoft.CodeAnalysis.CSharp`) participate in CPM the same way. Don't skip them.

6. **Restore.** `dotnet restore`. Common failures and fixes:
   - **NU1011** — "PackageVersion items cannot specify a floating version." → Add `<CentralPackageFloatingVersionsEnabled>true</CentralPackageFloatingVersionsEnabled>` to `Directory.Packages.props`. Alternative: pin to a concrete version, but that loses the original intent.
   - **NU1008** — "Projects that use central package version management should not define the version on the PackageReference items." → A `Version=` attribute slipped through. Re-grep `PackageReference.*Version=` across all csprojs.
   - **NU1507** — "There are N package sources defined… please map your package sources." → Pre-existing multi-source NuGet.config config that CPM now warns about. Out of scope for the migration commit; flag as follow-up. Fix later by adding `<packageSourceMapping>` to `NuGet.config`.

7. **Build.** `dotnet build --no-restore`. CPM is a metadata change; if restore passes, build should pass too.

8. **Commit as one atomic change.** Bundle the `.props` creation + every csproj edit in a single commit. Don't interleave with feature work — reviewers should be able to verify "no version changed except where called out" by reading the diff.

## Gotchas

- **`GlobalPackageReference`** (in `Directory.Packages.props`) injects a `PackageReference` into every project. Useful for analyzers like `Microsoft.SourceLink.GitHub`, but easy to overuse. Prefer per-project `PackageReference` unless the package truly must be everywhere.
- **Transitive pinning:** CPM offers `<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>` to lock transitive package versions too. Powerful but can cause restore failures with conflicting graphs — opt in deliberately, not as part of the initial migration.
- **`Update` vs `Include`:** A `<PackageVersion>` uses `Include`. Inside a project, `<PackageReference Update="X" Version="..." />` overrides the central version for that project only. Use sparingly; prefer raising the central version.
- **Generated/SDK packages:** `Microsoft.NET.Sdk.Web` and similar SDKs auto-add some package references. CPM handles them transparently — you don't need to declare versions for SDK-managed packages unless you explicitly reference them.

## Verification checklist

- [ ] `Directory.Packages.props` exists at repo root with `ManagePackageVersionsCentrally=true`
- [ ] `grep -rn 'PackageReference.*Version=' --include='*.csproj'` returns nothing
- [ ] Every `<PackageVersion>` Include matches at least one `<PackageReference>` Include somewhere
- [ ] `dotnet restore` succeeds
- [ ] `dotnet build` succeeds with the same warning/error count as before (NU1507 may newly appear — that's acceptable)
- [ ] Commit message explicitly notes any version conflicts that were resolved upward
