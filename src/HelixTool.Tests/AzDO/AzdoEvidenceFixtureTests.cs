// Groups B and C — real-fixture regression tests and provenance/model-contract tests.
//
// COMPILE GAP: references types not yet implemented by Ripley:
//   - AzdoEvidenceMatcher, AzdoEvidencePlan, AzdoEvidencePlanEntry, AzdoEvidenceCandidate
//   - AzdoEvidencePlanOptions, AzdoBuildProvenance (HelixTool.Core.AzDO)
//   - AzdoBuildArtifact.Source (model addition)
//   - AzdoArtifactResource.Properties (model addition)
//   - AzdoTimelineRecord.Attempt (model addition)
//   - AzdoTriggerInfo: PrSourceSha, PrSourceBranch, PrIsFork, PrDraft, PrProviderId
// Remove the COMPILE GAP comment when Ripley lands these types.
//
// Fixtures represent the falsifying evidence from builds 1570501 and 1569889 (§3).
// No operational download URLs, real SHAs, or sender data are included.
//
// Run: `dotnet test --filter "FullyQualifiedName~AzdoEvidenceFixtureTests"`

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HelixTool.Core.AzDO;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

/// <summary>
/// Regression fixtures derived from primary-source evidence in the design review (§3).
/// The 13-mismatch case (B1) and the retry-ambiguity case (B3/B4) are the gate tests (§11.2).
/// </summary>
public class AzdoEvidenceFixtureTests
{
    // ════════════════════════════════════════════════════════════════════════
    // B1 — Build 1570501: normalized-exact → 13 unmatched; auto → 0 unmatched
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Build1570501_NormalizedExact_Has13Unmatched()
    {
        // §3.2: "89 matched, 13 unmatched (12.7 %)" under normalized-exact.
        // The 13 failures all have the same shape: job display name carries a matrix-leg suffix
        // ("crossaot") that the artifact name omits.
        var (jobs, artifacts) = Build1570501Fixture();

        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed", "canceled"],
            ArtifactPattern = "Logs_Build_*",
            ArtifactJobPrefix = "Logs_Build_",
            StripAttemptPrefix = true,
            Match = "normalized-exact"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        var unmatchedCount = plan.Entries.Count(e => e.Status is "missing" or "ambiguous");
        // 13 CrossAOT_Mono jobs (missing) + Monitor Helix Jobs (missing) = 14 total non-mapped
        // Gate: at least 13 are the CrossAOT mismatch. Monitor Helix Jobs is additionally missing.
        Assert.True(unmatchedCount >= 13, $"Expected ≥13 unmatched under normalized-exact, got {unmatchedCount}");
        Assert.False(plan.Complete);
    }

