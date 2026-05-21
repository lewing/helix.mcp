---
name: "path-boundary-comparison"
description: "How to perform root-path containment checks without weakening security on case-sensitive filesystems"
domain: "security"
confidence: "medium"
source: "earned"
---

## Context
Use this when code must verify that a computed file path stays inside a trusted root directory. This commonly appears in cache stores, download helpers, extraction code, and any path traversal defense.

## Patterns
- Normalize both the candidate path and the root with `Path.GetFullPath(...)` before comparing.
- Preserve the root directory boundary explicitly by trimming trailing separators from the root and then adding exactly one `Path.DirectorySeparatorChar` for child-path checks.
- Compare security boundaries with `StringComparison.Ordinal`, not ignore-case comparisons. Case folding can make a sibling path appear to be under the trusted root on case-sensitive filesystems.
- Allow the exact root path as a special case in addition to child paths.
- Add a regression test for a sibling path that differs only by casing (for example `root` vs `ROOT`) so the security fix stays pinned if someone later reintroduces ignore-case comparisons.

## Examples
```csharp
var fullPath = Path.GetFullPath(path);
var fullRoot = Path.GetFullPath(root);
var rootWithoutSeparator = Path.TrimEndingDirectorySeparator(fullRoot);
var rootedPrefix = rootWithoutSeparator + Path.DirectorySeparatorChar;

if (!fullPath.StartsWith(rootedPrefix, StringComparison.Ordinal) &&
    !string.Equals(fullPath, rootWithoutSeparator, StringComparison.Ordinal))
{
    throw new ArgumentException("Resolved path escapes the trusted root.");
}
```

## Anti-Patterns
- Using `StartsWith(..., StringComparison.OrdinalIgnoreCase)` for a security boundary check.
- Comparing against a root string without enforcing a separator boundary, which can make `/tmp/root2` look like it is inside `/tmp/root`.
- Comparing raw user input paths before canonicalizing them with `Path.GetFullPath(...)`.
