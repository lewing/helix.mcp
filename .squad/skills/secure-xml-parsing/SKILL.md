# SKILL: Secure XML Parsing for Untrusted Content

**Confidence:** low
**Source:** earned
**Domain:** .NET / C# / Security

## When to Apply

When parsing XML files whose content originates from untrusted or semi-trusted sources (user uploads, CI artifacts, API responses, file downloads from external systems).

## The Pattern

```csharp
var settings = new XmlReaderSettings
{
    DtdProcessing = DtdProcessing.Prohibit,
    XmlResolver = null,
    MaxCharactersInDocument = 50_000_000 // ~50MB character limit
};

using var reader = XmlReader.Create(stream, settings);
var doc = XDocument.Load(reader);
```

## Why This Matters

1. **XXE (XML External Entity) attacks** — Default `XDocument.Load(stream)` allows `<!ENTITY>` declarations that can read local files (`file:///etc/passwd`) or make HTTP requests to internal services (SSRF). `DtdProcessing.Prohibit` blocks all DTD processing.

2. **Billion Laughs (XML bomb)** — Nested entity expansion can turn a 1KB XML file into gigabytes of memory consumption. `DtdProcessing.Prohibit` prevents this. `MaxCharactersInDocument` provides a secondary defense.

3. **External resolver SSRF** — Even with DTDs disabled, `XmlResolver` could resolve external schema references. Setting it to `null` prevents all external resource resolution.

## Checklist

- [ ] `DtdProcessing = DtdProcessing.Prohibit` — always
- [ ] `XmlResolver = null` — always
- [ ] `MaxCharactersInDocument` set to a reasonable limit — for untrusted input
- [ ] File size check BEFORE parsing (don't load a 500MB file into `XDocument`)
- [ ] Content truncation on extracted text fields (error messages, descriptions) to prevent downstream resource exhaustion (e.g., LLM context window overflow)

## Common Mistakes

- Using `XDocument.Load(path)` or `XDocument.Load(stream)` directly — these use default settings which allow DTDs
- Setting `DtdProcessing = DtdProcessing.Ignore` instead of `Prohibit` — `Ignore` still processes some DTD constructs
- Forgetting `XmlResolver = null` — DTD prohibition alone doesn't prevent all external resolution paths
- Not checking file size before parsing — `XDocument` loads the entire DOM into memory

## References

- [OWASP XXE Prevention Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/XML_External_Entity_Prevention_Cheat_Sheet.html)
- [Microsoft: XmlReaderSettings.DtdProcessing](https://learn.microsoft.com/dotnet/api/system.xml.xmlreadersettings.dtdprocessing)
