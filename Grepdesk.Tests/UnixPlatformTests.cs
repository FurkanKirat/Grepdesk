using Grepdesk.Core;
using Grepdesk.Core.Jobs;
using Grepdesk.Core.Transfer;
using Grepdesk.Linux;
using Grepdesk.MacOS;

namespace Grepdesk.Tests;

/// <summary>
/// Linux and macOS pieces. The parsing and file-writing tests run on every
/// platform; the ones that need a real Linux kernel are [LinuxFact] and run
/// in the Docker container (see README → Testing on Linux).
/// </summary>
public class LinuxClipboardTests
{
    [Fact]
    public void Gnome_copied_files_copy_and_cut()
    {
        var copy = LinuxFileClipboard.ParseGnomeCopiedFiles("copy\nfile:///home/me/a.txt\nfile:///home/me/My%20Folder\n");
        Assert.NotNull(copy);
        Assert.False(copy.IsCut);
        Assert.Equal(["/home/me/a.txt", "/home/me/My Folder"], copy.Paths.Select(p => p.Replace('\\', '/')));

        var cut = LinuxFileClipboard.ParseGnomeCopiedFiles("cut\nfile:///tmp/x");
        Assert.True(cut!.IsCut);
    }

    [Theory]
    [InlineData("")]
    [InlineData("copy\n")]
    [InlineData("hello world")]
    [InlineData("copy\nsftp://server/file")]
    public void Not_a_local_file_list_is_ignored(string text) =>
        Assert.Null(LinuxFileClipboard.ParseGnomeCopiedFiles(text));

    [Fact]
    public void Kde_uri_list_skips_comments_and_non_files()
    {
        var files = LinuxFileClipboard.ParseUriList("# from Dolphin\r\nfile:///srv/%C5%9Fema.png\r\nhttps://example.com\r\n", isCut: true);
        Assert.NotNull(files);
        Assert.True(files.IsCut);
        Assert.Equal(["/srv/şema.png"], files.Paths.Select(p => p.Replace('\\', '/')));
    }
}

public class LinuxDriveKindTests
{
    private const string MountInfo = """
        22 1 259:2 / / rw,relatime shared:1 - ext4 /dev/nvme0n1p2 rw
        30 22 8:17 / /mnt/backup\040disk rw,relatime shared:5 - ext4 /dev/sdb1 rw
        31 22 8:33 / /media/me/USB rw,nosuid shared:6 - vfat /dev/sdc1 rw
        32 22 0:55 / /home rw,relatime shared:7 - btrfs /dev/nvme0n1p3 rw
        33 22 0:60 / /mnt/nas rw,relatime shared:8 - cifs //nas/share rw
        """;

    [Fact]
    public void Mountinfo_is_parsed_including_escaped_spaces()
    {
        var mounts = LinuxDriveKindProvider.ParseMountInfo(MountInfo);
        Assert.Equal(5, mounts.Count);
        Assert.Equal("/mnt/backup disk", mounts[1].MountPoint);
        Assert.Equal("cifs", mounts[4].FileSystem);
        Assert.Equal("//nas/share", mounts[4].Source);
    }

    [Theory]
    [InlineData("/home/me/file", "/home")]
    [InlineData("/homework", "/")]
    [InlineData("/mnt/backup disk/photos", "/mnt/backup disk")]
    [InlineData("/etc", "/")]
    public void Longest_containing_mount_wins(string path, string expected) =>
        Assert.Equal(expected, LinuxDriveKindProvider.FindMount(LinuxDriveKindProvider.ParseMountInfo(MountInfo), path)!.MountPoint);

    [LinuxFact] // needs Linux path rules: on Windows "/mnt/nas" resolves to "C:\mnt\nas"
    public void Network_file_systems_are_recognised_without_sysfs()
    {
        var provider = new LinuxDriveKindProvider(() => MountInfo, "/nonexistent");
        Assert.Equal(DriveKind.Network, provider.GetKind("/mnt/nas/movies"));
        Assert.Equal(DriveKind.Unknown, provider.GetKind("/etc"));
    }

