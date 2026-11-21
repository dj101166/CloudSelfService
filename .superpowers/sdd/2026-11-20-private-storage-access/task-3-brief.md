### Task 3: Domain models and pure access helpers

**Files:**
- Create: `Models/Enums.cs`, `Models/Records.cs`, `Services/Access/AccessPolicy.cs`, `Services/Access/StoragePath.cs`, `Services/Transfers/ChunkMath.cs`, `Services/Transfers/RangeHeader.cs`, `Services/Transfers/ContentDispositionHeader.cs`
- Test: `AccessPolicyTests.cs`, `StoragePathTests.cs`, `ChunkMathTests.cs`, `RangeHeaderTests.cs`, `ContentDispositionHeaderTests.cs`

**Interfaces (produced, used by every later task):**

```csharp
public enum UserKind { Staff, Customer }
public enum StaffRole { None = 0, Staff = 1, StaffAdmin = 2, Owner = 3 }
public enum AccessLevel { None = 0, Read = 1, Contribute = 2, Full = 3 }
public enum StorageKind { FileShare, BlobContainer }
public enum CustomerCloud { Commercial, AzureGov }
public enum CrossTenantStatus { NotRequired, Pending, Complete }
public enum StorageOperation { List, Download, Upload, Overwrite, CreateFolder, Move, Delete }
public enum AuditResult { Success, Denied, Failed }

public sealed record AppUser(Guid Id, UserKind Kind, Guid? CustomerId, string Upn, string DisplayName,
    string? EntraObjectId, StaffRole StaffRole, bool Active, DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt);
public sealed record Customer(Guid Id, string Name, bool Active, string Notes, string? HomeTenantId,
    CustomerCloud Cloud, CrossTenantStatus CrossTenantStatus);
public sealed record StorageTarget(Guid Id, Guid? CustomerId, string DisplayName, string SubscriptionId,
    string ResourceGroup, string AccountName, StorageKind Kind, string ContainerName, string EndpointSuffix, bool Active);
public sealed record Grant(Guid CustomerId, Guid TargetId, AccessLevel Level, string GrantedBy, DateTimeOffset GrantedAt);
public sealed record AuditEvent(Guid Id, DateTimeOffset At, Guid? UserId, string UserDisplay, Guid? CustomerId,
    Guid? TargetId, string Operation, string Path, long? Bytes, AuditResult Result, string Error, string SourceIp, string CorrelationId);
public sealed record StorageItem(string Name, string Path, bool IsFolder, long? Size, DateTimeOffset? LastModified);
public sealed record StorageListing(IReadOnlyList<StorageItem> Items, string? ContinuationToken);
public sealed record AuditContext(string SourceIp, string CorrelationId);

public static class AccessPolicy {
    public static AccessLevel EffectiveLevel(AppUser? user, Customer? customer, StorageTarget? target, Grant? grant);
    public static AccessLevel Required(StorageOperation op);
    public static bool Allows(AccessLevel have, StorageOperation op) => have >= Required(op);
}
public static class StoragePath {
    public static bool TryNormalizePath(string? raw, out string normalized, out string? error); // "" = root
    public static bool TryValidateName(string? name, out string? error);                      // one segment
    public static string Combine(string folder, string name);
    public static string Parent(string path);
    public static string Leaf(string path);
}
public static class ChunkMath {
    public const int ChunkSize = 4 * 1024 * 1024;
    public static int ChunkCount(long size);                      // 0 for size 0
    public static long ExpectedLength(long size, int index);      // throws on bad index
    public static string BlockId(Guid sessionId, int index);      // base64 of $"{sessionId:N}-{index:D6}", fixed width
}
public static class RangeHeader { public static bool TryParse(string? header, long length, out long start, out long end); } // single range only
public static class ContentDispositionHeader { public static string Attachment(string fileName); }
```

**Rules the tests pin:**
- `EffectiveLevel` returns `None` when:
  - the user is null or inactive;
  - the target is null or inactive.
