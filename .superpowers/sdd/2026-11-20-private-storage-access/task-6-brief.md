### Task 6: Storage backends (Files + Blob)

**Files:**
- Create: `Services/Storage/IStorageBackend.cs`, `Services/Storage/StorageClientFactory.cs`, `Services/Storage/FileShareBackend.cs`, `Services/Storage/BlobBackend.cs`, `Services/Storage/StorageErrorMapper.cs`, `Services/Storage/FakeStorageBackend.cs`. The fake lives in the app, not the test project, because Task 10 registers it for local UI work. It is only registered when `Storage:UseFake=true` **and** `IsDevelopment()`, and the startup guard `DevSignIn.AssertSafe` also throws when `Storage:UseFake=true` outside Development.
- Modify: `CloudSelfService.csproj` (add `Azure.Identity`, `Azure.Storage.Files.Shares`, `Azure.Storage.Blobs`), `Program.cs`
- Test: `StorageErrorMapperTests.cs`, `StorageClientFactoryTests.cs`, `FakeStorageBackendTests.cs`

**Interfaces:**

```csharp
public interface IStorageBackend {
    Task<StorageListing> ListAsync(StorageTarget t, string folder, string? continuation, int pageSize, CancellationToken ct);
    Task<StorageItem?> GetItemAsync(StorageTarget t, string path, CancellationToken ct);           // null = not found
    Task<Stream> OpenReadAsync(StorageTarget t, string path, long offset, long? length, CancellationToken ct);
    Task CreateFolderAsync(StorageTarget t, string path, CancellationToken ct);
    Task MoveAsync(StorageTarget t, string from, string to, bool isFolder, CancellationToken ct);
    Task DeleteAsync(StorageTarget t, string path, bool isFolder, CancellationToken ct);           // recursive for folders
    Task BeginUploadAsync(StorageTarget t, string stagingPath, long size, CancellationToken ct);
    Task WriteChunkAsync(StorageTarget t, string stagingPath, string finalPath, string blockId, long offset, Stream data, long length, byte[] md5, CancellationToken ct);
    Task CompleteUploadAsync(StorageTarget t, string stagingPath, string finalPath, IReadOnlyList<string> blockIds, bool overwrite, CancellationToken ct);
    Task AbortUploadAsync(StorageTarget t, string stagingPath, CancellationToken ct);
    Task ProbeAsync(StorageTarget t, CancellationToken ct);
}
public interface IStorageBackendResolver { IStorageBackend For(StorageTarget t); }   // by t.Kind
public sealed class StorageClientFactory {   // singleton; caches clients per (account, kind, suffix); one shared DefaultAzureCredential
    public ShareClient Share(StorageTarget t);
    public BlobContainerClient Container(StorageTarget t);
    public static Uri ServiceUri(StorageTarget t);              // https://{account}.{file|blob}.{EndpointSuffix}/
    internal static ShareClientOptions BuildShareOptions();     // ShareTokenIntent = Backup
}
public enum StorageFailureKind { NotFound, AlreadyExists, NotAuthorised, Unreachable, Conflict, Other }
public sealed class StorageFailureException : Exception {
    public StorageFailureException(StorageFailureKind kind, string userMessage, Exception? inner = null);
    public StorageFailureKind Kind { get; }
}
public static class StorageErrorMapper { public static StorageFailureException Map(Exception ex); }
```

**Behaviour:**
- **Files:**
  - List uses `GetFilesAndDirectoriesAsync().AsPages(continuation, pageSize)` and returns one page.
  - Create folder creates any missing parents.
  - Move uses `RenameAsync` with `ReplaceIfExists=false`.
  - Deleting a folder is recursive: files first, then subfolders deepest-first.
  - Begin upload calls `CreateAsync(maxSize: size)` at the staging path.
  - Each chunk goes through `UploadRangeAsync(new HttpRange(offset,length), data, new ShareFileUploadRangeOptions { TransactionalContentHash = md5 })`.
  - Complete renames staging → final with `ReplaceIfExists = overwrite`.
  - Abort deletes the staging file and ignores NotFound.
