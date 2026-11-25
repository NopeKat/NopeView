namespace NopeView.App;

/// <summary>
/// Supported image files in one directory, with a current position. Enumeration
/// runs on a worker thread and hands its result over; this type only sorts the
/// paths and keeps the current index and the movement rules.
/// </summary>
internal sealed class ImageDirectory
{
    private readonly List<string> _paths;
    private int _index;

    private ImageDirectory(List<string> paths, int index)
    {
        _paths = paths;
        _index = index;
    }

    /// <summary>
    /// Wraps an enumerated path list around the file that is being opened. An
    /// empty list, which a missing or unreadable directory produces, still
    /// leaves the opened file as the only entry.
    /// </summary>
    internal static ImageDirectory FromPaths(string fullPath, IReadOnlyList<string> paths)
    {
        var sorted = paths.Count > 0 ? new List<string>(paths) : new List<string>();
        if (sorted.Count == 0)
        {
            sorted.Add(fullPath);
        }
        else
        {
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
        }

        var index = sorted.BinarySearch(fullPath, StringComparer.OrdinalIgnoreCase);
        if (index < 0)
        {
            // BinarySearch reports the insertion point as the complement of the
            // index, so the file joins the list without sorting it again.
            sorted.Insert(~index, fullPath);
            index = ~index;
        }

        return new ImageDirectory(sorted, index);
    }

    internal string CurrentPath => _paths[_index];

    internal bool HasPrevious => _index > 0;

    internal bool HasNext => _index + 1 < _paths.Count;

    internal bool MovePrevious()
    {
        if (!HasPrevious)
        {
            return false;
        }
        _index--;
        return true;
    }

    internal bool MoveNext()
    {
        if (!HasNext)
        {
            return false;
        }
        _index++;
        return true;
    }
}
