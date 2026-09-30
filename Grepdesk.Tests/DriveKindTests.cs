using System.Runtime.Versioning;
using Grepdesk.Core;
using Grepdesk.Windows;

namespace Grepdesk.Tests;

[SupportedOSPlatform("windows")]
public class DriveKindTests
{
    [WindowsFact]
    public void System_drive_is_detected_as_ssd_or_hdd()
    {
        var kind = new WindowsDriveKindProvider().GetKind(Environment.SystemDirectory);
        Assert.True(kind is DriveKind.SolidState or DriveKind.Rotational, $"got {kind}");
    }

    [WindowsFact]
    public void Unc_path_is_network() =>
        Assert.Equal(DriveKind.Network, new WindowsDriveKindProvider().GetKind(@"\\server\share\file.txt"));

    [Theory]
    [InlineData(new[] { DriveKind.SolidState, DriveKind.SolidState }, false)]
    [InlineData(new[] { DriveKind.SolidState, DriveKind.Rotational }, true)]
    [InlineData(new[] { DriveKind.Removable, DriveKind.Network }, true)]
    public void Spinning_or_removable_disks_get_one_worker(DriveKind[] drives, bool single)
    {
        Assert.Equal(single, WorkerPolicy.FileOperations(drives) == 1);
        Assert.Equal(single, WorkerPolicy.Compression(drives) == 1);
    }
}
