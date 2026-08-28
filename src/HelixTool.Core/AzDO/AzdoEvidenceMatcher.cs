namespace HelixTool.Core.AzDO;

/// <summary>
/// Pure, stateless matching engine for <see cref="AzdoEvidencePlan"/> construction.
/// No I/O, no regex, no side effects — safe for unit testing with no HTTP.
/// Mirrors the PR #132609 algorithm (§2.1 of the design) while fixing its known defects
/// (12.7 % miss rate, 100 % ambiguity on retried builds).
/// </summary>
public static class AzdoEvidenceMatcher
{
    /// <summary>Max plan entries; mirrors <c>MaxTimelineRecords = 200</c>.</summary>
    public const int MaxPlanEntries = 200;

    /// <summary>Max candidates retained per entry.</summary>
    public const int MaxCandidatesPerEntry = 10;

    /// <summary>
    /// Normalise a name to the PR #132609 key format:
    /// lower-case via <see cref="string.ToLowerInvariant"/>, then keep only ASCII
    /// alphanumeric characters (<c>[a-z0-9]</c>).
    /// ASCII-only matches bash <c>[:alnum:]</c> in the C locale;
    /// <see cref="char.IsLetterOrDigit"/> would diverge on non-ASCII input.
    /// Culture-insensitive: uses <c>ToLowerInvariant</c>, never <c>ToLower()</c>.
    /// </summary>
    public static string NormalizeKey(string s)
    {
        if (s.Length == 0) return s;
        var lower = s.ToLowerInvariant();
        var buf = new char[lower.Length];
        var len = 0;
        foreach (var c in lower)
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                buf[len++] = c;
        }
        return new string(buf, 0, len);
    }

    /// <summary>
    /// Strip <paramref name="prefix"/> from the start of <paramref name="artifactName"/>,
    /// then optionally strip the <c>AttemptN_</c> segment.
    /// Returns <c>(name, attempt)</c> where <c>name</c> is the remaining base name and
    /// <c>attempt</c> is the parsed attempt number (<c>null</c> when not stripped).
    /// All comparisons are <see cref="StringComparison.Ordinal"/>. No regex.
    /// </summary>
    public static (string name, int? attempt) StripArtifactPrefix(
        string artifactName,
        string? prefix,
        bool stripAttempt)
    {
        int? attempt = null;
        var remaining = artifactName;

        // Strip explicit job prefix (e.g. "Logs_Build_")
        if (prefix is { Length: > 0 } &&
            remaining.StartsWith(prefix, StringComparison.Ordinal))
        {
            remaining = remaining[prefix.Length..];
        }

        // Strip "AttemptN_" where N is one or more decimal digits
        if (stripAttempt &&
            remaining.StartsWith("Attempt", StringComparison.Ordinal))
        {
            int pos = "Attempt".Length;
            int digitStart = pos;
            while (pos < remaining.Length && remaining[pos] >= '0' && remaining[pos] <= '9')
                pos++;

            // Valid only when at least one digit was found, followed by '_'
            if (pos > digitStart && pos < remaining.Length && remaining[pos] == '_')
            {
                var digitSpan = remaining.AsSpan(digitStart, pos - digitStart);
                if (int.TryParse(digitSpan, out var n))
                {
                    attempt = n;
                    remaining = remaining[(pos + 1)..]; // skip '_'
                }
            }
        }

        return (remaining, attempt);
    }

    /// <summary>
    /// Build the evidence plan from all timeline records and artifacts.
    /// Filters by <c>type == "Job"</c> and <c>result in options.JobResults</c> internally.
    /// </summary>
    /// <param name="allRecords">All timeline records for the build.</param>
    /// <param name="artifacts">All artifacts from the build, pre-filtered by caller if desired.</param>
    /// <param name="options">Options driving job selection, prefix stripping and matching strategy.</param>
    public static AzdoBuiltPlanResult BuildPlan(
        IEnumerable<AzdoTimelineRecord> allRecords,
        IEnumerable<AzdoBuildArtifact> artifacts,
        AzdoEvidencePlanOptions options)
    {
        var strategy = AzdoEvidenceMatchStrategy.Canonicalize(options.Match);
        var jobs = SelectAndSortJobs(allRecords, options.JobResults);
        var artifactList = artifacts is IReadOnlyList<AzdoBuildArtifact> l ? l : artifacts.ToList();

        // Pre-process artifacts: strip prefixes + compute match keys once
        var processedArtifacts = new List<ProcessedArtifact>(artifactList.Count);
        foreach (var a in artifactList)
        {
            var name = a.Name ?? "";
            var (bare, attemptNum) = StripArtifactPrefix(
                name,
                options.ArtifactJobPrefix,
                options.StripAttemptPrefix);
            var normalizedKey = NormalizeKey(bare);

            processedArtifacts.Add(new ProcessedArtifact(
                Artifact: a,
                Bare: bare,
                NormalizedKey: normalizedKey,
                Attempt: attemptNum));
        }

        // Index by source GUID for source-id join
        var bySourceId = new Dictionary<string, List<ProcessedArtifact>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pa in processedArtifacts)
        {
            if (pa.Artifact.Source is { Length: > 0 } src)
            {
                if (!bySourceId.TryGetValue(src, out var list))
                    bySourceId[src] = list = [];
                list.Add(pa);
            }
        }

        var entries = new List<AzdoEvidencePlanEntry>(Math.Min(jobs.Count, MaxPlanEntries));
        var totalJobs = jobs.Count;
        var entriesTruncated = false;
        var truncatedCandidateLists = 0;

        foreach (var record in jobs)
        {
            if (entries.Count >= MaxPlanEntries)
            {
                entriesTruncated = true;
                break;
            }

            var recordId = record.Id ?? "";
            var jobName = record.Name ?? "";

            List<ProcessedArtifact>? rawCandidates = null;
            string? matchedBy = null;

            if (strategy is AzdoEvidenceMatchStrategy.Auto or AzdoEvidenceMatchStrategy.SourceId)
            {
                if (recordId.Length > 0 && bySourceId.TryGetValue(recordId, out var byId) && byId.Count > 0)
                {
                    rawCandidates = byId;
                    matchedBy = "source-id";
                }

                if (rawCandidates is null or { Count: 0 } &&
                    strategy == AzdoEvidenceMatchStrategy.Auto)
                {
                    var nameMatches = FindNormalizedMatches(processedArtifacts, jobName);
                    if (nameMatches.Count > 0)
                    {
                        rawCandidates = nameMatches;
                        matchedBy = "normalized-name";
                    }
                }
            }
            else if (strategy == AzdoEvidenceMatchStrategy.NormalizedExact)
            {
                var nameMatches = FindNormalizedMatches(processedArtifacts, jobName);
                if (nameMatches.Count > 0)
                {
                    rawCandidates = nameMatches;
                    matchedBy = "normalized-name";
                }
            }
            else if (strategy == AzdoEvidenceMatchStrategy.Exact)
            {
                if (jobName.Length > 0)
                {
                    var exactMatches = processedArtifacts
                        .Where(pa =>
                            pa.Bare.Length > 0 &&
                            string.Equals(pa.Bare, jobName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (exactMatches.Count > 0)
                    {
                        rawCandidates = exactMatches;
                        matchedBy = "exact";
                    }
                }
            }

            var count = rawCandidates?.Count ?? 0;
            var candidatesTruncated = count > MaxCandidatesPerEntry;
            if (candidatesTruncated)
                truncatedCandidateLists++;

            var status = count switch
            {
                0 => "missing",
                1 => "mapped",
                _ => "ambiguous"
            };

            // Build candidates, ranked by (Attempt desc, ArtifactName ordinal, ArtifactId asc)
            var candidateList = new List<AzdoEvidenceCandidate>();
            if (rawCandidates is { Count: > 0 })
            {
                var sorted = rawCandidates
                    .OrderByDescending(pa => pa.Attempt ?? 0)
                    .ThenBy(pa => pa.Artifact.Name ?? "", StringComparer.Ordinal)
                    .ThenBy(pa => pa.Artifact.Id)
                    .Take(MaxCandidatesPerEntry);

                var rank = 0;
                foreach (var pa in sorted)
                {
                    long? sizeBytes = null;
                    if (pa.Artifact.Resource?.Properties is { } props &&
                        props.TryGetValue("artifactsize", out var sizeStr) &&
                        long.TryParse(sizeStr, out var sz))
                    {
                        sizeBytes = sz;
                    }

                    candidateList.Add(new AzdoEvidenceCandidate
                    {
                        Rank = rank++,
                        ArtifactId = pa.Artifact.Id,
                        ArtifactName = pa.Artifact.Name ?? "",
                        Source = pa.Artifact.Source,
                        Attempt = pa.Attempt,
                        ResourceType = pa.Artifact.Resource?.Type,
                        DownloadUrl = pa.Artifact.Resource?.DownloadUrl,
                        SizeBytes = sizeBytes
                    });
                }
            }

            entries.Add(new AzdoEvidencePlanEntry
            {
                JobId = recordId,
                JobName = jobName,
                JobResult = record.Result,
                JobOrder = record.Order,
                JobAttempt = record.Attempt,
                MatchedBy = matchedBy,
                Status = status,
                Candidates = candidateList,
                CandidateTotal = count,
                CandidatesTruncated = candidatesTruncated,
                CandidateNote = candidatesTruncated
                    ? $"Candidates truncated: showing first {candidateList.Count} of {count}. Maximum is {MaxCandidatesPerEntry}."
                    : null
            });
        }

        // Assemble completeness diagnostics
        var incompleteReasons = new List<string>();
        var warnings = new List<string>();
        var warningSet = new HashSet<string>(StringComparer.Ordinal);

        void AddWarning(string warning)
        {
            if (warningSet.Add(warning))
                warnings.Add(warning);
        }

        var notes = new List<string>(2);
        if (entriesTruncated)
        {
            incompleteReasons.Add(
                $"Plan is incomplete because {totalJobs - entries.Count} of {totalJobs} selected jobs are not represented.");

            var msg =
                $"Plan entries truncated: showing first {entries.Count} of {totalJobs} selected jobs. " +
                $"Maximum is {MaxPlanEntries}.";
            AddWarning(msg);
            notes.Add(msg);
        }

        foreach (var e in entries)
        {
            if (e.Status == "missing")
                incompleteReasons.Add($"Job '{e.JobName}' ({e.JobId}): no matching artifact found.");
            else if (e.Status == "ambiguous")
                incompleteReasons.Add($"Job '{e.JobName}' ({e.JobId}): {e.CandidateTotal} ambiguous candidates — none selected.");

            if (e.CandidatesTruncated)
            {
                AddWarning(
                    $"Candidate lists truncated for job '{e.JobName}' ({e.JobId}): " +
                    $"showing first {e.Candidates.Count} of {e.CandidateTotal}; " +
                    "candidateTotal preserves the full count.");
            }
        }

        if (truncatedCandidateLists > 0)
        {
            var msg =
                $"Candidate lists truncated for {truncatedCandidateLists} of {entries.Count} returned entries. " +
                "Affected entries report candidateTotal, candidatesTruncated, and candidateNote.";
            notes.Add(msg);
        }

        var truncated = entriesTruncated || truncatedCandidateLists > 0;
        var complete = !truncated && incompleteReasons.Count == 0;

        return new AzdoBuiltPlanResult
        {
            Entries = entries,
            Complete = complete,
            IncompleteReasons = incompleteReasons,
            Warnings = warnings,
            Truncated = truncated,
            Total = totalJobs,
            MatchStrategy = strategy,
            Note = notes.Count == 0 ? null : string.Join(" ", notes)
        };
    }

    private static List<ProcessedArtifact> FindNormalizedMatches(
        IReadOnlyList<ProcessedArtifact> processedArtifacts,
        string jobName)
    {
        var jobKey = NormalizeKey(jobName);
        if (jobKey.Length == 0)
            return [];

        return processedArtifacts
            .Where(pa =>
                pa.NormalizedKey.Length > 0 &&
                string.Equals(pa.NormalizedKey, jobKey, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Sort and filter job records: only <c>type == "Job"</c> records with a result in
    /// <paramref name="jobResults"/> are included. The <c>none</c> filter selects a null
    /// (unset) API result; <c>"none"</c> is not a literal AzDO timeline task result.
    /// Total deterministic order:
    /// <c>(Order ?? int.MaxValue, Name ordinal, Attempt desc, Id ordinal)</c>.
    /// </summary>
    public static IReadOnlyList<AzdoTimelineRecord> SelectAndSortJobs(
        IEnumerable<AzdoTimelineRecord> records,
        IReadOnlyCollection<string> jobResults)
    {
        var resultSet = new HashSet<string>(jobResults, StringComparer.OrdinalIgnoreCase);
        var includeUnsetResult = resultSet.Contains("none");
        return records
            .Where(r =>
                string.Equals(r.Type, "Job", StringComparison.OrdinalIgnoreCase) &&
                (r.Result is null
                    ? includeUnsetResult
                    : !string.Equals(r.Result, "none", StringComparison.OrdinalIgnoreCase) &&
                      resultSet.Contains(r.Result)))
            .OrderBy(r => r.Order ?? int.MaxValue)
            .ThenBy(r => r.Name ?? "", StringComparer.Ordinal)
            .ThenByDescending(r => r.Attempt ?? 0)
            .ThenBy(r => r.Id ?? "", StringComparer.Ordinal)
            .ToList();
    }

    private record ProcessedArtifact(
        AzdoBuildArtifact Artifact,
        string Bare,
        string NormalizedKey,
        int? Attempt);
}
