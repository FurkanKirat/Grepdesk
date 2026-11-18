using Grepdesk.Core.Viewer;

namespace Grepdesk.Tests;

public class FileChangeTrackerTests
{
    [Fact]
    public async Task External_modification_is_detected_via_watcher()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("notes.txt", "hello");
        var baseline = FileSnapshot.Capture(path);
        using var tracker = new FileChangeTracker(path, baseline, TimeSpan.FromMilliseconds(40));

        var detected = new TaskCompletionSource<FileChangeKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.StateChanged += kind =>
        {
            if (kind == FileChangeKind.Modified)
                detected.TrySetResult(kind);
        };
        tracker.Start();

        await File.WriteAllTextAsync(path, "hello world");

        var result = await detected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FileChangeKind.Modified, result);
        Assert.Equal(FileChangeKind.Modified, tracker.CurrentKind);
        Assert.True(tracker.HasDiskDivergedFromBaseline());
    }

    [Fact]
    public void Same_length_and_timestamp_edit_is_detected_via_content_hash()
    {
        using var dir = new TempDir();
        var stamp = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var path = dir.WriteFile("same.txt", "abcd");
        File.SetLastWriteTimeUtc(path, stamp);

        var baseline = FileSnapshot.Capture(path);
        using var tracker = new FileChangeTracker(path, baseline);

        File.WriteAllText(path, "wxyz");
        File.SetLastWriteTimeUtc(path, stamp);

        tracker.CheckNow();

        Assert.Equal(FileChangeKind.Modified, tracker.CurrentKind);
        Assert.True(tracker.HasDiskDivergedFromBaseline());
    }

    [Fact]
    public async Task Atomic_save_via_temp_rename_is_reported_as_modified_not_deleted()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("atomic.txt", "version 1");
        var baseline = FileSnapshot.Capture(path);
        using var tracker = new FileChangeTracker(path, baseline, TimeSpan.FromMilliseconds(60));

        var sawDeleted = false;
        var modified = new TaskCompletionSource<FileChangeKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.StateChanged += kind =>
        {
            if (kind == FileChangeKind.Deleted) sawDeleted = true;
            if (kind == FileChangeKind.Modified) modified.TrySetResult(kind);
        };
        tracker.Start();

        var temp = dir.WriteFile("atomic.tmp", "version 2 from atomic save");
        File.Move(temp, path, overwrite: true);

        var result = await modified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FileChangeKind.Modified, result);
        Assert.False(sawDeleted);
        Assert.Equal(FileChangeKind.Modified, tracker.CurrentKind);
    }

    [Fact]
    public async Task Internal_save_suppresses_notification_and_clears_existing_change()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("doc.txt", "v1");
        var baseline = FileSnapshot.Capture(path);
        using var tracker = new FileChangeTracker(path, baseline, TimeSpan.FromMilliseconds(40));

        var events = new List<FileChangeKind>();
        tracker.StateChanged += kind => { lock (events) events.Add(kind); };
        tracker.Start();

        File.WriteAllText(path, "v2 external");
        tracker.CheckNow();
        Assert.Equal(FileChangeKind.Modified, tracker.CurrentKind);
        Assert.True(tracker.HasDiskDivergedFromBaseline());

        tracker.BeginInternalSave();
        File.WriteAllText(path, "v3 internal save");
        tracker.EndInternalSave(saved: true);

        await Task.Delay(150);

        Assert.Equal(FileChangeKind.None, tracker.CurrentKind);
        Assert.False(tracker.HasDiskDivergedFromBaseline());
        lock (events)
        {
            Assert.Equal([FileChangeKind.Modified, FileChangeKind.None], events);
        }
    }

    [Fact]
    public void Dismiss_hides_current_revision_but_realerts_on_newer_change()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("dismiss.txt", "v1");
        var baseline = FileSnapshot.Capture(path);
        using var tracker = new FileChangeTracker(path, baseline);

        File.WriteAllText(path, "v2");
        tracker.CheckNow();
        Assert.Equal(FileChangeKind.Modified, tracker.CurrentKind);

        tracker.Dismiss();
        Assert.Equal(FileChangeKind.None, tracker.CurrentKind);
        Assert.True(tracker.HasDiskDivergedFromBaseline());

        tracker.CheckNow();
        Assert.Equal(FileChangeKind.None, tracker.CurrentKind);

        File.WriteAllText(path, "v3 newer");
        tracker.CheckNow();
        Assert.Equal(FileChangeKind.Modified, tracker.CurrentKind);
    }

    [Fact]
    public void Deletion_and_recreation_transition_correctly()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("vanish.txt", "v1");
        var baseline = FileSnapshot.Capture(path);
        using var tracker = new FileChangeTracker(path, baseline);

        File.Delete(path);
        tracker.CheckNow();
        Assert.Equal(FileChangeKind.Deleted, tracker.CurrentKind);
        Assert.False(tracker.HasDiskDivergedFromBaseline());

        File.WriteAllText(path, "v2 recreated");
        tracker.CheckNow();
        Assert.Equal(FileChangeKind.Modified, tracker.CurrentKind);
        Assert.True(tracker.HasDiskDivergedFromBaseline());
    }

    [Fact]
    public void External_revert_to_baseline_clears_notification()
    {
        using var dir = new TempDir();
        var stamp = new DateTime(2026, 6, 2, 8, 30, 0, DateTimeKind.Utc);
        var path = dir.WriteFile("revert.txt", "original");
        File.SetLastWriteTimeUtc(path, stamp);

        var baseline = FileSnapshot.Capture(path);
        using var tracker = new FileChangeTracker(path, baseline);

        File.WriteAllText(path, "modified");
        tracker.CheckNow();
        Assert.Equal(FileChangeKind.Modified, tracker.CurrentKind);

        File.WriteAllText(path, "original");
        File.SetLastWriteTimeUtc(path, stamp);
        tracker.CheckNow();
        Assert.Equal(FileChangeKind.None, tracker.CurrentKind);
        Assert.False(tracker.HasDiskDivergedFromBaseline());
    }

    [Fact]
    public async Task Sibling_file_changes_do_not_trigger_notifications()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("target.txt", "stay");
        var baseline = FileSnapshot.Capture(path);
        using var tracker = new FileChangeTracker(path, baseline, TimeSpan.FromMilliseconds(30));

        var notified = false;
        tracker.StateChanged += _ => notified = true;
        tracker.Start();

        var sibling = dir.WriteFile("other.txt", "one");
        File.WriteAllText(sibling, "two");
        var renamed = dir.Combine("other2.txt");
        File.Move(sibling, renamed);
        File.Delete(renamed);

        await Task.Delay(150);
        tracker.CheckNow();

        Assert.False(notified);
        Assert.Equal(FileChangeKind.None, tracker.CurrentKind);
    }
}
