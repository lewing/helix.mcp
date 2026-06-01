# Ripley status: numeric `buildIdOrUrl` alias fix

Date: 2026-06-01

## Commit

- Fix commit: `92c2655abab198801084c0e14d3ced31fc96badb`
- Branch: `ripley/azdo-buildidorurl-aliases`

## JsonElement value-kind coercion approach

`AddBindingErrorFilter` still normalizes aliases before SDK binding, but now routes the alias value through `CoerceToStringElement(...)` before assigning the canonical `buildIdOrUrl` key.

- If the alias value is already `JsonValueKind.String`, it is preserved as-is.
- If the alias value is `Number`, `True`, `False`, or another non-string JSON kind, the raw JSON text is serialized into a JSON string element before assignment.
- This means real telemetry like `build_id: 2989057` becomes canonical `buildIdOrUrl: "2989057"`, which can bind to the tool method's `string buildIdOrUrl` parameter.

## Why the ordered alias structure was chosen

The alias map changed from `Dictionary<string, string>` to an ordered `(string Alias, string Canonical)[]` tuple array. Precedence is part of the contract for multi-alias/no-canonical calls, so relying on dictionary enumeration order was unnecessarily fragile. Tuple order now explicitly documents and enforces: `build_id` wins over `buildId`, which wins over `buildUrl`.

## Validation

- `dotnet build --nologo`: 0 warnings, 0 errors.
- `dotnet test --nologo --no-build`: 1312 passed, 2 skipped, 0 failed.
