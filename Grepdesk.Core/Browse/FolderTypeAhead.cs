namespace Grepdesk.Core.Browse;

/// <summary>
/// Explorer-style type-ahead matching for a folder listing: typing different
/// characters narrows by prefix, while pressing the same single character
/// repeatedly cycles through all entries starting with that character.
/// </summary>
public sealed class FolderTypeAhead
{
    public const long DefaultTimeoutMs = 1000;

    private readonly long _timeoutMs;
    private string _buffer = "";
    private long _lastInputMs;

    public FolderTypeAhead(long timeoutMs = DefaultTimeoutMs)
    {
        _timeoutMs = timeoutMs;
    }

    public void Reset()
    {
        _buffer = "";
        _lastInputMs = 0;
    }

    /// <summary>
    /// Returns the index in <paramref name="names"/> to select for <paramref name="input"/>,
    /// or -1 when no entry matches or the input should be ignored.
    /// </summary>
    public int NextMatch(IReadOnlyList<string> names, int currentIndex, string? input, long nowMs)
    {
        if (names.Count == 0 || string.IsNullOrEmpty(input)) return -1;
        foreach (var ch in input)
        {
            if (char.IsControl(ch)) return -1;
        }

        if (_buffer.Length > 0 && nowMs - _lastInputMs > _timeoutMs)
            _buffer = "";

        if (_buffer.Length == 0 && string.IsNullOrWhiteSpace(input))
            return -1;

        _lastInputMs = nowMs;

        var isSameSingleChar = input.Length == 1 &&
            (_buffer.Length == 0 || (_buffer.Length == 1 && char.ToUpperInvariant(_buffer[0]) == char.ToUpperInvariant(input[0])));

        if (isSameSingleChar)
        {
            _buffer = input;
            var start = currentIndex >= 0 && currentIndex < names.Count ? (currentIndex + 1) % names.Count : 0;
            return FindPrefixMatch(names, _buffer, start);
        }

        _buffer += input;
        var prefixStart = currentIndex >= 0 && currentIndex < names.Count ? currentIndex : 0;
        return FindPrefixMatch(names, _buffer, prefixStart);
    }

    private static int FindPrefixMatch(IReadOnlyList<string> names, string prefix, int startIndex)
    {
        for (var offset = 0; offset < names.Count; offset++)
        {
            var idx = (startIndex + offset) % names.Count;
            if (names[idx].StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
                return idx;
        }
        return -1;
    }
}
