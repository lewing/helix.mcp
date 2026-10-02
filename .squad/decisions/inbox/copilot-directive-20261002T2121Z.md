### 2026-10-02T16:22:25-05:00: User directive (supersedes 16:21 version)
**By:** Larry Ewing (via Copilot)
**What:** Wherever the coordinator's model selection, fallback chains, or "switch to code specialist" rules would pick a GPT model (gpt-5.x, gpt-5.x-codex, gpt-5.x-mini, gpt-4.1), use a MIX of `gpt-6.1-sol` and `hydrafusion` instead — alternate between them across spawns (experiment) and note which model each agent ran on so results can be compared. Claude/Gemini choices are unaffected.
**Why:** User request — trying the two side by side to see how they perform.
