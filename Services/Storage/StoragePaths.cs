using System.Text.RegularExpressions;
using CloudSelfService.Models;
using CloudSelfService.Services.Access;

namespace CloudSelfService.Services.Storage;

/// <summary>Pure path helpers shared by the backends and the fake. Inputs are already-normalised paths.</summary>
public static class StoragePaths
{
    public const string FolderMarker = ".folder";

    public static string Join(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    public static string Parent(string path) => StoragePath.Parent(path);

    public static string Leaf(string path) => StoragePath.Leaf(path);

    /// <summary>Blob listing prefix for a folder: null for the root, otherwise "folder/".</summary>
    public static string? BlobPrefix(string folder) => folder.Length == 0 ? null : folder + "/";

    public static string MarkerPath(string folder) => Join(folder, FolderMarker);

    public static bool IsMarker(string path) => Leaf(path) == FolderMarker;

    private static readonly Regex StagingName = new(@"^\..+\.CSSUPLOAD-[0-9A-F]{32}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Matched on the upper-cased name, so a character that case-folds into "cssupload" (such as U+017F) is covered. Caller-chosen upload staging names ("{folder}/.{name}.cssupload-{sessionId:N}") must never be listed.</summary>
    public static bool IsStagingName(string name) => StagingName.IsMatch(name.ToUpperInvariant());

    /// <summary>Entries users must never see: the folder marker and in-flight upload staging files.</summary>
    public static bool IsHiddenName(string name) => name == FolderMarker || IsStagingName(name);

    /// <summary>Metadata key a mover stamps on every blob it writes, so a resumed move can tell its own copies from foreign files.</summary>
    public const string MoveSourceKey = "cssp_move_src";

    /// <summary>Metadata-safe (ASCII) form of a source path.</summary>
    public static string EncodeMoveSource(string sourcePath) => Uri.EscapeDataString(sourcePath);

    /// <summary>Tag value binding a copy to one instance of its source: a recreated or modified source has a new ETag.</summary>
    public static string MoveTag(string sourcePath, string sourceETag) =>
        EncodeMoveSource(sourcePath) + "|" + Uri.EscapeDataString(sourceETag.Trim('"'));

    public const int MaxMetadataTagBytes = 4096;

    public static bool IsMoveIntoItself(string from, string to) =>
        to == from || to.StartsWith(from + "/", StringComparison.Ordinal);

    /// <summary>Folders first, then files, each ordinal-ignore-case by name. Ordering holds within a page only.</summary>
    public static List<StorageItem> SortPage(IEnumerable<StorageItem> items) =>
        items.OrderBy(i => i.IsFolder ? 0 : 1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

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

/// <summary>One server-side copy block: base64 fixed-width id, source offset and length.</summary>
public readonly record struct CopyBlock(string BlockId, long Offset, long Length);

public static class BlockPlanner
{
    public const long MaxBlockBytes = 4000L * 1024 * 1024;

    public static IReadOnlyList<CopyBlock> Plan(long size, long maxBlock = MaxBlockBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        var blocks = new List<CopyBlock>();
        for (long offset = 0; offset < size; offset += maxBlock)
        {
            var id = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"move-{blocks.Count:D6}"));
            blocks.Add(new CopyBlock(id, offset, Math.Min(maxBlock, size - offset)));
        }
        return blocks;
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
