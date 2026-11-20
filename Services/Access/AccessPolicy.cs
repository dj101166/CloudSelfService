using CloudSelfService.Models;

namespace CloudSelfService.Services.Access;

public static class AccessPolicy
{
    public static AccessLevel EffectiveLevel(AppUser? user, Customer? customer, StorageTarget? target, Grant? grant)
    {
        if (user is null || !user.Active || target is null || !target.Active)
            return AccessLevel.None;

        if (user.Kind == UserKind.Staff)
        {
            return user.StaffRole switch
            {
                StaffRole.Staff => AccessLevel.Contribute,
                StaffRole.StaffAdmin or StaffRole.Owner => AccessLevel.Full,
                _ => AccessLevel.None,
            };
        }

        if (user.CustomerId is not { } customerId
            || customer is null || !customer.Active || customer.Id != customerId
            || target.CustomerId != customerId
            || grant is null || grant.CustomerId != customerId || grant.TargetId != target.Id)
            return AccessLevel.None;

        return grant.Level;
    }

    public static AccessLevel Required(StorageOperation op) => op switch
    {
        StorageOperation.List or StorageOperation.Download => AccessLevel.Read,
        StorageOperation.Upload or StorageOperation.Overwrite
            or StorageOperation.CreateFolder or StorageOperation.Move => AccessLevel.Contribute,
        _ => AccessLevel.Full,
    };

    public static bool Allows(AccessLevel have, StorageOperation op) => have >= Required(op);
}