- **Blob:**
  - List uses `GetBlobsByHierarchyAsync(delimiter:"/", prefix: folder==""?null:folder+"/").AsPages(continuation, pageSize)`.
  - Create folder uploads a zero-byte marker `{path}/.folder`. Listings hide `.folder` but keep its folder visible. (Spec §8 leaves this open; this is my call.)
  - Move copies each blob under the prefix (or the single blob) with `SyncCopyFromUriAsync` (managed-identity bearer; `StartCopyFromUri` + poll above 256 MiB), then deletes the source.
  - Deleting a folder deletes every blob under the prefix.
  - Begin upload does nothing, and staging is unused.
  - Each chunk is staged with `StageBlockAsync(blockId, data, transactional MD5)` on the **final** name. Uncommitted blocks are invisible, so nothing shows until commit.
  - Complete calls `CommitBlockListAsync`, adding the condition `IfNoneMatch = ETag.All` when `!overwrite`.
  - Abort does nothing.
- **Error mapping:**
  - 404 → NotFound.
  - 409 with code `ResourceAlreadyExists`/`BlobAlreadyExists`, or 412 → AlreadyExists.
  - 403, or `AuthenticationFailedException` → NotAuthorised: "The portal is not authorised on this storage account. Contact Advanced support."
  - Status 0, `HttpRequestException`, `SocketException`, or a timeout not caused by caller cancellation → Unreachable: "Storage is temporarily unreachable. Please try again shortly."
  - An MD5 mismatch (400 `Md5Mismatch`) → Conflict: "A piece of the upload was corrupted in transit and will be retried."
  - Anything else → Other: "Something went wrong talking to storage."
  - Exception text never appears in user messages.
- **`FakeStorageBackend`:**
  - An in-memory tree per target id that implements every method with the same semantics, including MD5 verification and the `.folder` hiding.
  - Two switches, `ThrowUnreachable` and `ThrowNotAuthorised`, throw the mapped exceptions.
  - It records calls so tests can assert "no bytes written".

- [ ] **Step 1: Write the failing tests.**
  - `StorageErrorMapperTests`:
    - one test per mapping rule, built from `new RequestFailedException(status, msg, errorCode, null)`
    - `Map_UserMessageNeverContainsExceptionText`
  - `StorageClientFactoryTests`:
    - `ServiceUri_GovSuffix_UsesSuffix` (`core.usgovcloudapi.net` → `https://acct.file.core.usgovcloudapi.net/`)
    - `BuildShareOptions_SetsBackupIntent`
  - `FakeStorageBackendTests`:
    - `WriteChunk_Md5Mismatch_ThrowsConflict`
    - `Complete_OverwriteFalse_ExistingFile_AlreadyExists`
    - `List_FolderMarkerHidden`
    - `Delete_Folder_Recursive`
    - `List_PageSizeOne_ReturnsContinuation`
  - `DevSignInGuardTests.AssertSafe_UseFakeStorageInProduction_Throws` (added to the Task 5 test file).
- [ ] **Step 2: Run them; expect FAIL.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them; expect PASS.**
- [ ] **Step 5: Add a live check.** Add `LiveStorageTests.Live_FilesAndBlob_RoundTrip` with `[Fact(Skip = "live: needs VPN + dev test account")]`. It runs list → create folder → 9 MiB+1 upload in 3 chunks → download → SHA-256 equal → move → delete, against both kinds, using targets read from env vars `CSSP_LIVE_FILES_ACCOUNT`/`CSSP_LIVE_SHARE` and `CSSP_LIVE_BLOB_ACCOUNT`/`CSSP_LIVE_CONTAINER`. Run it unskipped only once Task 13's account exists, and record the result in the task report.
- [ ] **Step 6: Commit:** "Add Azure Files and Blob backends over managed identity with error mapping".

---

