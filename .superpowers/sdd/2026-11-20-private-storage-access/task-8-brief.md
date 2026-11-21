### Task 8: Download endpoint

**Files:**
- Create: `Endpoints/DownloadEndpoints.cs`, `CloudSelfService.Tests/Infrastructure/TestAppFactory.cs`
- Modify: `Program.cs` (call `app.MapDownloadEndpoints()`), `CloudSelfService.Tests.csproj` (add `Microsoft.AspNetCore.Mvc.Testing`)
- Test: `DownloadEndpointTests.cs`

**Interfaces:**
- Consumes: `StorageAccessService.AuthorizeDownloadAsync`, `IStorageBackendResolver`, `RangeHeader`, `ContentDispositionHeader`.
- Produces:
  - `GET /api/files/{targetId:guid}/download?path=…`, which requires authorization.
  - `TestAppFactory : WebApplicationFactory<Program>`. It runs in the Development environment with `Auth:Provider=Dev` and the in-memory stores, swaps `FakeStorageBackend` in as the backend, and exposes `CreateClientAs(string devPrincipal)`. That helper calls `/dev/signin?as=…` and keeps the cookie. Task 9 reuses it.

**Behaviour:**
- `AuditContext.SourceIp` is `HttpContext.Connection.RemoteIpAddress`. `CorrelationId` is `HttpContext.TraceIdentifier`.
- Status codes: `Denied`→403, `NotFound`→404, `Invalid`→400, `Unavailable`→503. Each returns plain text with the message. A folder path returns 400.
- **Success headers:**
  - `Content-Type: application/octet-stream`
  - `Content-Disposition` from `ContentDispositionHeader.Attachment(Leaf(path))`
  - `X-Content-Type-Options: nosniff`
  - `Accept-Ranges: bytes`
  - `Content-Length`
- **Range:** a valid single `Range` returns 206 with `Content-Range: bytes start-end/total`. A syntactically valid range that can't be satisfied returns 416 with `Content-Range: bytes */total`. An unparseable or multi-range header is ignored, and the full file is sent with 200.
- **Streaming:** copy the stream from `OpenReadAsync` to `Response.Body` in 80 KiB chunks. The file is never buffered whole.
- **Audit:** write one `Download` row with `Success` once the copy finishes, or `Failed` if the client disconnects mid-stream (operation cancelled). `Bytes` is the number actually written.

- [ ] **Step 1: Write the failing tests.**
- `Download_CustomerOwnFile_200_AttachmentOctetStream`
- `Download_OtherCustomersTarget_403`
- `Download_Anonymous_RedirectsOrUnauthorized`: this asserts no 200 and no file bytes.
- `Download_Range_206_WithContentRange`: with a 1000-byte file and `bytes=100-199`, expect 100 bytes and `Content-Range: bytes 100-199/1000`.
- `Download_RangeBeyondEnd_416`
- `Download_NameWithSpecialCharacters_HeaderEncoded` (Review Focus 3): the file is `"Q3 #1 50% a+b & c's Ünï.txt"`, and the header contains a `filename*=UTF-8''` value that decodes back to the same name.
- `Download_Folder_400`
- `Download_BackendUnreachable_503_ReadableMessage`
- [ ] **Step 2: Run them. Expect FAIL.**
- [ ] **Step 3: Implement `DownloadEndpoints` and `TestAppFactory`.**
- [ ] **Step 4: Run them. Expect PASS.**
- [ ] **Step 5: Invoke `security-auditor`** on the endpoint and fix what it finds.
- [ ] **Step 6: Commit:** "Add streaming, range-capable download endpoint".

---

