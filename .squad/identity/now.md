---
updated_at: 2026-05-21T17:58:00Z
focus_area: Post-v0.7.1 — design pass for surfacing new Helix.Client WorkItemSummary fields (ExitCode + ConsoleOutputUri) through IWorkItemSummary adapter
active_issues: []
---

# What We're Focused On

v0.7.1 shipped — dependency refresh including Microsoft.DotNet.Helix.Client 11.0.0-beta.26265.121 which adds ExitCode + ConsoleOutputUri to WorkItemSummary. Dallas is now running a design pass to decide how to surface these fields through IWorkItemSummary and whether to use ConsoleOutputUri to short-circuit GetConsoleLogAsync round-trips. Pending: proposal review → Ripley implementation → v0.7.2 or v0.8.0.
