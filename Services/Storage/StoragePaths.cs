namespace CloudSelfService.Services.Storage;

/// <summary>Pure path helpers shared by the backends and the fake. Inputs are already-normalised paths.</summary>
public static class StoragePaths
{
    public const string FolderMarker = ".folder";

    public static string Join(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    public static string Parent(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    public static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>Blob listing prefix for a folder: null for the root, otherwise "folder/".</summary>
    public static string? BlobPrefix(string folder) => folder.Length == 0 ? null : folder + "/";

    public static string MarkerPath(string folder) => Join(folder, FolderMarker);

    public static bool IsMarker(string path) => Leaf(path) == FolderMarker;

    /// <summary>A hierarchy-listing prefix such as "a/b/" becomes "a/b".</summary>
    public static string TrimPrefix(string prefix) => prefix.TrimEnd('/');

    /// <summary>Re-roots a path under a moved folder: ("a/b/c.txt","a/b","x/y") becomes "x/y/c.txt".</summary>
    public static string Rebase(string path, string fromFolder, string toFolder) =>
        path.Length == fromFolder.Length ? toFolder : Join(toFolder, path[(fromFolder.Length + 1)..]);

    /// <summary>"a/b/c" yields "a", "a/b", "a/b/c" so parents can be created shallowest-first.</summary>
    public static IEnumerable<string> Ancestors(string path)
    {
        var acc = "";
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            acc = Join(acc, seg);
            yield return acc;
        }
    }
}

/// <summary>Deletion order for a folder tree: files first, then subfolders deepest-first, then the folder itself.</summary>
public static class RecursiveDelete
{
    public static async Task RunAsync(
        string folder,
        Func<string, Task<(IReadOnlyList<string> Files, IReadOnlyList<string> Folders)>> list,
        Func<string, Task> deleteFile,
        Func<string, Task> deleteFolder)
    {
        var (files, folders) = await list(folder).ConfigureAwait(false);
        foreach (var f in files) await deleteFile(StoragePaths.Join(folder, f)).ConfigureAwait(false);
        foreach (var d in folders) await RunAsync(StoragePaths.Join(folder, d), list, deleteFile, deleteFolder).ConfigureAwait(false);
        await deleteFolder(folder).ConfigureAwait(false);
    }
}
