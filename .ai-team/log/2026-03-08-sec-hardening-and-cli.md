# Session: SEC Hardening & AzDO CLI

- **Date:** 2026-03-08
- **Requested by:** Larry Ewing

## Summary

Ripley implemented SEC-2 (IHttpClientFactory replacing static HttpClient with named clients "HelixDownload" and "AzDO"), SEC-3 (streaming with `HttpCompletionOption.ResponseHeadersRead` for all AzDO requests), SEC-4 (5-minute HttpClient timeout), and 9 AzDO CLI subcommands (`azdo-build`, `azdo-builds`, `azdo-timeline`, `azdo-log`, `azdo-changes`, `azdo-test-runs`, `azdo-test-results`, `azdo-artifacts`, `azdo-test-attachments`) in `AzdoCommands` class.

Lambert wrote 53 new tests:
- `HttpClientConfigurationTests` — 13 tests (timeout vs. cancellation, factory readiness)
- `StreamingBehaviorTests` — 18 tests (tail, disposal, special chars, error handling)
- `AzdoCliCommandTests` — 22 tests (build, timeline, log, changes, test runs/results, artifacts)

All 753 tests passing, build clean.
