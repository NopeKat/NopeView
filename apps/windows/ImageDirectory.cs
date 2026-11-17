using NopeView.App.Native;

namespace NopeView.App;

/// <summary>
/// Supported image files in one directory, with a current position. The core
/// file list service owns enumeration, extension filtering, and sorting; this
/// type only keeps the current index and the movement rules.
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

    internal static ImageDirectory Create(string filePath, IEnumerable<string> extensions)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        var paths = new List<string>();

        if (!string.IsNullOrEmpty(directory) &&
            FilesApi.TryListPaths(directory, extensions, out var corePaths))
        {
            paths.AddRange(corePaths);
        }

        if (paths.Count == 0)
        {
            paths.Add(fullPath);
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        var index = paths.FindIndex(
            path => string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            paths.Add(fullPath);
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            index = paths.FindIndex(
                path => string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase));
        }

        return new ImageDirectory(paths, index);
    }

    internal int Count => _paths.Count;

    internal int Position => _index + 1;

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
