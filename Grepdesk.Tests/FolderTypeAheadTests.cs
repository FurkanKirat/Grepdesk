using Grepdesk.Core.Browse;

namespace Grepdesk.Tests;

public class FolderTypeAheadTests
{
    private static readonly string[] SampleNames = ["Alpha", "Files", "Fought", "Four", "Fox", "Zebra"];

    [Fact]
    public void Pressing_same_letter_repeatedly_cycles_through_matches_and_wraps()
    {
        var typeAhead = new FolderTypeAhead(timeoutMs: 1000);

        var idx = typeAhead.NextMatch(SampleNames, -1, "f", 100);
        Assert.Equal(1, idx); // Files

        idx = typeAhead.NextMatch(SampleNames, idx, "f", 200);
        Assert.Equal(2, idx); // Fought

        idx = typeAhead.NextMatch(SampleNames, idx, "f", 300);
        Assert.Equal(3, idx); // Four

        idx = typeAhead.NextMatch(SampleNames, idx, "f", 400);
        Assert.Equal(4, idx); // Fox

        idx = typeAhead.NextMatch(SampleNames, idx, "f", 500);
        Assert.Equal(1, idx); // wraps back to Files
    }

    [Fact]
    public void Pressing_same_letter_after_timeout_still_advances_to_next_match()
    {
        var typeAhead = new FolderTypeAhead(timeoutMs: 1000);

        var idx = typeAhead.NextMatch(SampleNames, -1, "f", 100);
        Assert.Equal(1, idx); // Files

        idx = typeAhead.NextMatch(SampleNames, idx, "f", 3000);
        Assert.Equal(2, idx); // Fought
    }

    [Fact]
    public void Typing_different_letters_narrows_by_prefix()
    {
        var typeAhead = new FolderTypeAhead(timeoutMs: 1000);

        var idx = typeAhead.NextMatch(SampleNames, -1, "f", 100);
        Assert.Equal(1, idx); // Files

        idx = typeAhead.NextMatch(SampleNames, idx, "o", 250);
        Assert.Equal(2, idx); // Fought

        idx = typeAhead.NextMatch(SampleNames, idx, "u", 400);
        Assert.Equal(2, idx); // Fought still matches "fou"

        idx = typeAhead.NextMatch(SampleNames, idx, "r", 550);
        Assert.Equal(3, idx); // Four
    }

    [Fact]
    public void Leading_space_and_control_characters_are_ignored_but_space_in_prefix_matches()
    {
        string[] names = ["My Documents", "My Folder", "Notes"];
        var typeAhead = new FolderTypeAhead(timeoutMs: 1000);

        Assert.Equal(-1, typeAhead.NextMatch(names, -1, " ", 100));
        Assert.Equal(-1, typeAhead.NextMatch(names, -1, "\r", 150));

        var idx = typeAhead.NextMatch(names, -1, "m", 200);
        Assert.Equal(0, idx); // My Documents
        idx = typeAhead.NextMatch(names, idx, "y", 300);
        idx = typeAhead.NextMatch(names, idx, " ", 400);
        idx = typeAhead.NextMatch(names, idx, "f", 500);
        Assert.Equal(1, idx); // My Folder
    }

    [Fact]
    public void Reset_clears_accumulated_prefix()
    {
        var typeAhead = new FolderTypeAhead(timeoutMs: 1000);

        var idx = typeAhead.NextMatch(SampleNames, -1, "f", 100);
        Assert.Equal(1, idx);

        typeAhead.Reset();

        idx = typeAhead.NextMatch(SampleNames, -1, "z", 200);
        Assert.Equal(5, idx); // Zebra
    }
}
