# Task 6 report: Storage backends (Files + Blob)

Status: DONE_WITH_CONCERNS. Commit on feat/private-storage-access (see git log, subject "Add Azure Files and Blob backends over managed identity with error mapping").

## What was built
- `Services/Storage/`: IStorageBackend (+ IStorageBackendResolver, StorageBackendResolver by Kind, FixedBackendResolver), StorageClientFactory, FileShareBackend, BlobBackend, StorageErrorMapper (+ StorageFailureKind/Exception, `RunAsync` wrapper that lets caller cancellation through), FakeStorageBackend, StoragePaths (+ RecursiveDelete helper).
- Packages: Azure.Identity 1.21.0, Azure.Storage.Blobs 12.30.1, Azure.Storage.Files.Shares 12.28.0 (restore OK).
- Program.cs: when `Storage:UseFake=true` AND Development, FakeStorageBackend is a singleton and is the `IStorageBackendResolver` for every kind; otherwise real factory/backends/resolver.
- DevSignIn.AssertSafe throws `Fake storage cannot run outside Development`; new `DevSignIn.UseFakeStorage(config)`.
- Seeding coupling chosen (simplest): the fake lazily creates an empty tree per target id; `/dev/signin` customer-persona seeding calls `FakeStorageBackend.Seed(target.Id)` (resolved with `services.GetService`, null when the fake is not registered). Seed adds Documents, Reports/2026, Archive plus three files; idempotent.

## Tests
- New: StorageErrorMapperTests, StorageClientFactoryTests, FakeStorageBackendTests, StoragePathsTests (prefix, marker filtering, rebase, recursive delete ordering), LiveStorageTests (skipped), DevSignInGuardTests additions (`AssertSafe_UseFakeStorageInProduction_Throws` + Development-allowed case).
- Result: 326 passed, 0 failed, 6 skipped (was 268/5). `dotnet build` 0 warnings, 0 errors.
- TDD: RED = tests written first, build failed (CS0246 missing FileShareBackend/BlobBackend/Fake types, i.e. compile-level RED); GREEN after implementation.
- Live check: deferred to Task 13 (no account yet; no az commands or Azure resources used).

## SDK deviations from the brief
- `ShareTokenIntent` lives in `Azure.Storage.Files.Shares.Models` (needs that using).
- `ShareClient` has no `GetFileClient`; files are reached via `share.GetRootDirectoryClient().GetFileClient(path)`.
- `GetBlobsAsync` has no `prefix:` named-overload without traits: used `(BlobTraits.None, BlobStates.None, prefix, ct)`. `GetBlobsByHierarchyAsync(BlobTraits, BlobStates, delimiter, prefix, ct)` used positionally.
- Blob stage-block MD5: `BlockBlobStageBlockOptions.TransferValidation = new UploadTransferValidationOptions { ChecksumAlgorithm = MD5, PrecalculatedChecksum = md5 }`.
- `ShareFileUploadRangeOptions.TransactionalContentHash`, `RenameAsync` + `ShareFileRenameOptions.ReplaceIfExists`, `SyncCopyFromUriAsync` + `BlobCopyFromUriOptions.SourceAuthentication = HttpAuthorization("Bearer", token)` all exist as in the brief (compile verified; not exercised live).

## Self-review / concerns
- Unverifiable without Azure: Files RenameAsync semantic details (e.g. parent must exist; I create ancestors first), the 400 Md5Mismatch code from the real services, and the bearer-token source auth on same-account sync copy.
- Blob move for blobs over 256 MiB uses `StartCopyFromUriAsync` with NO source authentication option (the async copy API cannot take a bearer header for same-account sources without a SAS, which is forbidden). Large (>256 MiB) blob moves will likely fail with 403 -> mapped NotAuthorised until verified in Task 13; alternative is chunked server-side `StageBlockFromUri` with SourceAuthentication.
- Blob folder move is copy-then-delete per blob (not atomic); partial failure can leave blobs in both places.
- Blob `CreateFolder` checks existence via listing then writes marker `.folder` with overwrite=false.
- Files `ListAsync` does not populate LastModified (the list API does not return it cheaply); GetItem does.
- Fake `Move` of files does not verify parent; `Seed` is the only seeding hook.

