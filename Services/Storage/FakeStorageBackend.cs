using System.Security.Cryptography;
using CloudSelfService.Models;

namespace CloudSelfService.Services.Storage;

/// <summary>
/// In-memory backend with the same observable semantics as the Files and Blob backends: folders
/// created with a marker are listed but the marker is hidden, an upload is invisible until it is
/// completed, and every failure surfaces as a <see cref="StorageFailureException"/>. Registered
/// only in Development with <c>Storage:UseFake=true</c>; every automated test runs against it.
/// </summary>
public sealed class FakeStorageBackend : IStorageBackend, IStorageBackendResolver
{
    private sealed class Tree
    {
        public readonly Dictionary<string, (byte[] Data, DateTimeOffset Modified)> Files = new(StringComparer.Ordinal);
        public readonly HashSet<string> Folders = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, (long Offset, byte[] Data)>> Staging = new(StringComparer.Ordinal);
        public bool Seeded;
    }

    private readonly Dictionary<Guid, Tree> _trees = new();
    private readonly List<string> _calls = new();
    private readonly object _gate = new();
    private long _bytesWritten;

    public bool ThrowUnreachable { get; set; }
    public bool ThrowNotAuthorised { get; set; }

    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) return _calls.ToArray(); }
    }

    public IStorageBackend For(StorageTarget t) => this;

    /// <summary>Gives a target a few folders and files so dev personas have something to browse. Runs once per target.</summary>
    public void Seed(Guid targetId)
    {
        lock (_gate)
        {
            var tree = TreeFor(targetId);
            if (tree.Seeded) return;
            tree.Seeded = true;
            foreach (var folder in new[] { "Documents", "Reports/2026", "Archive" })
                AddFolder(tree, folder);
            Put(tree, "Welcome.txt", "Sample file seeded for local development."u8.ToArray());
            Put(tree, "Documents/Readme.txt", "Documents folder sample."u8.ToArray());
            Put(tree, "Reports/2026/January.csv", "month,total\nJanuary,42\n"u8.ToArray());
        }
    }

    private Tree TreeFor(Guid id)
    {
        if (!_trees.TryGetValue(id, out var tree)) _trees[id] = tree = new Tree();
        return tree;
    }

    private void Enter(string call)
    {
        lock (_gate) _calls.Add(call);
        if (ThrowUnreachable)
            throw new StorageFailureException(StorageFailureKind.Unreachable, StorageErrorMapper.UnreachableMessage);
        if (ThrowNotAuthorised)
            throw new StorageFailureException(StorageFailureKind.NotAuthorised, StorageErrorMapper.NotAuthorisedMessage);
    }

    private static StorageFailureException Fail(StorageFailureKind kind) => kind switch
    {
        StorageFailureKind.NotFound => new(kind, StorageErrorMapper.NotFoundMessage),
        StorageFailureKind.AlreadyExists => new(kind, StorageErrorMapper.AlreadyExistsMessage),
        StorageFailureKind.Conflict => new(kind, StorageErrorMapper.CorruptedChunkMessage),
        _ => new(kind, StorageErrorMapper.OtherMessage),
    };

    private static void AddFolder(Tree tree, string path)
    {
        foreach (var a in StoragePaths.Ancestors(path)) tree.Folders.Add(a);
    }

    private static void Put(Tree tree, string path, byte[] data)
    {
        AddFolder(tree, StoragePaths.Parent(path));
        tree.Files[path] = (data, DateTimeOffset.UtcNow);
    }

    private static bool Exists(Tree tree, string path) =>
        path.Length == 0 || tree.Files.ContainsKey(path) || tree.Folders.Contains(path);

    private static StorageItem? Item(Tree tree, string path)
    {
        if (path.Length == 0) return new StorageItem("", "", true, null, null);
        if (tree.Files.TryGetValue(path, out var f))
            return new StorageItem(StoragePaths.Leaf(path), path, false, f.Data.LongLength, f.Modified);
        return tree.Folders.Contains(path) ? new StorageItem(StoragePaths.Leaf(path), path, true, null, null) : null;
    }

    public Task<StorageListing> ListAsync(StorageTarget t, string folder, string? continuation, int pageSize, CancellationToken ct)
    {
        Enter($"List:{folder}");
        lock (_gate)
        {
            var tree = TreeFor(t.Id);
            if (!Exists(tree, folder)) throw Fail(StorageFailureKind.NotFound);

            var children = tree.Folders.Where(p => StoragePaths.Parent(p) == folder).OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => Item(tree, p)!)
                .Concat(tree.Files.Keys.Where(p => StoragePaths.Parent(p) == folder && !StoragePaths.IsMarker(p))
                    .OrderBy(p => p, StringComparer.Ordinal).Select(p => Item(tree, p)!))
                .ToList();

            var start = int.TryParse(continuation, out var s) && s > 0 ? s : 0;
            var page = children.Skip(start).Take(pageSize).ToList();
            var next = start + page.Count < children.Count ? (start + page.Count).ToString() : null;
            return Task.FromResult(new StorageListing(page, next));
        }
    }

    public Task<StorageItem?> GetItemAsync(StorageTarget t, string path, CancellationToken ct)
    {
        Enter($"GetItem:{path}");
        lock (_gate) return Task.FromResult(Item(TreeFor(t.Id), path));
    }

    public Task<Stream> OpenReadAsync(StorageTarget t, string path, long offset, long? length, CancellationToken ct)
    {
        Enter($"OpenRead:{path}");
        lock (_gate)
        {
            if (!TreeFor(t.Id).Files.TryGetValue(path, out var f)) throw Fail(StorageFailureKind.NotFound);
            var start = (int)Math.Min(offset, f.Data.Length);
            var count = (int)Math.Min(length ?? long.MaxValue, f.Data.Length - start);
            return Task.FromResult<Stream>(new MemoryStream(f.Data, start, count, writable: false));
        }
    }

    public Task CreateFolderAsync(StorageTarget t, string path, CancellationToken ct)
    {
        Enter($"CreateFolder:{path}");
        lock (_gate)
        {
            var tree = TreeFor(t.Id);
            if (Exists(tree, path)) throw Fail(StorageFailureKind.AlreadyExists);
            AddFolder(tree, path);
        }
        return Task.CompletedTask;
    }

    public Task MoveAsync(StorageTarget t, string from, string to, bool isFolder, CancellationToken ct)
    {
        Enter($"Move:{from}->{to}");
        lock (_gate)
        {
            var tree = TreeFor(t.Id);
            var sourceExists = isFolder ? tree.Folders.Contains(from) : tree.Files.ContainsKey(from);
            if (!sourceExists) throw Fail(StorageFailureKind.NotFound);
            if (Exists(tree, to)) throw Fail(StorageFailureKind.AlreadyExists);
            if (isFolder && to.StartsWith(from + "/", StringComparison.Ordinal)) throw Fail(StorageFailureKind.Other);

            AddFolder(tree, StoragePaths.Parent(to));
            if (!isFolder)
            {
                tree.Files[to] = tree.Files[from];
                tree.Files.Remove(from);
                return Task.CompletedTask;
            }

            bool Under(string p) => p == from || p.StartsWith(from + "/", StringComparison.Ordinal);
            foreach (var p in tree.Folders.Where(Under).ToList())
            {
                tree.Folders.Remove(p);
                tree.Folders.Add(StoragePaths.Rebase(p, from, to));
            }
            foreach (var p in tree.Files.Keys.Where(Under).ToList())
            {
                tree.Files[StoragePaths.Rebase(p, from, to)] = tree.Files[p];
                tree.Files.Remove(p);
            }
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(StorageTarget t, string path, bool isFolder, CancellationToken ct)
    {
        Enter($"Delete:{path}");
        lock (_gate)
        {
            var tree = TreeFor(t.Id);
            if (!isFolder)
            {
                if (!tree.Files.Remove(path)) throw Fail(StorageFailureKind.NotFound);
                return Task.CompletedTask;
            }

            if (!tree.Folders.Contains(path)) throw Fail(StorageFailureKind.NotFound);
            bool Under(string p) => p == path || p.StartsWith(path + "/", StringComparison.Ordinal);
            tree.Folders.RemoveWhere(Under);
            foreach (var p in tree.Files.Keys.Where(Under).ToList()) tree.Files.Remove(p);
        }
        return Task.CompletedTask;
    }

    public Task BeginUploadAsync(StorageTarget t, string stagingPath, long size, CancellationToken ct)
    {
        Enter($"BeginUpload:{stagingPath}");
        lock (_gate) TreeFor(t.Id).Staging[stagingPath] = new Dictionary<string, (long, byte[])>();
        return Task.CompletedTask;
    }

    public async Task WriteChunkAsync(StorageTarget t, string stagingPath, string finalPath, string blockId, long offset,
        Stream data, long length, byte[] md5, CancellationToken ct)
    {
        Enter($"WriteChunk:{stagingPath}:{blockId}");
        var buffer = new byte[length];
        await data.ReadExactlyAsync(buffer, ct);
        if (!MD5.HashData(buffer).AsSpan().SequenceEqual(md5)) throw Fail(StorageFailureKind.Conflict);

        lock (_gate)
        {
            if (!TreeFor(t.Id).Staging.TryGetValue(stagingPath, out var blocks)) throw Fail(StorageFailureKind.NotFound);
            blocks[blockId] = (offset, buffer);
        }
        Interlocked.Add(ref _bytesWritten, length);
    }

    public Task CompleteUploadAsync(StorageTarget t, string stagingPath, string finalPath, IReadOnlyList<string> blockIds,
        bool overwrite, CancellationToken ct)
    {
        Enter($"CompleteUpload:{finalPath}");
        lock (_gate)
        {
            var tree = TreeFor(t.Id);
            if (!tree.Staging.TryGetValue(stagingPath, out var blocks)) throw Fail(StorageFailureKind.NotFound);
            if (tree.Folders.Contains(finalPath) || (!overwrite && tree.Files.ContainsKey(finalPath)))
                throw Fail(StorageFailureKind.AlreadyExists);
            if (blockIds.Any(id => !blocks.ContainsKey(id))) throw Fail(StorageFailureKind.Other);

            var assembled = blockIds.SelectMany(id => blocks[id].Data).ToArray();
            Put(tree, finalPath, assembled);
            tree.Staging.Remove(stagingPath);
        }
        return Task.CompletedTask;
    }

    public Task AbortUploadAsync(StorageTarget t, string stagingPath, CancellationToken ct)
    {
        Enter($"AbortUpload:{stagingPath}");
        lock (_gate) TreeFor(t.Id).Staging.Remove(stagingPath);
        return Task.CompletedTask;
    }

    public Task ProbeAsync(StorageTarget t, CancellationToken ct)
    {
        Enter("Probe");
        return Task.CompletedTask;
    }
}