    /// <summary>
    /// A fake /sys laid out like the real one: /sys/dev/block/MAJ:MIN and
    /// /sys/class/block/NAME are symlinks to a partition folder inside its
    /// disk's folder, and the flags live on the disk. Symlinks need Linux.
    /// </summary>
    [LinuxFact]
    public void Partition_uses_its_disks_flags_and_btrfs_falls_back_to_the_source_device()
    {
        using var tmp = new TempDir();
        var sys = tmp.Combine("sys");

        string Disk(string name, string rotational, string removable)
        {
            var disk = Path.Combine(sys, "devices", name);
            Directory.CreateDirectory(Path.Combine(disk, "queue"));
            File.WriteAllText(Path.Combine(disk, "queue", "rotational"), rotational + "\n");
            File.WriteAllText(Path.Combine(disk, "removable"), removable + "\n");
            return disk;
        }

        void Partition(string disk, string name, string number)
        {
            var partition = Directory.CreateDirectory(Path.Combine(disk, name)).FullName;
            File.WriteAllText(Path.Combine(partition, "partition"), "1");
            Directory.CreateDirectory(Path.Combine(sys, "dev", "block"));
            Directory.CreateDirectory(Path.Combine(sys, "class", "block"));
            Directory.CreateSymbolicLink(Path.Combine(sys, "dev", "block", number), partition);
            Directory.CreateSymbolicLink(Path.Combine(sys, "class", "block", name), partition);
        }

        var nvme = Disk("nvme0n1", "0", "0");
        Partition(nvme, "nvme0n1p2", "259:2");
        Partition(nvme, "nvme0n1p3", "259:3");
        Partition(Disk("sdb", "1", "0"), "sdb1", "8:17");
        Partition(Disk("sdc", "0", "1"), "sdc1", "8:33");

        var provider = new LinuxDriveKindProvider(() => MountInfo, sys);
        Assert.Equal(DriveKind.SolidState, provider.GetKind("/etc"));
        Assert.Equal(DriveKind.Rotational, provider.GetKind("/mnt/backup disk/photos"));
        Assert.Equal(DriveKind.Removable, provider.GetKind("/media/me/USB/a.jpg"));
        Assert.Equal(DriveKind.SolidState, provider.GetKind("/home/me")); // btrfs: 0:55 isn't in /sys/dev/block
        Assert.Equal(DriveKind.Network, provider.GetKind("/mnt/nas"));
    }

    [LinuxFact]
    public void Real_system_answers_without_throwing()
    {
        var kind = new LinuxDriveKindProvider().GetKind("/");
        Assert.True(Enum.IsDefined(kind));
    }
}

public class LinuxShellIntegrationTests
{
    private static string Label(string key) => key switch
    {
        "MenuOpenWith" => "Grepdesk ile aç",
        "MenuExtractHere" => "Grepdesk: Buraya çıkart",
        "MenuExtractTo" => "Grepdesk: Klasöre çıkart",
        "MenuCompress" => "Grepdesk ile sıkıştır",
        "MenuPaste" => "Grepdesk ile yapıştır",
        _ => key
    };

