namespace HelixTool.Tests;

internal static class TestConsoleCapture
{
    public static readonly SemaphoreSlim Lock = new(1, 1);
}