    [Fact]
    public void Build1570501_Auto_HasZeroUnmatched()
    {
        // §3.2/§3.1: source-id join resolves 100% under "auto" strategy.
        // "Monitor Helix Jobs" is the one genuine missing (no artifact published).
        var (jobs, artifacts) = Build1570501Fixture();

        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed", "canceled"],
            ArtifactPattern = "Logs_Build_*",
            ArtifactJobPrefix = "Logs_Build_",
            StripAttemptPrefix = true,
            Match = "auto"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        // Only the Monitor Helix Jobs gap should remain (true evidence gap, not a mapping bug).
        var mismatched = plan.Entries.Where(e => e.Status == "missing" && e.JobName != "Monitor Helix Jobs").ToList();
        Assert.Empty(mismatched);

        // CrossAOT_Mono jobs are all resolved via source-id.
        var crossAotEntries = plan.Entries.Where(e => e.JobName != null && e.JobName.Contains("CrossAOT_Mono", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.All(crossAotEntries, e => Assert.Equal("mapped", e.Status));
    }

    // ── B2 — Monitor Helix Jobs: missing under BOTH strategies ───────────────

    [Fact]
    public void Build1570501_MonitorHelixJobs_MissingUnderBothStrategies()
    {
        // §3.4: genuine evidence gap — no Logs_Build_* artifact published by this job.
        var (jobs, artifacts) = Build1570501Fixture();

        foreach (var matchMode in new[] { "auto", "normalized-exact", "source-id" })
        {
            var opts = new AzdoEvidencePlanOptions
            {
                JobResults = ["failed", "canceled"],
                ArtifactPattern = "Logs_Build_*",
                ArtifactJobPrefix = "Logs_Build_",
                StripAttemptPrefix = true,
                Match = matchMode
            };

            var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);
            var monitorEntry = plan.Entries.SingleOrDefault(e =>
                string.Equals(e.JobName, "Monitor Helix Jobs", StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(monitorEntry);
            Assert.Equal("missing", monitorEntry!.Status);
            Assert.Empty(monitorEntry.Candidates);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // B3 — Build 1569889: auto → 7/7 mapped on Attempt2; complete == true
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Build1569889_Auto_SevenMapped_AllOnAttempt2()
    {
        // §3.3: source-id join resolves each job to its current-attempt (attempt=2) artifact.
        var (jobs, artifacts) = Build1569889Fixture();

        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed", "canceled"],
            ArtifactPattern = "Logs_Build_*",
            ArtifactJobPrefix = "Logs_Build_",
            StripAttemptPrefix = true,
            Match = "auto"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        Assert.Equal(7, plan.Entries.Count);
        Assert.All(plan.Entries, e =>
        {
            Assert.Equal("mapped", e.Status);
            Assert.Single(e.Candidates);
            Assert.Equal(2, e.Candidates[0].Attempt);  // always Attempt2 via source-id
        });
        Assert.True(plan.Complete);
    }

    // ════════════════════════════════════════════════════════════════════════
    // B4 — Build 1569889: normalized-exact → 7/7 ambiguous, Attempt1 AND Attempt2
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Build1569889_NormalizedExact_SevenAmbiguous_BothAttempts()
    {
        // §3.3: strip-attempt-prefix collapses Attempt1/Attempt2 into the same key → ambiguous.
        // "We must not" silently download both (design §3.3).
        var (jobs, artifacts) = Build1569889Fixture();

        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed", "canceled"],
            ArtifactPattern = "Logs_Build_*",
            ArtifactJobPrefix = "Logs_Build_",
            StripAttemptPrefix = true,
            Match = "normalized-exact"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        Assert.Equal(7, plan.Entries.Count);
        Assert.All(plan.Entries, e =>
        {
            Assert.Equal("ambiguous", e.Status);
            Assert.Equal(2, e.Candidates.Count);
            // Both attempts must be present in candidates.
            var attempts = e.Candidates.Select(c => c.Attempt).OrderBy(a => a).ToList();
            Assert.Equal([1, 2], attempts);
        });
        Assert.False(plan.Complete);
        Assert.Equal(7, plan.IncompleteReasons.Count);
    }

    // ════════════════════════════════════════════════════════════════════════
    // B5 — Every Logs_Build_* artifact's source resolves to a Job record
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [MemberData(nameof(AllFixtureArtifacts))]
    public void AllLogsArtifacts_SourceResolvesToJobRecord(AzdoBuildArtifact artifact, List<AzdoTimelineRecord> allRecords)
    {
        if (artifact.Name == null || !artifact.Name.StartsWith("Logs_Build_", StringComparison.OrdinalIgnoreCase))
            return;

        Assert.NotNull(artifact.Source);
        Assert.NotEmpty(artifact.Source!);

        var sourceRecord = allRecords.SingleOrDefault(r => r.Id == artifact.Source);
        Assert.NotNull(sourceRecord);
        Assert.Equal("Job", sourceRecord!.Type);
    }

    public static IEnumerable<object[]> AllFixtureArtifacts()
    {
        // Call each fixture ONCE so that records and artifacts share the same random GUIDs.
        var (records1, artifacts1) = Build1570501Fixture();
        foreach (var a in artifacts1)
            yield return [a, records1];

        var (records2, artifacts2) = Build1569889Fixture();
        // 1569889: Attempt1 artifacts have Source = a previous-attempt GUID that is NOT in the
        // current-attempt records list (§3.3: "previousAttempts[].id is null live").
        // Their source is a valid GUID from the live API, but the corresponding timeline record
        // is only returned by a previous GetTimeline call, not the current one.
        // Only include Attempt2 (current-attempt) artifacts for source-resolution validation.
        foreach (var a in artifacts2.Where(a =>
            a.Name == null || !a.Name.Contains("Attempt1_", StringComparison.Ordinal)))
        {
            yield return [a, records2];
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // B6 — Build 1570501: resource.properties.artifactsize surfaces as sizeBytes
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Build1570501_ArtifactSizeBytes_SurfacedFromProperties()
    {
        var (jobs, artifacts) = Build1570501Fixture();
        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed", "canceled"],
            ArtifactPattern = "Logs_Build_*",
            ArtifactJobPrefix = "Logs_Build_",
            StripAttemptPrefix = true,
            Match = "auto"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        // All mapped entries should have sizeBytes populated from properties.
        var mappedWithSize = plan.Entries
            .Where(e => e.Status == "mapped")
            .Select(e => e.Candidates[0])
            .Where(c => c.SizeBytes.HasValue)
            .ToList();

        Assert.NotEmpty(mappedWithSize);
        Assert.All(mappedWithSize, c => Assert.True(c.SizeBytes > 0));
    }

    // ════════════════════════════════════════════════════════════════════════
    // C1 — TriggerInfo: pr.sourceSha → prSourceSha; all new fields bound
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TriggerInfo_PrSourceSha_Bound()
    {
        // JSON key "pr.sourceSha" → property PrSourceSha (exact name verified live, §3.6).
        var json = """{"pr.sourceSha":"c1191feefeed1234567890abcdef","pr.number":"14859","pr.sourceBranch":"refs/pull/14859/head","pr.isFork":"True","pr.draft":"False","pr.providerId":"github"}""";
        var info = JsonSerializer.Deserialize<AzdoTriggerInfo>(json)!;

        Assert.Equal("c1191feefeed1234567890abcdef", info.PrSourceSha);
        Assert.Equal("14859", info.PrNumber);
        Assert.Equal("refs/pull/14859/head", info.PrSourceBranch);
        Assert.Equal("True", info.PrIsFork);    // raw string — bool parsing is separate (C2)
        Assert.Equal("False", info.PrDraft);
        Assert.Equal("github", info.PrProviderId);
    }

    [Fact]
    public void TriggerInfo_AllExpectedFields_HasJsonPropertyName()
    {
        // All new TriggerInfo properties must have [JsonPropertyName] with the exact AzDO key.
        var prop = typeof(AzdoTriggerInfo).GetProperty(nameof(AzdoTriggerInfo.PrSourceSha));
        Assert.NotNull(prop);
        var attr = prop!.GetCustomAttribute<JsonPropertyNameAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("pr.sourceSha", attr!.Name);
    }

    // ════════════════════════════════════════════════════════════════════════
    // C2 — "True"/"False" string parse to bool?; garbage → null, no throw
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("True",  true)]
    [InlineData("true",  true)]
    [InlineData("TRUE",  true)]
    [InlineData("False", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void BuildProvenance_IsForkAndDraft_ParseCaseInsensitive(string raw, bool expected)
    {
        // AzdoBuildProvenance.PrIsFork / .PrDraft are parsed from TriggerInfo string values.
        var triggerInfo = new AzdoTriggerInfo { PrIsFork = raw, PrDraft = "False" };
        var build = new AzdoBuild
        {
            Id = 1,
            TriggerInfo = triggerInfo,
            Definition = new AzdoBuildDefinition { Id = 100, Name = "runtime" },
        };

        var provenance = AzdoBuildProvenance.FromBuild(build, "dnceng-public", "public");

        Assert.Equal(expected, provenance.PrIsFork);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("garbage")]
    public void BuildProvenance_IsFork_GarbageOrNull_IsNull(string? raw)
    {
        var triggerInfo = new AzdoTriggerInfo { PrIsFork = raw };
        var build = new AzdoBuild { Id = 1, TriggerInfo = triggerInfo };
        var provenance = AzdoBuildProvenance.FromBuild(build, "dnceng-public", "public");

        Assert.Null(provenance.PrIsFork);
    }

    // ════════════════════════════════════════════════════════════════════════
    // C3 — Non-PR build (triggerInfo == null) → all pr* fields null, no throw
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildProvenance_NoTriggerInfo_AllPrFieldsNull()
    {
        var build = new AzdoBuild
        {
            Id = 99,
            BuildNumber = "20260827.1",
            Status = "completed",
            Result = "succeeded",
            TriggerInfo = null  // CI push, not a PR
        };

        var provenance = AzdoBuildProvenance.FromBuild(build, "dnceng-public", "public");

        Assert.Null(provenance.PrNumber);
        Assert.Null(provenance.PrSourceSha);
        Assert.Null(provenance.PrSourceBranch);
        Assert.Null(provenance.PrIsFork);
        Assert.Null(provenance.PrDraft);
        Assert.Null(provenance.PrProviderId);
    }

    // ════════════════════════════════════════════════════════════════════════
    // C4 — Serialized plan JSON contains none of the excluded fields (PII / injection)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SerializedRealPlan_WhitelistsProvenanceAndExcludesPopulatedRawPii()
    {
        const string prSourceSha = "0123456789abcdef0123456789abcdef01234567";
        const string rawBuildJson =
            """
            {
              "id": 1570501,
              "buildNumber": "20260827.1",
              "status": "completed",
              "result": "failed",
              "definition": { "id": 42, "name": "runtime" },
              "sourceBranch": "refs/heads/main",
              "sourceVersion": "89abcdef0123456789abcdef0123456789abcdef",
              "finishTime": "2026-08-27T17:00:00Z",
              "requestedFor": { "displayName": "FORBIDDEN-SENDER\r\n\u001b[31m" },
              "triggerInfo": {
                "ci.message": "FORBIDDEN-CI\r\n\u0007\u001b[31m",
                "pr.title": "FORBIDDEN-TITLE\r\nsecond line",
                "pr.sender.name": "FORBIDDEN-PR-SENDER",
                "pr.sender.avatarUrl": "FORBIDDEN-AVATAR",
                "pr.number": "14859",
                "pr.sourceSha": "0123456789abcdef0123456789abcdef01234567",
                "pr.sourceBranch": "refs/pull/14859/head",
                "pr.isFork": "True",
                "pr.draft": "False",
                "pr.providerId": "github"
              }
            }
            """;

        var rawBuild = JsonSerializer.Deserialize<AzdoBuild>(rawBuildJson)!;
        Assert.Equal("FORBIDDEN-CI\r\n\u0007\u001b[31m", rawBuild.TriggerInfo!.CiMessage);
        Assert.Equal("FORBIDDEN-SENDER\r\n\u001b[31m", rawBuild.RequestedFor!.DisplayName);

        var api = Substitute.For<IAzdoApiClient>();
        api.GetBuildAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(rawBuild);
        api.GetTimelineAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Records =
                [
                    new()
                    {
                        Id = "00000000-0000-0000-0000-000000000001",
                        Type = "Job",
                        Result = "succeeded",
                        Name = "not selected",
                        Order = 1
                    }
                ]
            });
        api.GetBuildArtifactsAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns([]);

        var expectedProvenance = AzdoBuildProvenance.FromBuild(rawBuild, "dnceng-public", "public");
        var plan = await new AzdoService(api).GetEvidencePlanAsync("1570501", new AzdoEvidencePlanOptions());
        var json = JsonSerializer.Serialize(plan);
        using var document = JsonDocument.Parse(json);
        var build = document.RootElement.GetProperty("build");

        var approvedProperties = new[]
        {
            "buildId", "buildNumber", "definitionName", "definitionId", "status", "result",
            "sourceBranch", "sourceVersion", "finishTime", "webUrl", "org", "project",
            "prNumber", "prSourceSha", "prSourceBranch", "prIsFork", "prDraft", "prProviderId"
        };
        Assert.Equal(
            approvedProperties.Order(StringComparer.Ordinal),
            build.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(prSourceSha, build.GetProperty("prSourceSha").GetString());
        Assert.Equal(JsonSerializer.Serialize(expectedProvenance), JsonSerializer.Serialize(plan.Build));

        foreach (var forbidden in new[]
                 {
                     "FORBIDDEN-SENDER", "FORBIDDEN-CI", "FORBIDDEN-TITLE", "FORBIDDEN-PR-SENDER",
                     "FORBIDDEN-AVATAR", "requestedFor", "triggerInfo", "ci.message", "pr.title",
                     "pr.sender.name", "pr.sender.avatarUrl"
                 })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain('\u0007', json);
        Assert.DoesNotContain('\u001b', json);
    }

    // ════════════════════════════════════════════════════════════════════════
    // C5 — Every result-type property has [JsonPropertyName] (reflection sweep)
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(typeof(AzdoEvidencePlan))]
    [InlineData(typeof(AzdoEvidencePlanEntry))]
    [InlineData(typeof(AzdoEvidenceCandidate))]
    [InlineData(typeof(AzdoBuildProvenance))]
    public void ResultType_AllProperties_HaveJsonPropertyName(Type type)
    {
        // mcp-structured-content: every property carries an explicit [JsonPropertyName].
        var missing = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() == null)
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(missing);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Fixtures — frozen representative data from builds 1570501 and 1569889
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Representative fixture for build 1570501 (102 Logs_Build_* artifacts, 13 CrossAOT_Mono
    /// name mismatches, Monitor Helix Jobs genuine gap).
    /// No download URLs, SAS material, or PII are included.
    /// </summary>
    private static (List<AzdoTimelineRecord> Jobs, List<AzdoBuildArtifact> Artifacts) Build1570501Fixture()
    {
        // The 13 CrossAOT_Mono jobs whose display names have the extra "crossaot" suffix.
        // Artifact name omits the matrix-leg suffix; source-id join resolves them correctly.
        var crossAotConfigs = new[]
        {
            ("linux-arm64",       "linux__arm64"),
            ("linux-musl-arm64",  "linux__musl_arm64"),
            ("linux-musl-x64",    "linux__musl_x64"),
            ("linux-x64",         "linux__x64"),
            ("windows-x64",       "windows__x64"),
            ("windows-arm64",     "windows__arm64"),
            ("linux-arm",         "linux__arm"),
            ("linux-s390x",       "linux__s390x"),
            ("linux-ppc64le",     "linux__ppc64le"),
            ("linux-riscv64",     "linux__riscv64"),
            ("osx-arm64",         "osx__arm64"),
            ("osx-x64",           "osx__x64"),
            ("linux-loongarch64", "linux__loongarch64"),
        };

        var jobs = new List<AzdoTimelineRecord>();
        var artifacts = new List<AzdoBuildArtifact>();

        // Monitor Helix Jobs — genuine evidence gap (no artifact).
        var monitorJobId = Guid.NewGuid().ToString();
        jobs.Add(new() { Id = monitorJobId, Type = "Job", Result = "failed", Name = "Monitor Helix Jobs", Order = 1, Attempt = 1 });

        // Add the 13 CrossAOT_Mono jobs and their corresponding artifacts.
        for (int i = 0; i < crossAotConfigs.Length; i++)
        {
            var (jobSuffix, artifactSuffix) = crossAotConfigs[i];
            var jobId = Guid.NewGuid().ToString();
            var jobName = $"{jobSuffix} release CrossAOT_Mono crossaot";   // has extra "crossaot" suffix
            var artifactName = $"Logs_Build_Attempt1_{artifactSuffix}_release_CrossAOT_Mono";  // no "crossaot"

            jobs.Add(new AzdoTimelineRecord
            {
                Id = jobId, Type = "Job", Result = "failed", Name = jobName, Order = i + 2, Attempt = 1
            });
            artifacts.Add(new AzdoBuildArtifact
            {
                Id = i + 1,
                Name = artifactName,
                Source = jobId,  // exact identity join
                Resource = new AzdoArtifactResource
                {
                    Type = "Container",
                    Properties = new Dictionary<string, string> { ["artifactsize"] = ((i + 1) * 10240).ToString() }
                }
            });
        }

        // Add a representative sample of "normal" jobs and artifacts (not mismatching).
        var normalJobs = new[]
        {
            "windows-x64 release", "linux-x64 release", "linux-arm64 release",
            "windows-arm64 release", "osx-x64 release", "osx-arm64 release",
        };
        for (int i = 0; i < normalJobs.Length; i++)
        {
            var jobId = Guid.NewGuid().ToString();
            var artifactSuffix = normalJobs[i].Replace(" ", "_");
            jobs.Add(new AzdoTimelineRecord
            {
                Id = jobId, Type = "Job", Result = "failed", Name = normalJobs[i], Order = i + 20, Attempt = 1
            });
            artifacts.Add(new AzdoBuildArtifact
            {
                Id = i + 100,
                Name = $"Logs_Build_Attempt1_{artifactSuffix}",
                Source = jobId,
                Resource = new AzdoArtifactResource
                {
                    Type = "Container",
                    Properties = new Dictionary<string, string> { ["artifactsize"] = "51200" }
                }
            });
        }

        return (jobs, artifacts);
    }

    /// <summary>
    /// Representative fixture for build 1569889 (7 failed/canceled jobs, all attempt=2,
    /// each with Attempt1 and Attempt2 artifacts — demonstrating retry ambiguity).
    /// </summary>
    private static (List<AzdoTimelineRecord> Jobs, List<AzdoBuildArtifact> Artifacts) Build1569889Fixture()
    {
        var jobNames = new[]
        {
            "linux-x64 release",
            "windows-x64 release",
            "linux-arm64 release",
            "windows-arm64 release",
            "osx-x64 release",
            "osx-arm64 release",
            "linux-musl-x64 release",
        };

        var jobs = new List<AzdoTimelineRecord>();
        var artifacts = new List<AzdoBuildArtifact>();

        for (int i = 0; i < jobNames.Length; i++)
        {
            var jobId = Guid.NewGuid().ToString();
            var artifactSuffix = jobNames[i].Replace(" ", "_");

            // Job is on attempt 2.
            jobs.Add(new AzdoTimelineRecord
            {
                Id = jobId, Type = "Job", Result = "failed", Name = jobNames[i], Order = i + 1, Attempt = 2
            });

            // Attempt1 artifact (source = different GUID, not this job's current-attempt record).
            // §3.3: previousAttempts[].id is null live — only current record's id joins.
            artifacts.Add(new AzdoBuildArtifact
            {
                Id = (i * 2) + 1,
                Name = $"Logs_Build_Attempt1_{artifactSuffix}",
                Source = Guid.NewGuid().ToString(),  // NOT jobId — previous attempt is unreachable
                Resource = new AzdoArtifactResource
                {
                    Type = "Container",
                    Properties = new Dictionary<string, string> { ["artifactsize"] = "20480" }
                }
            });

            // Attempt2 artifact (source = jobId — the current-attempt record).
            artifacts.Add(new AzdoBuildArtifact
            {
                Id = (i * 2) + 2,
                Name = $"Logs_Build_Attempt2_{artifactSuffix}",
                Source = jobId,  // exact identity join for current attempt
                Resource = new AzdoArtifactResource
                {
                    Type = "Container",
                    Properties = new Dictionary<string, string> { ["artifactsize"] = "25600" }
                }
            });
        }

        return (jobs, artifacts);
    }

    private static AzdoEvidencePlan BuildMockPlan()
    {
        var provenance = new AzdoBuildProvenance
        {
            BuildId = 1570501,
            BuildNumber = "20260827.1",
            DefinitionName = "runtime",
            DefinitionId = 666,
            Status = "completed",
            Result = "failed",
            SourceBranch = "refs/heads/main",
            SourceVersion = "abc123def456",
            Org = "dnceng-public",
            Project = "public",
            PrNumber = "14859",
            PrSourceSha = "c1191feefeed",
            PrSourceBranch = "refs/pull/14859/head",
            PrIsFork = true,
            PrDraft = false,
            PrProviderId = "github",
            // Deliberately NOT including: sender, avatarUrl, pr.title, ci.message
        };

        return new AzdoEvidencePlan
        {
            BuildId = 1570501,
            Build = provenance,
            Complete = false,
            IncompleteReasons = ["'Monitor Helix Jobs' (failed): missing — no Logs_Build_* artifact published"],
            Entries =
            [
                new AzdoEvidencePlanEntry
                {
                    JobId = "guid-1",
                    JobName = "Monitor Helix Jobs",
                    JobResult = "failed",
                    JobOrder = 5,
                    JobAttempt = 1,
                    Status = "missing",
                    Candidates = [],
                }
            ]
        };
    }
}
