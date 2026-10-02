using HelixTool.Core.Acquisition;

namespace HelixTool.Core.AzDO;

public interface IAzdoAcquisitionFailureRecorder
{
    Task RecordBuildLogFailureAsync(
        string org,
        string project,
        int buildId,
        int logId,
        AcquisitionError error,
        CancellationToken ct = default);
}

public sealed class NoOpAzdoAcquisitionFailureRecorder : IAzdoAcquisitionFailureRecorder
{
    public static readonly NoOpAzdoAcquisitionFailureRecorder Instance = new();

    private NoOpAzdoAcquisitionFailureRecorder()
    {
    }

    public Task RecordBuildLogFailureAsync(
        string org,
        string project,
        int buildId,
        int logId,
        AcquisitionError error,
        CancellationToken ct = default)
        => Task.CompletedTask;
}
