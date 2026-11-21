### Task 9: Chunked, resumable upload API

**Files:**
- Create: `Services/Transfers/UploadSessionService.cs`, `Services/Transfers/UploadCleanupService.cs` (`BackgroundService`), `Services/Data/UploadSessionStore.cs` (interface, PG implementation and in-memory implementation), `Endpoints/UploadEndpoints.cs`
- Modify: `Program.cs`, `Endpoints/AuthEndpoints.cs` (add `GET /api/antiforgery`)
- Test: `UploadSessionServiceTests.cs`, `UploadEndpointTests.cs`

**Interfaces:**
- Consumes: `StorageAccessService.AuthorizeAsync`, `ChunkMath`, `IStorageBackendResolver`, `AuditService`, `TestAppFactory`.
- Produces:

```csharp
public sealed record UploadSession(Guid Id, Guid UserId, Guid TargetId, string FinalPath, string StagingPath,
    long Size, int ChunkSize, bool Overwrite, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Status); // Status: Open|Completed|Aborted
public interface IUploadSessionStore {
    Task CreateAsync(UploadSession s, CancellationToken ct = default);
    Task<UploadSession?> GetAsync(Guid id, CancellationToken ct = default);
    Task MarkChunkAsync(Guid id, int index, CancellationToken ct = default);          // idempotent
    Task<IReadOnlyList<int>> ReceivedChunksAsync(Guid id, CancellationToken ct = default);
    Task SetStatusAsync(Guid id, string status, CancellationToken ct = default);
    Task<IReadOnlyList<UploadSession>> ListExpiredOpenAsync(DateTimeOffset now, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
public sealed record BeginUploadRequest(Guid TargetId, string Folder, string FileName, long Size, bool Overwrite);
public sealed record BeginUploadResponse(Guid SessionId, int ChunkSize, int ChunkCount, int ParallelChunks, IReadOnlyList<int> Received);
public sealed class UploadSessionService {
    public Task<AccessResult<BeginUploadResponse>> BeginAsync(Guid userId, BeginUploadRequest req, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult<IReadOnlyList<int>>> StatusAsync(Guid userId, Guid sessionId, CancellationToken ct = default);
    public Task<AccessResult> PutChunkAsync(Guid userId, Guid sessionId, int index, Stream body, long? contentLength, string? contentMd5Base64, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult> CompleteAsync(Guid userId, Guid sessionId, AuditContext ctx, CancellationToken ct = default);
    public Task<AccessResult> AbortAsync(Guid userId, Guid sessionId, AuditContext ctx, CancellationToken ct = default);
}
```

**Endpoints.** All require authorization and validate the antiforgery token (sent in the `RequestVerificationToken` header, fetched from `GET /api/antiforgery`). All return JSON. Status codes map as in Task 8, plus `AlreadyExists`→409.
- `POST /api/uploads` takes a `BeginUploadRequest` body. Returns 200 with a `BeginUploadResponse`.
- `GET /api/uploads/{id:guid}` returns `{ received: int[] }`.
- `PUT /api/uploads/{id:guid}/chunks/{index:int}` takes a raw body and a `Content-MD5` header. Returns 204.
- `POST /api/uploads/{id:guid}/complete` returns 204.
- `DELETE /api/uploads/{id:guid}` returns 204.
- `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize` is set to `ChunkSize` on the chunk route only. The Kestrel default stays in force everywhere else.

**Rules the tests pin:**
- **Begin:**
  - Validates `FileName` with `TryValidateName` and `Folder` with `TryNormalizePath`.
  - `Size` must be ≥ 0 and ≤ `Transfers:MaxUploadBytes`; otherwise `Invalid`, with the message "File is larger than the {N} GB limit."
  - The final path must not exist unless `Overwrite` is true; otherwise `AlreadyExists`.
  - The operation checked is `Upload`, or `Overwrite` when the target exists and `Overwrite` is true.
  - Files staging path: `{folder}/.{name}.cssupload-{sessionId:N}`. Blob: the staging path equals the final path.
  - Calls `BeginUploadAsync` and returns `ParallelChunks` from config.
  - **Resume lookup.** First look for an open, unexpired session owned by the same user with the same target, final path and size. If one exists, return it with its `Received` list instead of creating a new one. That lookup is what makes a page reload resumable.
