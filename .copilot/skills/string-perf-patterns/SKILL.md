# SKILL: String Performance Patterns in .NET

## When to apply
When writing or reviewing C# code that processes text content — especially log files, API responses, or search operations that may handle multi-MB strings.

## Patterns to avoid

### 1. Chained `.Replace()` for line-ending normalization
```csharp
// BAD — 2 intermediate full-size strings
var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
```
**Fix:** Use a span-based line enumerator, or `string.Create` with a single-pass copy that normalizes as it goes. For search operations, consider handling mixed line endings during iteration instead of pre-normalizing.

### 2. `Split()` + `Join()` for tail/head operations
```csharp
// BAD — allocates string[] of all lines just to get last N
var lines = content.Split('\n');
return string.Join('\n', lines[^tailLines..]);
```
**Fix:** Reverse-scan for Nth `\n` using `ReadOnlySpan<char>`:
```csharp
var span = content.AsSpan();
int pos = span.Length;
for (int i = 0; i < tailLines && pos > 0; i++)
{
    pos = span[..pos].LastIndexOf('\n');
    if (pos < 0) { pos = 0; break; }
}
return pos > 0 ? content[(pos + 1)..] : content;
```

### 3. Substring allocation in pattern matching
```csharp
// BAD — allocates new string every call
name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
```
**Fix:** Use spans:
```csharp
name.AsSpan().EndsWith(pattern.AsSpan(1), StringComparison.OrdinalIgnoreCase);
```

### 4. Triple-iteration for categorization
```csharp
// BAD — 3 passes over same list
Binlogs = files.Where(f => IsBinlog(f)).Select(Map).ToList();
TestResults = files.Where(f => IsTest(f)).Select(Map).ToList();
Other = files.Where(f => !IsBinlog(f) && !IsTest(f)).Select(Map).ToList();
```
**Fix:** Single-pass loop:
```csharp
var binlogs = new List<FileInfo_>();
var testResults = new List<FileInfo_>();
var other = new List<FileInfo_>();
foreach (var f in files)
{
    var mapped = new FileInfo_ { Name = f.Name, Uri = f.Uri };
    if (IsBinlog(f.Name)) binlogs.Add(mapped);
    else if (IsTest(f.Name)) testResults.Add(mapped);
    else other.Add(mapped);
}
```

### 5. JSON serialization for large plain-text cache values
```csharp
// BAD — JsonSerializer.Serialize<string>() escapes every special char
await cache.Set(key, JsonSerializer.Serialize(logContent), ttl);
// ... later, Deserialize<string>() re-parses the entire content
```
**Fix:** Store plain text directly, or use a binary cache format. JSON wrapping of large strings doubles memory and adds O(n) processing for escape/unescape.

## Severity heuristic
- **P0:** Pattern appears in a loop that runs per-log/per-line (search paths)
- **P1:** Pattern runs once per API request but on large data (tail trimming, cache hit)
- **P2:** Pattern on small data or one-time setup code (cache keys, query strings)
