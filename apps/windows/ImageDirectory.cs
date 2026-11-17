namespace NopeView.App;

/// <summary>
/// Supported image files in one directory, with a current position.
/// This is the Windows-side list for now; the pure ordering and index logic
/// can move to apps/core once the shared file-system abstraction exists.
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
        var allowed = new HashSet<string>(
            extensions.Select(NormalizeExtension),
            StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();

        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            try
            {
                foreach (var candidate in Directory.EnumerateFiles(directory))
                {
                    if (allowed.Contains(Path.GetExtension(candidate)))
                    {
                        paths.Add(candidate);
                    }
                }
            }
            catch (IOException)
            {
                // Fall back to the single file below.
            }
            catch (UnauthorizedAccessException)
            {
                // Fall back to the single file below.
            }
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

    private static string NormalizeExtension(string extension)
    {
        return extension.StartsWith('.') ? extension : "." + extension;
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
