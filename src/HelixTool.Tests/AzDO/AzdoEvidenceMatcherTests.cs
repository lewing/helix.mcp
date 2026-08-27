// Group A — AzdoEvidenceMatcher pure unit tests (no HTTP, no mocks needed).
//
// Run: `dotnet test --filter "FullyQualifiedName~AzdoEvidenceMatcherTests"`

using System.Globalization;
using System.Text.Json;
using HelixTool.Core.AzDO;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoEvidenceMatcherTests
{
    // ════════════════════════════════════════════════════════════════════════
    // A1 — NormalizeKey parity with PR #132609 bash algorithm
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("linux-arm64 release CrossAOT_Mono crossaot", "linuxarm64releasecrossaotmonocrossaot")]
    [InlineData("linux-arm64 release CrossAOT_Mono", "linuxarm64releasecrossaotmono")]
    [InlineData("Monitor Helix Jobs", "monitorhelixjobs")]
    [InlineData("windows-x64 release", "windowsx64release")]
    [InlineData("linux-musl-arm64", "linuxmuslarm64")]
    [InlineData("  leading trailing  ", "leadingtrailing")]  // trailing space dropped
    [InlineData("", "")]
    public void NormalizeKey_MatchesPrAlgorithm(string input, string expected)
    {
        // ToLowerInvariant, then keep only [a-z0-9]; everything else dropped.
        var result = AzdoEvidenceMatcher.NormalizeKey(input);
        Assert.Equal(expected, result);
    }

    // ════════════════════════════════════════════════════════════════════════
    // A2 — NormalizeKey is ASCII-only (diverges from char.IsLetterOrDigit)
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("café", "caf")]        // 'é' (U+00E9) is dropped, not retained as in Unicode-aware IsLetterOrDigit
    [InlineData("naïve", "nave")]       // 'ï' (U+00EF) dropped
    [InlineData("Ωmega", "mega")]       // Greek Omega dropped
    [InlineData("日本語", "")]            // All non-ASCII — result is empty
    [InlineData("abc123", "abc123")]    // Pure ASCII alnum — unchanged
    [InlineData("abc_DEF-123", "abcdef123")]  // underscore and hyphen dropped, ASCII letters kept
    public void NormalizeKey_IsAsciiOnly_NonAsciiDropped(string input, string expected)
    {
        // D2: "ASCII-only, matching bash [:alnum:] in the C locale."
        // char.IsLetterOrDigit is Unicode-aware and would retain 'é', 'ï', etc.
        var result = AzdoEvidenceMatcher.NormalizeKey(input);
        Assert.Equal(expected, result);
    }

    // ════════════════════════════════════════════════════════════════════════
    // A3 — NormalizeKey is culture-invariant (tr-TR safety)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void NormalizeKey_IsCultureInvariant_TurkishI()
    {
        // In tr-TR locale, ToLower("I") -> "ı" (dotless i, U+0131) not "i".
        // ToLowerInvariant always returns "i" — required for portability.
        const string input = "ILIKE";

        string resultDefault, resultTurkish;

        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            resultDefault = AzdoEvidenceMatcher.NormalizeKey(input);

            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            resultTurkish = AzdoEvidenceMatcher.NormalizeKey(input);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }

        Assert.Equal("ilike", resultDefault);
        Assert.Equal("ilike", resultTurkish);  // must be identical — InvariantCulture
    }

    // ════════════════════════════════════════════════════════════════════════
    // A4 — Prefix stripping: Logs_Build_AttemptN_ is parsed and removed
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void StripArtifactPrefix_AttemptNumeric_StrippedAndParsed()
    {
        // "Logs_Build_Attempt12_Foo" -> Name = "Foo", Attempt = 12
        var (name, attempt) = AzdoEvidenceMatcher.StripArtifactPrefix(
            "Logs_Build_Attempt12_Foo",
            prefix: "Logs_Build_",
            stripAttempt: true);

        Assert.Equal("Foo", name);
        Assert.Equal(12, attempt);
    }

    [Fact]
    public void StripArtifactPrefix_Attempt1_StrippedAndParsed()
    {
        var (name, attempt) = AzdoEvidenceMatcher.StripArtifactPrefix(
            "Logs_Build_Attempt1_linux__arm64_release_CrossAOT_Mono",
            prefix: "Logs_Build_",
            stripAttempt: true);

        Assert.Equal("linux__arm64_release_CrossAOT_Mono", name);
        Assert.Equal(1, attempt);
    }

    // ════════════════════════════════════════════════════════════════════════
    // A5 — Strip disabled: Attempt1/Attempt2 names retained, no collision
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void StripArtifactPrefix_StripDisabled_AttemptRetained()
    {
        // stripAttempt=false -> "Attempt1_Foo" is the remaining name; attempt == null.
        var (name1, attempt1) = AzdoEvidenceMatcher.StripArtifactPrefix(
            "Logs_Build_Attempt1_Foo", prefix: "Logs_Build_", stripAttempt: false);

        var (name2, attempt2) = AzdoEvidenceMatcher.StripArtifactPrefix(
            "Logs_Build_Attempt2_Foo", prefix: "Logs_Build_", stripAttempt: false);

        Assert.Equal("Attempt1_Foo", name1);
        Assert.Equal("Attempt2_Foo", name2);
        Assert.Null(attempt1);
        Assert.Null(attempt2);
        Assert.NotEqual(name1, name2);  // no collision when stripping is off
    }

    // ════════════════════════════════════════════════════════════════════════
    // A6 — Non-greedy / malformed attempt tokens: prefix NOT stripped
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Logs_Build_Attempt_Foo")]    // "Attempt_" — no digits
    [InlineData("Logs_Build_AttemptX_Foo")]   // "AttemptX_" — letter not digit
    [InlineData("Logs_Build_Attempt")]         // dangling "Attempt" at end — no underscore after digits
    public void StripArtifactPrefix_MalformedAttempt_NotStripped(string artifactName)
    {
        // The attempt token requires at least one digit followed by '_'.
        // D2: "Prefix stripping ... is implemented as StartsWith + a manual digit scan — no Regex".
        var (name, attempt) = AzdoEvidenceMatcher.StripArtifactPrefix(
            artifactName, prefix: "Logs_Build_", stripAttempt: true);

        // Prefix is still stripped (if present), but no attempt number extracted.
        Assert.Null(attempt);
        // The remaining name should NOT start with a bare numeric digit (no partial strip).
        Assert.DoesNotMatch(@"^\d+_", name);
    }

    // ════════════════════════════════════════════════════════════════════════
    // A7 — source-id join wins even when normalized name would not match
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildPlan_SourceIdJoin_WinsOverNameMismatch()
    {
        // Job GUID "job-guid-1" has name "linux-arm64 release CrossAOT_Mono crossaot"
        // Artifact source = "job-guid-1", name = "Logs_Build_Attempt1_linux__arm64_release_CrossAOT_Mono"
        // Normalized job key ≠ normalized artifact key, BUT source-id matches => mapped.
        var jobId = "job-guid-1";
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = jobId, Type = "Job", Result = "failed", Name = "linux-arm64 release CrossAOT_Mono crossaot", Order = 1, Attempt = 1 }
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 10, Name = "Logs_Build_Attempt1_linux__arm64_release_CrossAOT_Mono",
                    Source = jobId,
                    Resource = new() { Type = "Container" } }
        };
        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed"],
            ArtifactJobPrefix = "Logs_Build_",
            StripAttemptPrefix = true,
            Match = "auto"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        Assert.Single(plan.Entries);
        var entry = plan.Entries[0];
        Assert.Equal("mapped", entry.Status);
        Assert.Equal("source-id", entry.MatchedBy);
        Assert.Single(entry.Candidates);
        Assert.True(plan.Complete);
    }

    // ════════════════════════════════════════════════════════════════════════
    // A8 — NativeAOT collision guard
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildPlan_NativeAOT_DoesNotMatchNativeAOT_Libraries()
    {
        // Both jobs failed. Their normalized keys differ:
        //   "nativeaot" ≠ "nativeaotlibraries"
        // Artifact "Logs_Build_Attempt1_NativeAOT" must not match the _Libraries job.
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-nativeaot",     Type = "Job", Result = "failed", Name = "NativeAOT",           Order = 1, Attempt = 1 },
            new() { Id = "job-nativeaot-lib", Type = "Job", Result = "failed", Name = "NativeAOT_Libraries", Order = 2, Attempt = 1 }
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "Logs_Build_Attempt1_NativeAOT",
                    Source = "job-nativeaot",  // correct join via source-id
                    Resource = new() { Type = "Container" } }
        };
        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed"],
            ArtifactJobPrefix = "Logs_Build_",
            StripAttemptPrefix = true,
            Match = "auto"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        Assert.Equal(2, plan.Entries.Count);

        var nativeAotEntry = plan.Entries.Single(e => e.JobId == "job-nativeaot");
        Assert.Equal("mapped", nativeAotEntry.Status);

        var libEntry = plan.Entries.Single(e => e.JobId == "job-nativeaot-lib");
        Assert.Equal("missing", libEntry.Status);
        Assert.Empty(libEntry.Candidates);
    }

    [Fact]
    public void NormalizeKey_NativeAOT_DoesNotEqualNativeAOT_Libraries()
    {
        Assert.NotEqual(
            AzdoEvidenceMatcher.NormalizeKey("NativeAOT"),
            AzdoEvidenceMatcher.NormalizeKey("NativeAOT_Libraries"));
    }

    // ════════════════════════════════════════════════════════════════════════
    // A9 — Ambiguity: two candidates → status == "ambiguous", no winner selected
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildPlan_TwoCandidates_AmbiguousNoneChosen()
    {
        // Two artifacts share the same source GUID (shouldn't happen, but test the logic path)
        // OR: under normalized-exact two artifacts have the same key.
        // Use normalized-exact mode where Attempt1 and Attempt2 collapse.
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-1", Type = "Job", Result = "failed", Name = "linux x64 release", Order = 1, Attempt = 1 }
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 11, Name = "Logs_Build_Attempt1_linux_x64_release",
                    Source = "other-guid",  // no source join
                    Resource = new() { Type = "Container" } },
            new() { Id = 12, Name = "Logs_Build_Attempt2_linux_x64_release",
                    Source = "other-guid2",
                    Resource = new() { Type = "Container" } },
        };
        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed"],
            ArtifactJobPrefix = "Logs_Build_",
            StripAttemptPrefix = true,
            Match = "normalized-exact"  // force name-only; both attempt artifacts collapse to same key
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        Assert.Single(plan.Entries);
        var entry = plan.Entries[0];
        Assert.Equal("ambiguous", entry.Status);
        Assert.Equal(2, entry.Candidates.Count);
        // No winner field — all candidates listed, none flagged as chosen (D4)
        Assert.False(plan.Complete);
        Assert.NotEmpty(plan.IncompleteReasons);
    }

    // ════════════════════════════════════════════════════════════════════════
    // A10 — Missing: zero candidates → status == "missing", entry present
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildPlan_NoCandidates_MissingEntryPresent()
    {
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-monitor", Type = "Job", Result = "failed", Name = "Monitor Helix Jobs", Order = 5, Attempt = 1 }
        };
        var artifacts = new List<AzdoBuildArtifact>();  // empty — no artifacts at all
        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed"],
            ArtifactJobPrefix = "Logs_Build_",
            StripAttemptPrefix = true,
            Match = "auto"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        Assert.Single(plan.Entries);
        var entry = plan.Entries[0];
        Assert.Equal("missing", entry.Status);
        Assert.Equal("job-monitor", entry.JobId);
        Assert.Empty(entry.Candidates);
        Assert.False(plan.Complete);
        Assert.NotEmpty(plan.IncompleteReasons);
    }

    // ════════════════════════════════════════════════════════════════════════
    // A11 — Ordering totality: shuffled input → byte-identical output
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildPlan_DeterministicOrdering_ShuffledInputProducesSameOutput()
    {
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-a", Type = "Job", Result = "failed", Name = "Alpha", Order = 3, Attempt = 1 },
            new() { Id = "job-b", Type = "Job", Result = "failed", Name = "Beta",  Order = 1, Attempt = 1 },
            new() { Id = "job-c", Type = "Job", Result = "failed", Name = "Gamma", Order = 2, Attempt = 1 },
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "Logs_Build_Alpha", Source = "job-a", Resource = new() { Type = "Container" } },
            new() { Id = 2, Name = "Logs_Build_Beta",  Source = "job-b", Resource = new() { Type = "Container" } },
            new() { Id = 3, Name = "Logs_Build_Gamma", Source = "job-c", Resource = new() { Type = "Container" } },
        };
        var opts = new AzdoEvidencePlanOptions { JobResults = ["failed"], ArtifactJobPrefix = "Logs_Build_", Match = "auto" };

        // Run 20 times with different insertion orders.
        string? referenceOrder = null;
        var rng = new Random(42);
        for (int i = 0; i < 20; i++)
        {
            var shuffledJobs = jobs.OrderBy(_ => rng.Next()).ToList();
            var shuffledArtifacts = artifacts.OrderBy(_ => rng.Next()).ToList();
            var plan = AzdoEvidenceMatcher.BuildPlan(shuffledJobs, shuffledArtifacts, opts);
            var order = string.Join(",", plan.Entries.Select(e => e.JobId));
            if (referenceOrder == null) referenceOrder = order;
            else Assert.Equal(referenceOrder, order);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // A12 — Caps: > MaxPlanEntries (200) or > MaxCandidatesPerEntry (10)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildPlan_OverMaxEntries_Truncated()
    {
        // D6: MaxPlanEntries = 200; overflow sets truncated = true, total = N, complete = false.
        var jobs = Enumerable.Range(1, 201)
            .Select(i => new AzdoTimelineRecord
            {
                Id = $"job-{i}", Type = "Job", Result = "failed", Name = $"Job {i}", Order = i, Attempt = 1
            })
            .ToList();

        var opts = new AzdoEvidencePlanOptions { JobResults = ["failed"], Match = "auto" };
        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, [], opts);

        Assert.True(plan.Truncated);
        Assert.Equal(201, plan.Total);
        Assert.True(plan.Entries.Count <= 200);
        Assert.False(plan.Complete);
        Assert.NotNull(plan.Note);
    }

    [Fact]
    public void BuildPlan_OverMaxCandidatesPerEntry_CandidatesTruncated()
    {
        var jobId = "job-1";
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = jobId, Type = "Job", Result = "failed", Name = "BigJob", Order = 1, Attempt = 1 }
        };
        // 12 artifacts with the same normalized name (force normalized-exact mode)
        // and no source join → all 12 become candidates
        var artifacts = Enumerable.Range(1, 12)
            .Select(i => new AzdoBuildArtifact
            {
                Id = i,
                Name = $"Logs_Build_Attempt{i}_BigJob",
                Source = $"other-guid-{i}",
                Resource = new() { Type = "Container" }
            })
            .ToList<AzdoBuildArtifact>();

        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed"],
            ArtifactJobPrefix = "Logs_Build_",
            StripAttemptPrefix = true,
            Match = "normalized-exact"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(10, entry.Candidates.Count);
        Assert.Equal(12, entry.CandidateTotal);
        Assert.True(entry.CandidatesTruncated);
        Assert.Equal(
            "Candidates truncated: showing first 10 of 12. Maximum is 10.",
            entry.CandidateNote);
        Assert.Equal(Enumerable.Range(3, 10).Reverse(), entry.Candidates.Select(candidate => candidate.ArtifactId));
        Assert.Equal(Enumerable.Range(0, 10), entry.Candidates.Select(candidate => candidate.Rank));
        Assert.Equal(
            "Job 'BigJob' (job-1): 12 ambiguous candidates — none selected.",
            Assert.Single(plan.IncompleteReasons));
        Assert.True(plan.Truncated);
        Assert.False(plan.Complete);
        Assert.Equal(
            "Candidate lists truncated for 1 of 1 returned entries. " +
            "Affected entries report candidateTotal, candidatesTruncated, and candidateNote.",
            plan.Note);

        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(entry));
        Assert.Equal(12, serialized.RootElement.GetProperty("candidateTotal").GetInt32());
        Assert.True(serialized.RootElement.GetProperty("candidatesTruncated").GetBoolean());
        Assert.Equal(10, serialized.RootElement.GetProperty("candidates").GetArrayLength());
    }

    // ════════════════════════════════════════════════════════════════════════
    // A13 — --match presets: each produces documented result
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildPlan_MatchAuto_UsesSourceIdThenFallback()
    {
        // auto: job-1 resolved via source-id; job-2 has no artifact with that source,
        // but falls back to normalized-exact and finds one.
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-1", Type = "Job", Result = "failed", Name = "Alpha", Order = 1, Attempt = 1 },
            new() { Id = "job-2", Type = "Job", Result = "failed", Name = "Beta",  Order = 2, Attempt = 1 },
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "Logs_Build_Alpha", Source = "job-1", Resource = new() { Type = "Container" } },
            new() { Id = 2, Name = "Logs_Build_Beta",  Source = "no-match-guid", Resource = new() { Type = "Container" } },
        };
        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed"], ArtifactJobPrefix = "Logs_Build_", StripAttemptPrefix = true, Match = "auto"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        var entry1 = plan.Entries.Single(e => e.JobId == "job-1");
        Assert.Equal("mapped", entry1.Status);
        Assert.Equal("source-id", entry1.MatchedBy);

        var entry2 = plan.Entries.Single(e => e.JobId == "job-2");
        Assert.Equal("mapped", entry2.Status);
        Assert.Equal("normalized-name", entry2.MatchedBy);

        Assert.True(plan.Complete);
    }

    [Fact]
    public void BuildPlan_MatchSourceIdOnly_LeavesUnmappedAsMissing()
    {
        // source-id: job-2 has no artifact with its GUID → missing (no name fallback).
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-1", Type = "Job", Result = "failed", Name = "Alpha", Order = 1, Attempt = 1 },
            new() { Id = "job-2", Type = "Job", Result = "failed", Name = "Beta",  Order = 2, Attempt = 1 },
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "Logs_Build_Alpha", Source = "job-1", Resource = new() { Type = "Container" } },
            new() { Id = 2, Name = "Logs_Build_Beta",  Source = "no-match-guid", Resource = new() { Type = "Container" } },
        };
        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed"], ArtifactJobPrefix = "Logs_Build_", StripAttemptPrefix = true, Match = "source-id"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        Assert.Equal("mapped",  plan.Entries.Single(e => e.JobId == "job-1").Status);
        Assert.Equal("missing", plan.Entries.Single(e => e.JobId == "job-2").Status);
        Assert.False(plan.Complete);
    }

    [Fact]
    public void BuildPlan_MatchNormalizedExact_IgnoresSourceId()
    {
        // normalized-exact: resolves by name regardless of source GUID.
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-1", Type = "Job", Result = "failed", Name = "Alpha", Order = 1, Attempt = 1 }
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "Logs_Build_Alpha", Source = "unrelated-guid", Resource = new() { Type = "Container" } }
        };
        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed"], ArtifactJobPrefix = "Logs_Build_", StripAttemptPrefix = true, Match = "normalized-exact"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        Assert.Single(plan.Entries);
        Assert.Equal("mapped", plan.Entries[0].Status);
        Assert.Equal("normalized-name", plan.Entries[0].MatchedBy);
        Assert.True(plan.Complete);
    }

    [Fact]
    public void BuildPlan_MatchExact_OrdinalIgnoreCase_NoNormalization()
    {
        // exact: equality after prefix strip using StringComparison.OrdinalIgnoreCase.
        // Case differs only ("alpha" vs "Alpha") → still a match; no normalization is applied.
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-1", Type = "Job", Result = "failed", Name = "alpha", Order = 1, Attempt = 1 }
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "Logs_Build_Alpha", Source = "unrelated-guid", Resource = new() { Type = "Container" } }
        };
        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed"], ArtifactJobPrefix = "Logs_Build_", StripAttemptPrefix = true, Match = "exact"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        Assert.Equal("mapped", plan.Entries[0].Status);
        Assert.Equal("exact", plan.Entries[0].MatchedBy);
        Assert.Equal(1, plan.Entries[0].Candidates[0].ArtifactId);
    }

    [Theory]
    // Only case differs → OrdinalIgnoreCase matches.
    [InlineData("linux-x64 Release", "Logs_Build_LINUX-X64 RELEASE", "mapped")]
    [InlineData("LINUX-X64 RELEASE", "Logs_Build_linux-x64 release", "mapped")]
    // Separators/punctuation differ → still a miss, because exact does NOT normalize.
    [InlineData("linux-x64 release", "Logs_Build_linux_x64_release", "missing")]
    [InlineData("linux-x64 release", "Logs_Build_linuxx64release", "missing")]
    public void BuildPlan_MatchExact_IsCaseInsensitiveButNotNormalizing(
        string jobName,
        string artifactName,
        string expectedStatus)
    {
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-1", Type = "Job", Result = "failed", Name = jobName, Order = 1, Attempt = 1 }
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = artifactName, Source = "unrelated-guid", Resource = new() { Type = "Container" } }
        };
        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed"], ArtifactJobPrefix = "Logs_Build_", StripAttemptPrefix = true, Match = "exact"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        Assert.Equal(expectedStatus, plan.Entries[0].Status);
    }

    [Fact]
    public void BuildPlan_MatchExact_UsesOrdinalIgnoreCase_NotCultureIgnoreCase()
    {
        // Ordinal-ignore-case must not apply Turkish casing: "ILIKE" and "ilike" match in every
        // culture, and the dotless "ı" never folds to ASCII "i".
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-ascii",   Type = "Job", Result = "failed", Name = "ILIKE", Order = 1, Attempt = 1 },
            new() { Id = "job-dotless", Type = "Job", Result = "failed", Name = "ıLIKE", Order = 2, Attempt = 1 },
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "ilike", Source = "unrelated-guid", Resource = new() { Type = "Container" } }
        };
        var opts = new AzdoEvidencePlanOptions { JobResults = ["failed"], Match = "exact" };

        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

            Assert.Equal("mapped", plan.Entries.Single(e => e.JobId == "job-ascii").Status);
            Assert.Equal("missing", plan.Entries.Single(e => e.JobId == "job-dotless").Status);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // A14 — Strategy inputs are canonicalized case-insensitively
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("Auto")]
    [InlineData("aUtO")]
    public void BuildPlan_AutoStrategy_IsCaseInsensitive_NoAllMissingRegression(string match)
    {
        // A mixed-case strategy name must behave identically to its canonical spelling.
        // The regression this guards: an unrecognized casing silently falls through every
        // strategy branch and reports every entry as "missing".
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-1", Type = "Job", Result = "failed", Name = "Alpha", Order = 1, Attempt = 1 },
            new() { Id = "job-2", Type = "Job", Result = "failed", Name = "Beta",  Order = 2, Attempt = 1 },
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "Logs_Build_Alpha", Source = "job-1",         Resource = new() { Type = "Container" } },
            new() { Id = 2, Name = "Logs_Build_Beta",  Source = "no-match-guid", Resource = new() { Type = "Container" } },
        };
        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed"], ArtifactJobPrefix = "Logs_Build_", StripAttemptPrefix = true, Match = match
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        // Non-vacuous: both entries resolve, and each uses the branch canonical "auto" would use.
        Assert.Equal(2, plan.Entries.Count);
        Assert.DoesNotContain(plan.Entries, e => e.Status == "missing");
        Assert.Equal("source-id",       plan.Entries.Single(e => e.JobId == "job-1").MatchedBy);
        Assert.Equal("normalized-name", plan.Entries.Single(e => e.JobId == "job-2").MatchedBy);
        Assert.True(plan.Complete);
        Assert.Empty(plan.IncompleteReasons);
    }

    [Theory]
    [InlineData("source-id",        "SOURCE-ID")]
    [InlineData("source-id",        "Source-Id")]
    [InlineData("normalized-exact", "NORMALIZED-EXACT")]
    [InlineData("normalized-exact", "Normalized-Exact")]
    [InlineData("exact",            "EXACT")]
    [InlineData("exact",            "Exact")]
    [InlineData("auto",             "AUTO")]
    public void BuildPlan_StrategyCasing_ProducesIdenticalPlanToCanonical(string canonical, string variant)
    {
        // Every strategy — not just auto — must canonicalize case-insensitively and produce
        // byte-identical entry status/matchedBy/candidate output.
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-1", Type = "Job", Result = "failed", Name = "Alpha", Order = 1, Attempt = 1 },
            new() { Id = "job-2", Type = "Job", Result = "failed", Name = "Beta",  Order = 2, Attempt = 1 },
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "Logs_Build_Alpha", Source = "job-1",         Resource = new() { Type = "Container" } },
            new() { Id = 2, Name = "Logs_Build_Beta",  Source = "no-match-guid", Resource = new() { Type = "Container" } },
        };

        AzdoBuiltPlanResult Build(string match) => AzdoEvidenceMatcher.BuildPlan(
            jobs,
            artifacts,
            new AzdoEvidencePlanOptions
            {
                JobResults = ["failed"],
                ArtifactJobPrefix = "Logs_Build_",
                StripAttemptPrefix = true,
                Match = match
            });

        var expected = Build(canonical);
        var actual = Build(variant);

        Assert.Equal(
            JsonSerializer.Serialize(expected.Entries),
            JsonSerializer.Serialize(actual.Entries));
        Assert.Equal(expected.Complete, actual.Complete);
        Assert.Equal(expected.IncompleteReasons, actual.IncompleteReasons);

        // Guard against a vacuous comparison of two all-missing plans: the canonical run must
        // resolve at least one entry for every strategy exercised here.
        Assert.Contains(expected.Entries, e => e.Status == "mapped");
    }

    // ════════════════════════════════════════════════════════════════════════
    // A15 — Empty normalized keys never false-map
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("日本語")]                 // all non-ASCII
    [InlineData("Ωμέγα")]                  // all non-ASCII (Greek)
    [InlineData("---___...")]              // all punctuation
    [InlineData("   ")]                    // all whitespace
    [InlineData("\r\n\t\u0000\u0007")]     // all control characters
    [InlineData("")]                       // empty
    public void NormalizeKey_NonAlnumOnly_ProducesEmptyKey(string input)
    {
        Assert.Equal("", AzdoEvidenceMatcher.NormalizeKey(input));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("normalized-exact")]
    public void BuildPlan_EmptyNormalizedKeys_NeverFalseMap(string match)
    {
        // A job whose name normalizes to "" must not collide with an artifact whose name
        // also normalizes to "" — an empty key carries no identity and must never match.
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-nonascii",  Type = "Job", Result = "failed", Name = "日本語",              Order = 1, Attempt = 1 },
            new() { Id = "job-punct",     Type = "Job", Result = "failed", Name = "---___...",          Order = 2, Attempt = 1 },
            new() { Id = "job-control",   Type = "Job", Result = "failed", Name = "\r\n\t\u0000\u0007", Order = 3, Attempt = 1 },
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            // Every artifact name also normalizes to the empty key.
            new() { Id = 1, Name = "Logs_Build_Ωμέγα",      Source = "unrelated-1", Resource = new() { Type = "Container" } },
            new() { Id = 2, Name = "Logs_Build_...___---",  Source = "unrelated-2", Resource = new() { Type = "Container" } },
            new() { Id = 3, Name = "Logs_Build_\u0007\r\n", Source = "unrelated-3", Resource = new() { Type = "Container" } },
        };
        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed"], ArtifactJobPrefix = "Logs_Build_", StripAttemptPrefix = true, Match = match
        };

        // Pre-condition: the fixture really does produce empty keys on both sides.
        Assert.All(jobs, j => Assert.Equal("", AzdoEvidenceMatcher.NormalizeKey(j.Name!)));
        Assert.All(
            artifacts,
            a => Assert.Equal(
                "",
                AzdoEvidenceMatcher.NormalizeKey(
                    AzdoEvidenceMatcher.StripArtifactPrefix(a.Name!, "Logs_Build_", true).name)));

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        Assert.Equal(3, plan.Entries.Count);
        Assert.All(plan.Entries, e =>
        {
            Assert.Equal("missing", e.Status);
            Assert.Null(e.MatchedBy);
            Assert.Empty(e.Candidates);
            Assert.Equal(0, e.CandidateTotal);
        });
        Assert.False(plan.Complete);
    }

    [Fact]
    public void BuildPlan_EmptyNormalizedKey_StillMapsViaSourceId()
    {
        // The empty-key guard must be scoped to name matching only. A job with an
        // unnormalizable name still maps when the artifact carries its source GUID.
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-nonascii", Type = "Job", Result = "failed", Name = "日本語",     Order = 1, Attempt = 1 },
            new() { Id = "job-punct",    Type = "Job", Result = "failed", Name = "---___...", Order = 2, Attempt = 1 },
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            // Maps by GUID despite an equally unnormalizable artifact name.
            new() { Id = 1, Name = "Logs_Build_Ωμέγα",     Source = "job-nonascii", Resource = new() { Type = "Container" } },
            new() { Id = 2, Name = "Logs_Build_...___---", Source = "unrelated",    Resource = new() { Type = "Container" } },
        };

        foreach (var match in new[] { "auto", "source-id" })
        {
            var plan = AzdoEvidenceMatcher.BuildPlan(
                jobs,
                artifacts,
                new AzdoEvidencePlanOptions
                {
                    JobResults = ["failed"],
                    ArtifactJobPrefix = "Logs_Build_",
                    StripAttemptPrefix = true,
                    Match = match
                });

            var mapped = plan.Entries.Single(e => e.JobId == "job-nonascii");
            Assert.Equal("mapped", mapped.Status);
            Assert.Equal("source-id", mapped.MatchedBy);
            Assert.Equal(1, mapped.Candidates[0].ArtifactId);

            // The other empty-key job has no source-id join and must not fall back onto
            // the empty-key artifact.
            var unmapped = plan.Entries.Single(e => e.JobId == "job-punct");
            Assert.Equal("missing", unmapped.Status);
            Assert.Empty(unmapped.Candidates);
        }
    }

    [Fact]
    public void BuildPlan_EmptyNormalizedKey_DoesNotSuppressValidNormalizedMatches()
    {
        // Guard against over-correcting: suppressing empty keys must not disturb a
        // normally-normalizing job in the same plan.
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-empty", Type = "Job", Result = "failed", Name = "日本語",           Order = 1, Attempt = 1 },
            new() { Id = "job-real",  Type = "Job", Result = "failed", Name = "linux-x64 release", Order = 2, Attempt = 1 },
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "Logs_Build_日本語",              Source = "unrelated-1", Resource = new() { Type = "Container" } },
            new() { Id = 2, Name = "Logs_Build_linux_x64_release",  Source = "unrelated-2", Resource = new() { Type = "Container" } },
        };
        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed"], ArtifactJobPrefix = "Logs_Build_", StripAttemptPrefix = true, Match = "normalized-exact"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        Assert.Equal("missing", plan.Entries.Single(e => e.JobId == "job-empty").Status);

        var real = plan.Entries.Single(e => e.JobId == "job-real");
        Assert.Equal("mapped", real.Status);
        Assert.Equal("normalized-name", real.MatchedBy);
        Assert.Equal(2, real.Candidates[0].ArtifactId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildPlan_MatchExact_EmptyJobName_NeverMapsToEmptyBareArtifact(string? jobName)
    {
        // exact compares the *bare* artifact name, so it has its own empty-key hazard that
        // normalization-based suppression does not cover: a job with no usable name collapses
        // to "", and prefix/attempt stripping can collapse an artifact name to "" as well.
        // Two empty strings are ordinal-ignore-case equal, so nothing but an explicit guard
        // stops them joining. An empty key carries no identity and must never map.
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-empty", Type = "Job", Result = "failed", Name = jobName,  Order = 1, Attempt = 1 },
            new() { Id = "job-real",  Type = "Job", Result = "failed", Name = "Alpha",  Order = 2, Attempt = 1 },
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            // Bare name becomes "" through job-prefix stripping alone.
            new() { Id = 1, Name = "Logs_Build_",          Source = "unrelated-1", Resource = new() { Type = "Container" } },
            // Bare name becomes "" through job-prefix + AttemptN_ stripping.
            new() { Id = 2, Name = "Logs_Build_Attempt2_", Source = "unrelated-2", Resource = new() { Type = "Container" } },
            // Control: a normal artifact that exact must still resolve.
            new() { Id = 3, Name = "Logs_Build_Alpha",     Source = "unrelated-3", Resource = new() { Type = "Container" } },
        };
        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed"], ArtifactJobPrefix = "Logs_Build_", StripAttemptPrefix = true, Match = "exact"
        };

        // Pre-condition: the fixture really does produce empty bare names on both sides, and
        // the attempt artifact is stripped via the attempt path (not merely the prefix path).
        var strippedPrefixOnly = AzdoEvidenceMatcher.StripArtifactPrefix("Logs_Build_", "Logs_Build_", true);
        Assert.Equal("", strippedPrefixOnly.name);
        Assert.Null(strippedPrefixOnly.attempt);

        var strippedAttempt = AzdoEvidenceMatcher.StripArtifactPrefix("Logs_Build_Attempt2_", "Logs_Build_", true);
        Assert.Equal("", strippedAttempt.name);
        Assert.Equal(2, strippedAttempt.attempt);

        Assert.Equal("", jobName ?? "");

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        var empty = plan.Entries.Single(e => e.JobId == "job-empty");
        Assert.Equal("missing", empty.Status);
        Assert.Null(empty.MatchedBy);
        Assert.Empty(empty.Candidates);
        Assert.Equal(0, empty.CandidateTotal);

        // Non-vacuous: suppressing the empty key must not disable exact matching for the
        // normally-named job sharing the plan.
        var real = plan.Entries.Single(e => e.JobId == "job-real");
        Assert.Equal("mapped", real.Status);
        Assert.Equal("exact", real.MatchedBy);
        Assert.Equal(3, real.Candidates[0].ArtifactId);

        Assert.False(plan.Complete);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildPlan_MatchExact_EmptyJobName_StaysMissingRegardlessOfSourceId(string? jobName)
    {
        // The exact empty-key guard must be source-ID independent in both directions:
        // exact never consults artifact.source, so carrying the job GUID neither rescues the
        // empty-name job under exact nor is required for it to be suppressed. The same fixture
        // under auto/source-id still maps via the GUID, proving the miss is specific to exact
        // rather than a broken fixture.
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-empty", Type = "Job", Result = "failed", Name = jobName, Order = 1, Attempt = 1 },
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            // Bare name collapses to "" AND carries the job's source GUID.
            new() { Id = 1, Name = "Logs_Build_Attempt3_", Source = "job-empty", Resource = new() { Type = "Container" } },
        };

        AzdoBuiltPlanResult Build(string match) => AzdoEvidenceMatcher.BuildPlan(
            jobs,
            artifacts,
            new AzdoEvidencePlanOptions
            {
                JobResults = ["failed"],
                ArtifactJobPrefix = "Logs_Build_",
                StripAttemptPrefix = true,
                Match = match
            });

        var exact = Build("exact").Entries.Single();
        Assert.Equal("missing", exact.Status);
        Assert.Null(exact.MatchedBy);
        Assert.Empty(exact.Candidates);
        Assert.Equal(0, exact.CandidateTotal);

        // Control: the GUID join itself is sound, so the exact miss is a strategy property.
        foreach (var match in new[] { "auto", "source-id" })
        {
            var mapped = Build(match).Entries.Single();
            Assert.Equal("mapped", mapped.Status);
            Assert.Equal("source-id", mapped.MatchedBy);
            Assert.Equal(1, mapped.Candidates[0].ArtifactId);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Additional edge cases
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildPlan_JobResultCanceled_Included()
    {
        // D3: default job-results includes "canceled" jobs.
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-canceled", Type = "Job", Result = "canceled", Name = "Canceled Job", Order = 1, Attempt = 1 }
        };
        var opts = new AzdoEvidencePlanOptions { JobResults = ["failed", "canceled"], Match = "auto" };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, [], opts);

        Assert.Single(plan.Entries);
        Assert.Equal("job-canceled", plan.Entries[0].JobId);
        Assert.Equal("canceled", plan.Entries[0].JobResult);
    }

    [Fact]
    public void BuildPlan_SucceededJobNotSelected_ByDefault()
    {
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-passed", Type = "Job", Result = "succeeded", Name = "PassedJob", Order = 1, Attempt = 1 }
        };
        var opts = new AzdoEvidencePlanOptions { JobResults = ["failed", "canceled"], Match = "auto" };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, [], opts);

        Assert.Empty(plan.Entries);
        Assert.True(plan.Complete);  // vacuously complete — no entries to be ambiguous/missing
    }

    [Fact]
    public void BuildPlan_NonJobTypeRecords_NotSelected()
    {
        // Only "Job" type records are selected (D3 / PR #132609 parity).
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "stage-1", Type = "Stage",   Result = "failed", Name = "Build", Order = 1 },
            new() { Id = "phase-1", Type = "Phase",   Result = "failed", Name = "CI",    Order = 2 },
            new() { Id = "task-1",  Type = "Task",    Result = "failed", Name = "MSBuild", Order = 3 },
            new() { Id = "job-1",   Type = "Job",     Result = "failed", Name = "Actual Job", Order = 4, Attempt = 1 },
        };
        var opts = new AzdoEvidencePlanOptions { JobResults = ["failed"], Match = "auto" };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, [], opts);

        Assert.Single(plan.Entries);
        Assert.Equal("job-1", plan.Entries[0].JobId);
    }

    [Fact]
    public void BuildPlan_NullResultRecord_NotSelected()
    {
        // Records with result == null (still running) must never be selected (D3).
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-running", Type = "Job", Result = null, Name = "In Progress", Order = 1 }
        };
        var opts = new AzdoEvidencePlanOptions { JobResults = ["failed", "canceled"], Match = "auto" };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, [], opts);

        Assert.Empty(plan.Entries);
    }

    [Fact]
    public void BuildPlan_ControlAndNewlineCharsInJobName_NormalizedSafely()
    {
        // Control/newline characters in job names should not crash normalization;
        // they are dropped by the ASCII-alnum filter.
        var jobId = "job-ctrl";
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = jobId, Type = "Job", Result = "failed", Name = "Job\r\nWith\0Control", Order = 1, Attempt = 1 }
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "Logs_Build_JobWithControl", Source = jobId, Resource = new() { Type = "Container" } }
        };
        var opts = new AzdoEvidencePlanOptions { JobResults = ["failed"], ArtifactJobPrefix = "Logs_Build_", Match = "auto" };

        // Should not throw; source-id will resolve it.
        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);
        Assert.Equal("mapped", plan.Entries[0].Status);
    }

    [Fact]
    public void NormalizeKey_ControlAndNewlineChars_Dropped()
    {
        Assert.Equal("jobwithcontrol", AzdoEvidenceMatcher.NormalizeKey("Job\r\nWith\0Control"));
        Assert.Equal("jobwithtab", AzdoEvidenceMatcher.NormalizeKey("Job\tWith\tTab"));
    }

    [Fact]
    public void BuildPlan_IncompletePlanCarriesPerJobReasons()
    {
        // Each missing/ambiguous job contributes a line to incompleteReasons (D4).
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "j1", Type = "Job", Result = "failed", Name = "Alpha", Order = 1, Attempt = 1 },
            new() { Id = "j2", Type = "Job", Result = "failed", Name = "Beta",  Order = 2, Attempt = 1 },
        };
        var opts = new AzdoEvidencePlanOptions { JobResults = ["failed"], Match = "auto" };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, [], opts);

        Assert.False(plan.Complete);
        Assert.Equal(2, plan.IncompleteReasons.Count);
    }

    [Fact]
    public void BuildPlan_ArtifactSizeBytes_SurfacedFromProperties()
    {
        // D5: sizeBytes comes from resource.properties["artifactsize"]; no extra HTTP call.
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "job-1", Type = "Job", Result = "failed", Name = "Alpha", Order = 1, Attempt = 1 }
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "Logs_Build_Alpha", Source = "job-1",
                    Resource = new AzdoArtifactResource
                    {
                        Type = "Container",
                        Properties = new Dictionary<string, string> { ["artifactsize"] = "102400" }
                    } }
        };
        var opts = new AzdoEvidencePlanOptions { JobResults = ["failed"], ArtifactJobPrefix = "Logs_Build_", Match = "auto" };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);

        Assert.Equal(102400L, plan.Entries[0].Candidates[0].SizeBytes);
    }

    [Fact]
    public void BuildPlan_CandidatesOrderedByAttemptDescThenName()
    {
        // D6: candidates ordered by (Attempt desc, ArtifactName ordinal, ArtifactId asc).
        // Rank 0 is presentation only — not a selection.
        var jobs = new List<AzdoTimelineRecord>
        {
            new() { Id = "j1", Type = "Job", Result = "failed", Name = "Foo", Order = 1, Attempt = 2 }
        };
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "Logs_Build_Attempt1_Foo", Source = "other-1",
                    Resource = new() { Type = "Container" } },
            new() { Id = 2, Name = "Logs_Build_Attempt2_Foo", Source = "other-2",
                    Resource = new() { Type = "Container" } },
        };
        var opts = new AzdoEvidencePlanOptions
        {
            JobResults = ["failed"], ArtifactJobPrefix = "Logs_Build_", StripAttemptPrefix = true, Match = "normalized-exact"
        };

        var plan = AzdoEvidenceMatcher.BuildPlan(jobs, artifacts, opts);
        var candidates = plan.Entries[0].Candidates;

        // Attempt 2 comes first (desc).
        Assert.Equal(2, candidates[0].Attempt);
        Assert.Equal(1, candidates[1].Attempt);
    }
}
