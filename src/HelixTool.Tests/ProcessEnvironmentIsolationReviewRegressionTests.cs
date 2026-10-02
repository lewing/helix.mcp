using System.Reflection;
using HelixTool.Core.AzDO;
using HelixTool.Tests.AzDO;
using HelixTool.Tests.Collect;
using HelixTool.Tests.Helix;
using Xunit;

namespace HelixTool.Tests;

[Collection("AzdoTokenEnv")]
public sealed class ProcessEnvironmentIsolationReviewRegressionTests
{
    [Fact]
    public void ProcessGlobalReadersAndWriters_ShareDisabledParallelCollection_Finding4170560914()
    {
        Type[] classes =
        [
            typeof(AzdoCacheKeyReviewRegressionTests), typeof(AzdoBuildCollectorPr2Tests),
            typeof(EvalModeAzdoAuthTests), typeof(AzdoTokenAccessorTests), typeof(AzdoSecurityTests),
            typeof(ApiKeyMiddlewareTests), typeof(ApiKeyScopedRequestIsolationTests),
            typeof(HttpTransportSessionModeTests), typeof(HttpContextHelixTokenAccessorTests),
            typeof(SearchFileTests), typeof(SearchLogTests), typeof(TrxParsingTests),
            typeof(XunitXmlParsingTests), typeof(AzdoSearchLogTests), typeof(SearchBuildLogAcrossStepsTests),
            typeof(CliJsonAcquisitionEnvelopeRegressionTests), typeof(HelixOperationClassificationRegressionTests),
            typeof(IndependentReviewRegressionTests), typeof(CacheOptionsTests), typeof(SnapshotCommandOutputTests),
            typeof(AzdoEvidenceSurfaceTests), typeof(AcquisitionRedactionRegressionTests),
            typeof(AzdoCliAcquisitionErrorTests), typeof(AzdoPagingPr1CliTests),
            typeof(AzdoPagingPr1CacheCompatibilityTests), typeof(SnapshotMissErrorShapeTests)
        ];
        Assert.All(classes, type =>
        {
            var collection = Assert.Single(type.GetCustomAttributesData(),
                attribute => attribute.AttributeType == typeof(CollectionAttribute));
            Assert.Equal("AzdoTokenEnv", collection.ConstructorArguments[0].Value);
        });
        Assert.True(typeof(AzdoTokenEnvCollection)
            .GetCustomAttribute<CollectionDefinitionAttribute>()?.DisableParallelization);
    }

    [Fact]
    public async Task PartitionFixtures_RestorePreexistingEnvironment_Finding4170560914()
    {
        var names = new[] { "AZDO_TOKEN", "AZDO_TOKEN_TYPE", EvalSnapshotAzdoPartitionSelector.EnvironmentVariable };
        var originals = names.Select(Environment.GetEnvironmentVariable).ToArray();
        var sentinels = new[] { "environment-isolation-token", "bearer", "cache-deadbeef" };
        try
        {
            for (var index = 0; index < names.Length; index++)
                Environment.SetEnvironmentVariable(names[index], sentinels[index]);

            using (var collector = new AzdoBuildCollectorPr2Tests())
                Assert.All(names, name => Assert.Null(Environment.GetEnvironmentVariable(name)));
            Assert.Equal(sentinels, names.Select(Environment.GetEnvironmentVariable));

            using (var eval = new EvalModeAzdoAuthTests())
                Assert.All(names, name => Assert.Null(Environment.GetEnvironmentVariable(name)));
            Assert.Equal(sentinels, names.Select(Environment.GetEnvironmentVariable));

            using (var cacheKeys = new AzdoCacheKeyReviewRegressionTests())
            {
                await cacheKeys.AuthenticatedSnapshotWithSuffixNamedProject_ReplaysFromAuthPartition_Finding4169759118();
                Assert.Equal(sentinels, names.Select(Environment.GetEnvironmentVariable));
            }
            Assert.Equal(sentinels, names.Select(Environment.GetEnvironmentVariable));
        }
        finally
        {
            for (var index = 0; index < names.Length; index++)
                Environment.SetEnvironmentVariable(names[index], originals[index]);
        }
    }

    [Fact]
    public async Task FileSearchToggleTests_RestorePreexistingEnvironment_Finding4170560914()
    {
        const string name = "HLX_DISABLE_FILE_SEARCH";
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "false");
            var search = new SearchFileTests();
            await search.SearchFile_ThrowsWhenDisabledByConfig();
            Assert.Equal("false", Environment.GetEnvironmentVariable(name));
            await search.SearchConsoleLog_ThrowsWhenDisabledByConfig();
            Assert.Equal("false", Environment.GetEnvironmentVariable(name));
            await new TrxParsingTests().ParseTrx_ThrowsWhenDisabledByConfig();
            Assert.Equal("false", Environment.GetEnvironmentVariable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }
}
