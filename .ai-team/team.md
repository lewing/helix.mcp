# Meet the Team

This project uses [Squad](https://github.com/bradygaster/squad), an AI team orchestration framework for GitHub Copilot. Squad lets you define a roster of specialized AI agents that collaborate on a codebase — each with a clear role, scope, and set of responsibilities.

Here's who's on the team:

| Name | Role | What they do |
|------|------|-------------|
| **Dallas** | Lead | Architecture decisions, scope management, code review |
| **Ripley** | Backend Dev | Core library, CLI commands, MCP tools, Helix API integration |
| **Lambert** | Tester | Unit tests, quality gates, edge case coverage |
| **Kane** | Docs | README, API docs, usage guides, llmstxt output |
| **Ash** | Product Analyst | Threat modeling, security analysis, product strategy |
| **Scribe** | *(silent)* | Session memory, decision logs, history tracking |

Each agent has a charter in `.ai-team/agents/<name>/charter.md` that defines their responsibilities and boundaries. Agents communicate through shared decision logs and history files, and defer to each other based on expertise.
