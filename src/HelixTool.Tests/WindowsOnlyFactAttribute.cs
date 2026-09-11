using System.Runtime.InteropServices;
using Xunit;

namespace HelixTool.Tests;

/// <summary>
/// Marks a fact that only makes sense on Windows — e.g. Win32 exclusive-handle/file-share
/// semantics that POSIX unlink-on-rename/delete-while-open rules make inapplicable on
/// Unix. Reports as genuinely <c>Skip</c>ped (not simulated via an early return that would
/// vacuously "pass" with zero assertions) on every other OS, following the same pattern as
/// <c>WindowsShortNameFactAttribute</c> in <c>SnapshotExportTests.cs</c>.
/// </summary>
internal sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip =
                "Windows-only: requires Win32 exclusive file-share semantics, not " +
                "available on " + RuntimeInformation.OSDescription + ".";
        }
    }
}
