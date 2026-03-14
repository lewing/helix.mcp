# Session: 2026-03-14-describe-command

- **Requested by:** Larry Ewing
- Ripley created the `HelixTool.Generators` source generator project, `McpEquivalentAttribute`, and the `hlx describe` command
- Lambert wrote 13 tests covering `CommandRegistry` and `describe` output; 1121 total tests pass
- Kane updated `SKILL.md` with the progressive discovery chain
- The source generator reads MCP `[Description]` attributes from a referenced assembly plus CLI `[Command]` and `[McpEquivalent]` metadata from source, then emits `CommandRegistry`
- Progressive disclosure now flows through three levels: `hlx describe` → `hlx describe <cmd>` → `hlx <cmd> --schema`
