# Ripley Decision: HelixTool roll-forward policy

- **Decision:** Use `<RollForward>Major</RollForward>` in `src/HelixTool/HelixTool.csproj`.
- **Not chosen:** `LatestMajor`, because we want conservative behavior that prefers the exact target runtime when it is installed and only moves to the lowest higher major when the target major is missing.
- **Rationale:** `hlx` is shipped as a global `dotnet tool`, so its generated runtimeconfig controls whether the executable starts on machines that only have a newer shared framework installed. `Major` allows `net10.0` to run on .NET 11+ when .NET 10 is absent, avoiding startup failures for both the CLI and `hlx mcp serve`.
- **Scope:** This applies only to the executable project `HelixTool`. Library projects do not produce the tool runtimeconfig and should not be changed for this policy.
