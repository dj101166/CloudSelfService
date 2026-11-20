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
        public readonly Dictionary<string, (string Source, long Version)> MovedFrom = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Versions = new(StringComparer.Ordinal);
        // Blob only: what the last commit at a path wrote, valid while the path's version is unchanged.
        public readonly Dictionary<string, (IReadOnlyList<string> BlockIds, Guid? Session, long Version)> Committed = new(StringComparer.Ordinal);
        public long Counter;
        public long Bump(string path) => Versions[path] = ++Counter;
        public bool Seeded;
    }

    private readonly Dictionary<Guid, Tree> _trees = new();
    private readonly List<string> _calls = new();
    private readonly object _gate = new();
    private long _bytesWritten;

    public bool ThrowUnreachable { get; set; }
    public bool ThrowNotAuthorised { get; set; }

    /// <summary>Test hooks: a non-storage exception (as a raw SDK or backend bug would throw) from GetItem / OpenRead.</summary>
    public Exception? UnexpectedOnGetItem { get; set; }
    public Exception? UnexpectedOnOpenRead { get; set; }

    /// <summary>Test hook: given the recorded call (e.g. "BeginUpload:path"), returns an exception to throw, or null.</summary>
    public Func<string, Exception?>? FailOnCall { get; set; }

    /// <summary>Test hook: wraps every stream OpenRead returns, to simulate slow, short or failing reads.</summary>
    public Func<Stream, Stream>? WrapReadStream { get; set; }

    /// <summary>Test hook: a folder move fails (Unreachable) after copying this many files, leaving a partial move.</summary>
    public int? FailFolderMoveAfterFiles { get; set; }

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
                AddFolderWithMarker(tree, folder);
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
        if (FailOnCall?.Invoke(call) is { } fault) throw fault;
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

    private static void AddFolderWithMarker(Tree tree, string path)
    {
        AddFolder(tree, path);
        var marker = StoragePaths.MarkerPath(path);
        tree.Files[marker] = (Array.Empty<byte>(), DateTimeOffset.UtcNow);
        tree.Bump(marker);
    }

    private static void Put(Tree tree, string path, byte[] data)
    {
        AddFolder(tree, StoragePaths.Parent(path));
        tree.Files[path] = (data, DateTimeOffset.UtcNow);
        tree.Bump(path);
    }

    private static bool Exists(Tree tree, string path) =>
        path.Length == 0 || tree.Files.ContainsKey(path) || tree.Folders.Contains(path);

    private static string ETagOf(Tree tree, string path) => $"\"v{tree.Versions.GetValueOrDefault(path)}\"";

    private static StorageItem? Item(Tree tree, string path)
    {
        if (path.Length == 0) return new StorageItem("", "", true, null, null);
        if (StoragePaths.IsHiddenName(StoragePaths.Leaf(path))) return null;
        if (tree.Files.TryGetValue(path, out var f))
            return new StorageItem(StoragePaths.Leaf(path), path, false, f.Data.LongLength, f.Modified, ETagOf(tree, path));
        return tree.Folders.Contains(path) ? new StorageItem(StoragePaths.Leaf(path), path, true, null, null) : null;
    }

    public Task<StorageListing> ListAsync(StorageTarget t, string folder, string? continuation, int pageSize, CancellationToken ct)
    {
        Enter($"List:{folder}");
        lock (_gate)
        {
            var tree = TreeFor(t.Id);
            if (!Exists(tree, folder)) throw Fail(StorageFailureKind.NotFound);

            var children = StoragePaths.SortPage(tree.Folders.Where(p => StoragePaths.Parent(p) == folder)
                .Concat(tree.Files.Keys.Where(p => StoragePaths.Parent(p) == folder && !StoragePaths.IsHiddenName(StoragePaths.Leaf(p))))
                .Select(p => Item(tree, p)!));

            var start = int.TryParse(continuation, out var s) && s > 0 ? s : 0;
            var page = children.Skip(start).Take(pageSize).ToList();
            var next = start + page.Count < children.Count ? (start + page.Count).ToString() : null;
            return Task.FromResult(new StorageListing(page, next));
        }
    }

    public Task<StorageItem?> GetItemAsync(StorageTarget t, string path, CancellationToken ct)
    {
        Enter($"GetItem:{path}");
        if (UnexpectedOnGetItem is { } getItemFault) throw getItemFault;
        lock (_gate) return Task.FromResult(Item(TreeFor(t.Id), path));
    }

    public Task<Stream> OpenReadAsync(StorageTarget t, string path, long offset, long? length, CancellationToken ct, string? expectedETag = null)
    {
        Enter($"OpenRead:{path}");
        if (UnexpectedOnOpenRead is { } openFault) throw openFault;
        lock (_gate)
        {
            var tree = TreeFor(t.Id);
            if (!tree.Files.TryGetValue(path, out var f)) throw Fail(StorageFailureKind.NotFound);
            if (expectedETag is not null && expectedETag != ETagOf(tree, path))
                throw new StorageFailureException(StorageFailureKind.Conflict, StorageErrorMapper.ChangedDuringReadMessage);
            var start = Math.Min(offset, f.Data.LongLength);
            var count = Math.Min(length ?? long.MaxValue, f.Data.LongLength - start);
            Stream stream = new MemoryStream(f.Data, checked((int)start), checked((int)count), writable: false);
            return Task.FromResult(WrapReadStream?.Invoke(stream) ?? stream);
        }
    }

    public Task CreateFolderAsync(StorageTarget t, string path, CancellationToken ct)
    {
        Enter($"CreateFolder:{path}");
        lock (_gate)
        {
            var tree = TreeFor(t.Id);
            if (Exists(tree, path)) throw Fail(StorageFailureKind.AlreadyExists);
            AddFolderWithMarker(tree, path);
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
            if (isFolder && StoragePaths.IsMoveIntoItself(from, to)) throw StorageErrorMapper.MoveIntoItself();
            if (!sourceExists) throw Fail(StorageFailureKind.NotFound);
            if (!isFolder && Exists(tree, to)) throw Fail(StorageFailureKind.AlreadyExists);
            if (isFolder && tree.Files.ContainsKey(to)) throw Fail(StorageFailureKind.AlreadyExists);

            AddFolder(tree, StoragePaths.Parent(to));
            if (!isFolder)
            {
                tree.Files[to] = tree.Files[from];
                tree.Files.Remove(from);
                tree.Versions.Remove(from);
                tree.Bump(to);
                return Task.CompletedTask;
            }

            // Mirrors the blob mover: a destination counts as already copied only if this mover wrote it.
            bool Under(string p) => p == from || p.StartsWith(from + "/", StringComparison.Ordinal);
            var copied = 0;
            foreach (var p in tree.Files.Keys.Where(Under).OrderBy(p => p, StringComparer.Ordinal).ToList())
            {
                var dest = StoragePaths.Rebase(p, from, to);
                var src = tree.Files[p];
                if (tree.Files.TryGetValue(dest, out var existing))
                {
                    var ours = tree.MovedFrom.TryGetValue(dest, out var tag) && tag == (p, tree.Versions[p])
                        && existing.Data.LongLength == src.Data.LongLength;
                    if (!ours) throw Fail(StorageFailureKind.AlreadyExists);
                }
                else
                {
                    if (FailFolderMoveAfterFiles is { } limit && copied >= limit)
                        throw new StorageFailureException(StorageFailureKind.Unreachable, StorageErrorMapper.UnreachableMessage);
                    AddFolder(tree, StoragePaths.Parent(dest));
                    tree.Files[dest] = src;
                    tree.MovedFrom[dest] = (p, tree.Versions[p]);
                    tree.Bump(dest);
                    copied++;
                }
                tree.Files.Remove(p);
                tree.Versions.Remove(p);
                tree.MovedFrom.Remove(dest);
            }
            foreach (var p in tree.Folders.Where(Under).ToList())
            {
                tree.Folders.Remove(p);
                tree.Folders.Add(StoragePaths.Rebase(p, from, to));
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
        lock (_gate)
        {
            var tree = TreeFor(t.Id);
            // Blob blocks are staged on the final name and stay invisible until committed; Files writes a staging file.
            if (t.Kind == StorageKind.BlobContainer)
            {
                tree.Staging.TryAdd(stagingPath, new Dictionary<string, (long, byte[])>());
                return Task.CompletedTask;
            }
            tree.Staging[stagingPath] = new Dictionary<string, (long, byte[])>();
            Put(tree, stagingPath, Array.Empty<byte>());
        }
        return Task.CompletedTask;
    }

    public async Task WriteChunkAsync(StorageTarget t, string stagingPath, string finalPath, string blockId, long offset,
        Stream data, long length, byte[] md5, CancellationToken ct)
    {
        Enter($"WriteChunk:{stagingPath}:{blockId}");
        // The Azure SDKs reject a non-seekable stream ("stream must be seekable") and send Length - Position bytes.
        if (!data.CanSeek) throw new ArgumentException("stream must be seekable", nameof(data));
        if (data.Length - data.Position != length)
            throw new ArgumentException($"stream holds {data.Length - data.Position} bytes, expected {length}", nameof(data));
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
        bool overwrite, CancellationToken ct, Guid? uploadSessionId = null)
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
            tree.Staging.Remove(stagingPath);
            if (stagingPath != finalPath) tree.Files.Remove(stagingPath);
            Put(tree, finalPath, assembled);
            if (t.Kind == StorageKind.BlobContainer)
                tree.Committed[finalPath] = (blockIds.ToArray(), uploadSessionId, tree.Versions[finalPath]);
        }
        return Task.CompletedTask;
    }

    public Task<bool> HasLandedAsync(StorageTarget t, string stagingPath, string finalPath, Guid sessionId, long size,
        CancellationToken ct)
    {
        Enter($"HasLanded:{finalPath}");
        lock (_gate)
        {
            var tree = TreeFor(t.Id);
            if (t.Kind != StorageKind.BlobContainer)
                return Task.FromResult(!tree.Files.ContainsKey(stagingPath) && tree.Files.ContainsKey(finalPath));
            if (!tree.Files.ContainsKey(finalPath)
                || !tree.Committed.TryGetValue(finalPath, out var c) || c.Version != tree.Versions.GetValueOrDefault(finalPath))
                return Task.FromResult(false);
            return Task.FromResult(size > 0
                ? c.BlockIds.Count > 0 && c.BlockIds[0] == CloudSelfService.Services.Transfers.ChunkMath.BlockId(sessionId, 0)
                : c.BlockIds.Count == 0 && c.Session == sessionId);
        }
    }

    public Task AbortUploadAsync(StorageTarget t, string stagingPath, CancellationToken ct)
    {
        Enter($"AbortUpload:{stagingPath}");
        lock (_gate)
        {
            var tree = TreeFor(t.Id);
            tree.Staging.Remove(stagingPath);
            if (t.Kind != StorageKind.BlobContainer) tree.Files.Remove(stagingPath);
        }
        return Task.CompletedTask;
    }

    public Task ProbeAsync(StorageTarget t, CancellationToken ct)
    {
        Enter("Probe");
        return Task.CompletedTask;
    }
}
