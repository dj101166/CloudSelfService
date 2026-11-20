namespace CloudSelfService.Models;

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