    [Fact]
    public void Enable_writes_dolphin_nautilus_and_nemo_entries()
    {
        using var home = new TempDir();
        var shell = new LinuxShellIntegration(home.Path);
        const string exe = "/opt/Grepdesk App/Grepdesk.UI";

        foreach (var feature in Enum.GetValues<ShellFeature>())
        {
            Assert.False(shell.IsEnabled(feature));
            Assert.True(shell.Enable(feature, exe, Label).IsSuccess);
            Assert.True(shell.IsEnabled(feature));
        }

        var dolphin = File.ReadAllText(home.Combine("kio", "servicemenus", "grepdesk-extract-here.desktop"));
        Assert.Contains("MimeType=application/zip;", dolphin);
        Assert.Contains("Exec=\"/opt/Grepdesk App/Grepdesk.UI\" --extract-here %F", dolphin);
        Assert.Contains("Name=Grepdesk: Buraya çıkart", dolphin);

        var open = File.ReadAllText(home.Combine("kio", "servicemenus", "grepdesk-open.desktop"));
        Assert.Contains("Exec=\"/opt/Grepdesk App/Grepdesk.UI\" %F", open);

        var script = File.ReadAllText(home.Combine("nautilus", "scripts", "Grepdesk ile yapıştır"));
        Assert.StartsWith("#!/bin/sh", script);
        Assert.Contains("exec '/opt/Grepdesk App/Grepdesk.UI' --paste \"${1:-$PWD}\"", script);

        var compress = File.ReadAllText(home.Combine("nautilus", "scripts", "Grepdesk ile sıkıştır"));
        Assert.Contains("--compress \"$@\"", compress);

        var background = File.ReadAllText(home.Combine("nemo", "actions", "grepdesk-paste-here.nemo_action"));
        Assert.Contains("Selection=none", background);
        Assert.Contains("--paste %P", background);

        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(home.Combine("kio", "servicemenus", "grepdesk-compress.desktop")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void Language_change_replaces_the_nautilus_script_and_disable_removes_everything()
    {
        using var home = new TempDir();
        var shell = new LinuxShellIntegration(home.Path);

        shell.Enable(ShellFeature.Compress, "/usr/bin/grepdesk", Label);
        shell.Enable(ShellFeature.Compress, "/usr/bin/grepdesk", key => key == "MenuCompress" ? "Compress with Grepdesk" : key);

        var scripts = Directory.GetFiles(home.Combine("nautilus", "scripts")).Select(Path.GetFileName);
        Assert.Equal(["Compress with Grepdesk"], scripts);

        shell.Disable(ShellFeature.Compress);
        Assert.Empty(Directory.GetFiles(home.Path, "*", SearchOption.AllDirectories));
    }
}

public class MacDriveKindTests
{
    [Fact]
    public void Df_output_with_spaces_in_mount_point()
    {
        const string output = """
            Filesystem   512-blocks      Used Available Capacity  Mounted on
            /dev/disk3s1  965595304 400000000 500000000    45%    /Volumes/My Backup
            """;
        Assert.Equal(("/dev/disk3s1", "/Volumes/My Backup"), MacDriveKindProvider.ParseDf(output));
    }

    [Fact]
    public void Plist_booleans_are_read()
    {
        const string plist = """
            <dict>
                <key>Internal</key>
                <true/>
                <key>SolidState</key>
                <false/>
            </dict>
            """;
        Assert.True(MacDriveKindProvider.PlistBool(plist, "Internal"));
        Assert.False(MacDriveKindProvider.PlistBool(plist, "SolidState"));
        Assert.Null(MacDriveKindProvider.PlistBool(plist, "Ejectable"));
    }
}

public class NativeFileCopierTests
{
    [Fact]
    public void Copies_small_and_large_files_with_timestamp()
    {
        using var tmp = new TempDir();
        var stamp = new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        tmp.WriteFile("small.txt", "hi");
        File.SetLastWriteTimeUtc(tmp.Combine("small.txt"), stamp);
        using (var big = File.Create(tmp.Combine("big.bin")))
            big.SetLength(80_000_000); // above the stream threshold

        var ctx = new JobContext(new FixedResolver(ConflictChoice.Overwrite), default);
        long bytes = 0;
        var copier = new NativeFileCopier();
        copier.Copy(tmp.Combine("small.txt"), tmp.Combine("small-copy.txt"), false, ctx, n => bytes += n);
        copier.Copy(tmp.Combine("big.bin"), tmp.Combine("big-copy.bin"), false, ctx, n => bytes += n);

        Assert.Equal("hi", File.ReadAllText(tmp.Combine("small-copy.txt")));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(tmp.Combine("small-copy.txt")));
        Assert.Equal(80_000_000, new FileInfo(tmp.Combine("big-copy.bin")).Length);
        Assert.Equal(80_000_002, bytes);
    }

    [LinuxFact]
    public void Execute_bit_survives_a_copy()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("run.sh", "#!/bin/sh\necho hi\n");
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(tmp.Combine("run.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var ctx = new JobContext(new FixedResolver(ConflictChoice.Overwrite), default);
        new StreamFileCopier().Copy(tmp.Combine("run.sh"), tmp.Combine("stream.sh"), false, ctx, _ => { });
        new NativeFileCopier().Copy(tmp.Combine("run.sh"), tmp.Combine("native.sh"), false, ctx, _ => { });

        Assert.True(File.GetUnixFileMode(tmp.Combine("stream.sh")).HasFlag(UnixFileMode.UserExecute));
        Assert.True(File.GetUnixFileMode(tmp.Combine("native.sh")).HasFlag(UnixFileMode.UserExecute));
    }
}
