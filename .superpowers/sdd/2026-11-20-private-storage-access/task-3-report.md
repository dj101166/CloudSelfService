# Task 3 report

Status: DONE. Commit feb584e "Add domain model and pure access, path, chunk and header helpers".

## Implemented
Models/Enums.cs, Models/Records.cs, Services/Access/{AccessPolicy,StoragePath}.cs, Services/Transfers/{ChunkMath,RangeHeader,ContentDispositionHeader}.cs, with signatures exactly as in the brief.

Rulings applied:
- Combine("", n) returns n. Parent("a"), Parent("") and Leaf("") return "".
- TryNormalizePath(null or whitespace) succeeds with "".
- Staff with StaffRole.None get None. StaffRole is ignored for customers.
- Content-Disposition fallback: non-ASCII and control characters become `_`; `"` and `\` are stripped. filename* literals are only A-Z a-z 0-9 - . _ ~, everything else is %XX uppercase.

Choices beyond the brief:
- ChunkMath.ExpectedLength throws ArgumentOutOfRangeException for index out of range, including any index when size is 0. ChunkCount throws for negative size.
- RangeHeader: the end is clamped to length-1; a suffix larger than the length gives the whole file; `bytes=-0`, malformed input, end < start and length <= 0 return false; the `bytes=` prefix is case-insensitive.

## Tests
5 new test files, about 100 test cases. Full suite: 156 passed, 0 failed.

TDD evidence:
- RED: `dotnet test CloudSelfService.Tests/CloudSelfService.Tests.csproj` gave 99 `error CS0234` (namespaces Models, Services.Access, Services.Transfers do not exist).
- GREEN: same command after implementation: `Passed! - Failed: 0, Passed: 156, Total: 156`.

The Task 2 static guards stayed green.

## Files changed
12 files, 753 insertions, all new (listed above plus the 5 test files).

## Self-review
- Diff matches the brief; no stray files staged. `.superpowers/` is untracked and was not committed.
- No Co-Authored-By trailer.
- The only comment is the Azure block-ID same-length note, whose WHY is non-obvious.

## Concerns
- Bash heredocs with `''` broke on the first two attempts (nothing ran), so the files were written with the Write tool instead. No effect on results.
- AccessPolicy.Required maps unknown enum values to Full (fail closed).
