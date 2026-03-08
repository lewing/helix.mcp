using Xunit;

namespace HelixTool.Tests;

/// <summary>
/// Collection definition for tests that mutate the HLX_DISABLE_FILE_SEARCH environment variable.
/// Classes in this collection run sequentially to avoid flaky parallel access to the process-wide env var.
/// </summary>
[CollectionDefinition("FileSearchConfig", DisableParallelization = true)]
public class FileSearchConfigCollection;
