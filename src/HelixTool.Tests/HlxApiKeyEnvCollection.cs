using Xunit;

namespace HelixTool.Tests;

/// <summary>
/// Shared non-parallel xUnit collection for every test that reads or mutates the ambient
/// <c>HLX_API_KEY</c> process environment variable (see
/// <see cref="HelixTool.Mcp.ApiKeyMiddleware.EnvVarName"/>).
///
/// <para>Both <see cref="ApiKeyMiddlewareTests"/> and the tests that boot the real host via
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// (<c>HttpTransportSessionModeTests</c>, <c>ApiKeyScopedRequestIsolationTests</c>) observe this
/// single ambient variable — the real host reads it exactly once, at pipeline-build time, in
/// <c>ApiKeyMiddlewareExtensions.UseApiKeyAuthIfConfigured</c>. xUnit runs different test classes
/// in parallel by default (only tests within one class are serialized against each other), so
/// without this shared collection two classes could race: one setting/clearing the variable
/// while another's <c>WebApplicationFactory</c> lazily builds its host and reads it, producing a
/// spuriously-authenticated or spuriously-401'd run. Placing every HLX_API_KEY-touching class in
/// this collection (<c>DisableParallelization = true</c>) forces them to run one at a time.</para>
/// </summary>
[CollectionDefinition("HlxApiKeyEnv", DisableParallelization = true)]
public class HlxApiKeyEnvCollection;
