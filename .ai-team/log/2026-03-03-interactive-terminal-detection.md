# 2026-03-03 Interactive terminal detection

**Requested by:** Larry Ewing

## Summary

Ripley updated `Program.cs` to auto-detect interactive terminal context using `Console.IsInputRedirected`. When stdin is interactive, the CLI now defaults to showing `--help` output. When stdin is redirected (piped by MCP host), it starts the MCP server as before.

Lambert verified: 373 tests pass, `--help` output works correctly.

**Commit:** 1ee7fbd — pushed to `origin/main`.
