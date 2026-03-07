# XXE Prevention Test Regression

**Author:** Lambert (Tester)
**Date:** 2026-03-01
**Severity:** Security — needs review

## Issue

The existing test `TrxParsingTests.ParseTrx_RejectsXxeDtdDeclaration` now fails after the production code refactoring that added xUnit XML auto-discovery.

**Before:** `ParseTrxResultsAsync` → `ParseTrxFile` → throws `XmlException` (DTD prohibited)
**After:** `ParseTrxResultsAsync` → `TryParseTestFile` → `DetectTestFileFormat` or `ParseTrxFile` catches/swallows the `XmlException` → returns `null` → `HelixException("Found XML files but none were in a recognized format")`

## Concern

The DTD-bearing XML file is no longer rejected at the XML parsing level — the `XmlException` is being swallowed somewhere in `TryParseTestFile` or `DetectTestFileFormat`. While this means the DTD content is not processed (good), the error message no longer indicates a security rejection (bad for diagnostics). We should verify that:

1. `DetectTestFileFormat` uses `DtdProcessing.Prohibit`
2. The swallowed exception doesn't silently process any DTD content
3. The test should be updated to match the new behavior (assert `HelixException` not `XmlException`)

## Recommendation

Ripley should review `DetectTestFileFormat` and `TryParseTestFile` to confirm XXE prevention is still in place. Lambert will update the test once the team confirms the expected behavior.