- **PutChunk:**
  - Loads the session. It must belong to `userId`, be `Open` and be unexpired; otherwise `NotFound`.
  - **Calls `AuthorizeAsync` again on every chunk** (Review Focus 2).
  - `index` must be in range, `contentLength` must equal `ChunkMath.ExpectedLength`, and `Content-MD5` must be present as 16 bytes of base64. Any failure is `Invalid`.
  - Writes through the backend at `offset = index * ChunkSize` with `BlockId(sessionId, index)`, then calls `MarkChunkAsync`.
  - Chunks are not audited individually.
- **Complete:**
  - Every index in `0..ChunkCount-1` must be received; otherwise `Invalid` ("Upload is missing N pieces.").
  - Re-authorises, calls `CompleteUploadAsync` with block ids in index order, and sets the status to `Completed`.
  - Audits one `Upload` row with `Bytes = Size`.
  - A zero-byte file has `ChunkCount` 0 and completes immediately (Review Focus 1). For blobs, commit an empty block list.
- **Abort:** calls `AbortUploadAsync`, sets the status to `Aborted`, and audits `Upload` as `Failed` with the error "Cancelled by user".
- **`UploadCleanupService`:**
  - Every 30 minutes, for `ListExpiredOpenAsync(now)`: call `AbortUploadAsync` (ignoring errors), set the status to `Aborted`, then delete the session rows 7 days after expiry.
  - Expose `internal Task SweepAsync(DateTimeOffset now, CancellationToken ct)` for tests.

- [ ] **Step 1: Write the failing tests.**

`UploadSessionServiceTests` uses in-memory stores and the fake backend:
- `Begin_ReadUser_Denied`
- `Begin_TooLarge_Invalid`
- `Begin_ExistingFileNoOverwrite_AlreadyExists`
- `Begin_ExistingFileOverwrite_RequiresContribute_Ok`
- `Begin_SameFileAgain_ReturnsSameSessionWithReceived`
- `PutChunk_WrongLength_Invalid`
- `PutChunk_MissingMd5_Invalid`
- `PutChunk_BadMd5_UnavailableAndChunkNotMarked`
- `PutChunk_OtherUsersSession_NotFound`
- `PutChunk_GrantRevokedAfterBegin_Denied` (Review Focus 2). Assert the fake backend recorded no write.
- `PutChunk_SameIndexTwice_Idempotent`
- `Complete_MissingChunk_Invalid`
- `Complete_AllChunks_FileExistsWithExactBytes`: 9 MiB + 1 byte, 3 chunks sent out of order (2, 0, 1); assert the SHA-256 matches.
- `Complete_ZeroByteFile_CreatesEmptyFile` (Review Focus 1)
- `Complete_AuditsSingleUploadRow`
- `Abort_RemovesStaging_AuditsFailed`
- `Sweep_ExpiredOpenSession_Aborted`

`UploadEndpointTests` goes through `TestAppFactory`:
- `Upload_EndToEnd_CustomerContribute_Succeeds`
- `PutChunk_WithoutAntiforgery_400`
- `PutChunk_BodyLargerThanChunk_413`
- `Begin_Anonymous_NotAuthorised`
- [ ] **Step 2: Run them. Expect FAIL.**
- [ ] **Step 3: Implement the services, stores, endpoints and background service.**
- [ ] **Step 4: Run them. Expect PASS.**
- [ ] **Step 5: Invoke `security-auditor`** on the upload surface (antiforgery, body limits, session ownership, path handling) and fix its findings. Then invoke `unit-test-reviewer` on `Services/Transfers/`.
- [ ] **Step 6: Commit:** "Add chunked, resumable, MD5-verified upload API with expiry cleanup".

---

