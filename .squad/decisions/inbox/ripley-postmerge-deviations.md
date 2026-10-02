# Ripley post-merge review deviations

Date: 2026-10-02

No intentional deviations from Larry's post-merge review guidance.

Implementation notes:

- Retry-After is honored beyond `--retry-max-delay`; only computed exponential backoff is capped by that option. A one-hour safety ceiling remains to prevent an unbounded single sleep from a hostile or malformed server value.
- Helix monitor parser de-duplication now includes the monitor job label so distinct leg rows sharing the same Helix job/work-item coordinate are still represented in collection manifests.
- Additional item #19 had no intentional deviation. I treated empty raw AzDO log metadata rows as corruption only for keys that must contain cached log content; valid empty success payloads for other key families remain unchanged.