- Staff map by role: `None`→None, `Staff`→Contribute, `StaffAdmin` and `Owner`→Full. Staff access ignores customers and grants.
- A customer user gets the grant's level only when all of these hold:
  - the user has a `CustomerId`;
  - the customer is non-null, active, and has that Id;
  - `target.CustomerId == user.CustomerId`;
  - `grant.CustomerId == user.CustomerId` and `grant.TargetId == target.Id`.
- Anything else gives `None`.
- `Required`: List and Download → Read; Upload, Overwrite, CreateFolder and Move → Contribute; Delete → Full.
- `TryNormalizePath`:
  - Trims leading and trailing `/` and collapses `//`.
  - Rejects `\`, `.` or `..` segments, control characters (U+0000–U+001F, U+007F), the characters `" : | < > * ?`, a segment ending in `.` or a space, a segment longer than 255 characters, and a total longer than 1,024 characters.
  - Accepts spaces, `#`, `%`, `+`, `&`, `'` and non-ASCII characters.
- `TryValidateName` applies the same rules to a single segment and also rejects `/` and an empty name.
- `Attachment(name)` returns `attachment; filename="<ASCII fallback, non-ASCII→_ and quotes stripped>"; filename*=UTF-8''<RFC 5987 percent-encoded>`.

- [ ] **Step 1: Write the failing tests.** At minimum these, each with concrete values:
  - **AccessPolicy:** a role×level×op theory covering every `StorageOperation` for each StaffRole and AccessLevel, plus:
    - `EffectiveLevel_CustomerUserOtherCustomersTarget_None`
    - `EffectiveLevel_GrantForDifferentTarget_None`
    - `EffectiveLevel_InactiveCustomer_None`
    - `EffectiveLevel_InactiveTarget_None`
    - `EffectiveLevel_StaffRoleNone_None`
    - `Required_Delete_IsFull`
  - **StoragePath:**
    - `TryNormalizePath_DotDot_Rejected` with inputs `"../x"`, `"a/../../b"`, `"a/./b"`
    - `TryNormalizePath_Backslash_Rejected` with `"a\\b"`
    - `TryNormalizePath_LeadingTrailingSlashes_Trimmed` (`"/a//b/"` → `"a/b"`)
    - `TryNormalizePath_SpecialCharacters_Accepted` (`"Q3 #1 50% a+b & c's/Ünïcode.txt"` unchanged)
    - `TryNormalizePath_ControlCharacter_Rejected` (`"a\u0001b"`)
    - `TryNormalizePath_TrailingDotSegment_Rejected` (`"a./b"`)
    - `TryValidateName_ContainsSlash_Rejected`
    - `Parent_RootChild_IsEmpty`
  - **ChunkMath:**
    - `ChunkCount_Zero_IsZero`
    - `ChunkCount_ExactMultiple` (8 MiB → 2)
    - `ChunkCount_Remainder` (8 MiB + 1 → 3)
    - `ExpectedLength_LastChunk_IsRemainder` (size 8 MiB+1, index 2 → 1)
    - `ExpectedLength_IndexOutOfRange_Throws`
    - `BlockId_AllIndicesSameLength` (indices 0 and 999,999)
  - **RangeHeader:**
    - `TryParse_BytesStartEnd` (`"bytes=0-99"`, len 1000 → 0, 99)
    - `TryParse_OpenEnded` (`"bytes=500-"` → 500, 999)
    - `TryParse_Suffix` (`"bytes=-100"` → 900, 999)
    - `TryParse_MultiRange_False`
    - `TryParse_StartBeyondLength_False`
    - `TryParse_Null_False`
  - **ContentDispositionHeader:**
    - `Attachment_NonAscii_HasFilenameStar` (`"Ünï #1.txt"` → contains `filename*=UTF-8''%C3%9Cn%C3%AF%20%231.txt`)
    - `Attachment_QuoteInName_Stripped`
- [ ] **Step 2: Run the tests and expect FAIL.** They won't compile, because the types don't exist yet.
- [ ] **Step 3: Implement the types above** in the listed files.
- [ ] **Step 4: Run the tests and expect PASS.**
- [ ] **Step 5: Commit:** "Add domain model and pure access, path, chunk and header helpers".

---