## Fix round 1
Changes:
- (C1) BlobBackend: blobs >256 MiB copied via `StageBlockFromUriAsync` (SourceAuthentication bearer, SourceRange HttpRange) in blocks from `BlockPlanner.Plan` (<=4000 MiB, fixed-width ids), then `CommitBlockListAsync` with IfNoneMatch=ETag.All, then source delete. <=256 MiB keeps SyncCopyFromUri. Note: SDK arg order is (sourceUri, base64BlockId).
- (I2) Removed backend-invented `.staging/`; stagingPath is used as given. `StoragePaths.IsStagingName` (`^\..+\.cssupload-[0-9a-f]{32}$`) / `IsHiddenName` applied in List and GetItem of Files, Blob and Fake. Fake keeps staging files in-tree (hidden), removed on complete/abort.
- (I3) Blob list of missing folder (empty first page, no marker) -> NotFound.
- (I4) OpenRead omits Range when offset==0 && length==null; length 0 returns empty stream without a call (Files, Blob). New fake test OpenRead_ZeroByteFile_EmptyStream.
- (I5a) `IsMoveIntoItself` guard -> Conflict "Can't move a folder into itself." in Files (before service call), Blob, Fake. (I5b) Chose per-blob resumability: dropped up-front destination-exists check for folders; per blob, if destination exists with same length the source is just deleted, different length -> AlreadyExists. Single-file moves keep strict no-overwrite.
- (I6) Bearer obtained per copy / per staged block.
- Minors: Files list requests ShareFileTraits.Timestamps and fills LastModified (from `Properties.LastModified`; relies on SDK/service returning it); OperationCanceledException (non-caller) -> Unreachable; Files hides `.folder`; Fake CreateFolder/Seed write the marker; fake short stream -> Conflict "Upload piece was shorter than expected."; no int casts for offsets (checked cast only at MemoryStream boundary); `StoragePaths.SortPage` folders-first, ordinal-ignore-case, per page only (documented).
Tests: BlockPlanner (0, 1, 4000 MiB, +1, 10 GiB), staging predicate, move-into-itself, SortPage, fake zero-byte/into-itself/staging-hidden/short-stream/ordering, mapper cancellation. Suite: 349 passed, 0 failed, 6 skipped; build 0 warnings 0 errors.
Concerns: still not exercised against real Azure (Task 13). Fake in-tree staging path is whatever the caller passes; one existing fake test had to use a pattern-matching staging name.

## Fix round 2
- R21: BlobBackend stamps `cssp_move_src` (URL-escaped source path, plus the source's own metadata) on every blob it writes (sync copy and staged commit). Existing destination: skip copy and delete source only if tag == this source path AND length matches; otherwise AlreadyExists, source kept. Single-file moves never overwrite. Folder move where the destination path is a file blob -> AlreadyExists. Source-delete 404 on resume = success.
- Fake mirrors this with a per-entry moved-from tag; added test hook `FailFolderMoveAfterFiles`. New tests: MoveFolder_OntoExistingFolderWithSameSizeDifferentFile_AlreadyExists_SourceKept, MoveFolder_ResumeAfterPartialMove_Completes, MoveFolder_DestinationIsFile_AlreadyExists.
- Files: comment that renames are atomic per call (no resume marker).
- Minors: staging regex uses `\z`; Blob/Files GetItem hide `.folder`; Blob empty first page with continuation token is followed before deciding NotFound (an empty page then returns an empty listing rather than NotFound if the following page is also empty; rare edge).
- Suite: 352 passed, 0 failed, 6 skipped; build 0/0.

## Fix round 3
- Tag bound to the source instance: `cssp_move_src` = `EncodeMoveSource(from) + "|" + escaped source ETag` (quotes stripped). Resume (folder moves only) treats a destination as already copied only if its tag equals the tag computed from the LIVE source ETag and lengths match; a recreated or modified source has a new ETag, so it gets AlreadyExists and the source is kept.
- Source ETag is also sent as IfMatch on the copy source (sync copy, each StageBlockFromUri) and on the source delete, so a source changed mid-move is not copied torn or deleted.
- After the source delete, best-effort `SetMetadataAsync` clears the tag (ignores RequestFailedException).
- Single-file moves never take the resume path: existing destination -> AlreadyExists.
- Tags over 4096 chars are not stamped (blob just not resumable); the move never fails over it.
- Fake: MovedFrom stores (source path, source version); every write/move bumps a per-path version; entry cleared on completion. New tests: MoveFile_SecondMoveOfRecreatedSameSizeSource_AlreadyExists_SourceKept, MoveFolder_SecondMoveOfRecreatedSameSizeSource_AlreadyExists_SourceKept; MoveFolder_ResumeAfterPartialMove_Completes still passes. StoragePaths.MoveTag unit test added.
- Suite: 355 passed, 0 failed, 6 skipped; build 0/0.
- Unverified live: ETag "H" formatting round trip and IfMatch on SyncCopyFromUri SourceConditions (SDK-supported, not exercised against Azure).
