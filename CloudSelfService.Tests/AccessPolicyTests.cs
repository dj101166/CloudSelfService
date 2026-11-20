using System;
using CloudSelfService.Models;
using CloudSelfService.Services.Access;
using Xunit;

namespace CloudSelfService.Tests;

public class AccessPolicyTests
{
    private static readonly Guid CustA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CustB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid TargetA = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid TargetB = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

    private static AppUser Staff(StaffRole role, bool active = true) =>
        new(Guid.NewGuid(), UserKind.Staff, null, "s@advancedutility.com", "Staff", null, role, active, Now, null);

    private static AppUser CustomerUser(Guid? customerId, bool active = true) =>
        new(Guid.NewGuid(), UserKind.Customer, customerId, "c@client.com", "Cust", null, StaffRole.None, active, Now, null);

    private static Customer Cust(Guid id, bool active = true) =>
        new(id, "Cust", active, "", null, CustomerCloud.Commercial, CrossTenantStatus.NotRequired);

    private static StorageTarget Target(Guid id, Guid? customerId, bool active = true) =>
        new(id, customerId, "T", "sub", "rg", "acct", StorageKind.FileShare, "share", "core.windows.net", active);

    private static Grant GrantFor(Guid customerId, Guid targetId, AccessLevel level) =>
        new(customerId, targetId, level, "admin", Now);

    [Theory]
    [InlineData(StaffRole.None, AccessLevel.None)]
    [InlineData(StaffRole.Staff, AccessLevel.Contribute)]
    [InlineData(StaffRole.StaffAdmin, AccessLevel.Full)]
    [InlineData(StaffRole.Owner, AccessLevel.Full)]
    public void EffectiveLevel_StaffRole_MapsToLevel(StaffRole role, AccessLevel expected)
    {
        var level = AccessPolicy.EffectiveLevel(Staff(role), null, Target(TargetA, CustA), null);
        Assert.Equal(expected, level);
    }

    [Fact]
    public void EffectiveLevel_StaffRoleNone_None() =>
        Assert.Equal(AccessLevel.None, AccessPolicy.EffectiveLevel(Staff(StaffRole.None), null, Target(TargetA, null), null));

    [Theory]
    [InlineData(StaffRole.None, StorageOperation.List, false)]
    [InlineData(StaffRole.Staff, StorageOperation.List, true)]
    [InlineData(StaffRole.Staff, StorageOperation.Download, true)]
    [InlineData(StaffRole.Staff, StorageOperation.Upload, true)]
    [InlineData(StaffRole.Staff, StorageOperation.Overwrite, true)]
    [InlineData(StaffRole.Staff, StorageOperation.CreateFolder, true)]
    [InlineData(StaffRole.Staff, StorageOperation.Move, true)]
    [InlineData(StaffRole.Staff, StorageOperation.Delete, false)]
    [InlineData(StaffRole.StaffAdmin, StorageOperation.Delete, true)]
    [InlineData(StaffRole.Owner, StorageOperation.Delete, true)]
    [InlineData(StaffRole.Owner, StorageOperation.Move, true)]
    public void Allows_StaffRoleAndOperation_ExpectedOutcome(StaffRole role, StorageOperation op, bool expected)
    {
        var level = AccessPolicy.EffectiveLevel(Staff(role), null, Target(TargetA, null), null);
        Assert.Equal(expected, AccessPolicy.Allows(level, op));
    }

    [Theory]
    [InlineData(AccessLevel.None, StorageOperation.List, false)]
    [InlineData(AccessLevel.Read, StorageOperation.List, true)]
    [InlineData(AccessLevel.Read, StorageOperation.Download, true)]
    [InlineData(AccessLevel.Read, StorageOperation.Upload, false)]
    [InlineData(AccessLevel.Read, StorageOperation.Overwrite, false)]
    [InlineData(AccessLevel.Read, StorageOperation.CreateFolder, false)]
    [InlineData(AccessLevel.Read, StorageOperation.Move, false)]
    [InlineData(AccessLevel.Read, StorageOperation.Delete, false)]
    [InlineData(AccessLevel.Contribute, StorageOperation.Upload, true)]
    [InlineData(AccessLevel.Contribute, StorageOperation.Overwrite, true)]
    [InlineData(AccessLevel.Contribute, StorageOperation.CreateFolder, true)]
    [InlineData(AccessLevel.Contribute, StorageOperation.Move, true)]
    [InlineData(AccessLevel.Contribute, StorageOperation.Delete, false)]
    [InlineData(AccessLevel.Full, StorageOperation.Delete, true)]
    [InlineData(AccessLevel.Full, StorageOperation.List, true)]
    public void Allows_LevelAndOperation_ExpectedOutcome(AccessLevel level, StorageOperation op, bool expected) =>
        Assert.Equal(expected, AccessPolicy.Allows(level, op));

    [Fact]
    public void Required_UnknownOperation_IsFull()
    {
        Assert.Equal(AccessLevel.Full, AccessPolicy.Required((StorageOperation)99));
        Assert.False(AccessPolicy.Allows(AccessLevel.Contribute, (StorageOperation)99));
    }

    [Fact]
    public void EffectiveLevel_UnknownStaffRole_None() =>
        Assert.Equal(AccessLevel.None,
            AccessPolicy.EffectiveLevel(Staff((StaffRole)99), null, Target(TargetA, null), null));

    [Fact]
    public void Required_Delete_IsFull() =>
        Assert.Equal(AccessLevel.Full, AccessPolicy.Required(StorageOperation.Delete));

    [Theory]
    [InlineData(StorageOperation.List, AccessLevel.Read)]
    [InlineData(StorageOperation.Download, AccessLevel.Read)]
    [InlineData(StorageOperation.Upload, AccessLevel.Contribute)]
    [InlineData(StorageOperation.Overwrite, AccessLevel.Contribute)]
    [InlineData(StorageOperation.CreateFolder, AccessLevel.Contribute)]
    [InlineData(StorageOperation.Move, AccessLevel.Contribute)]
    [InlineData(StorageOperation.Delete, AccessLevel.Full)]
    public void Required_Operation_ReturnsMinimumLevel(StorageOperation op, AccessLevel expected) =>
        Assert.Equal(expected, AccessPolicy.Required(op));

    [Theory]
    [InlineData(AccessLevel.Read)]
    [InlineData(AccessLevel.Contribute)]
    [InlineData(AccessLevel.Full)]
    public void EffectiveLevel_CustomerWithMatchingGrant_ReturnsGrantLevel(AccessLevel granted)
    {
        var level = AccessPolicy.EffectiveLevel(CustomerUser(CustA), Cust(CustA), Target(TargetA, CustA), GrantFor(CustA, TargetA, granted));
        Assert.Equal(granted, level);
    }

    [Fact]
    public void EffectiveLevel_CustomerUserOtherCustomersTarget_None() =>
        Assert.Equal(AccessLevel.None,
            AccessPolicy.EffectiveLevel(CustomerUser(CustA), Cust(CustA), Target(TargetB, CustB), GrantFor(CustA, TargetB, AccessLevel.Full)));

    [Fact]
    public void EffectiveLevel_GrantForDifferentTarget_None() =>
        Assert.Equal(AccessLevel.None,
            AccessPolicy.EffectiveLevel(CustomerUser(CustA), Cust(CustA), Target(TargetA, CustA), GrantFor(CustA, TargetB, AccessLevel.Full)));

    [Fact]
    public void EffectiveLevel_GrantForDifferentCustomer_None() =>
        Assert.Equal(AccessLevel.None,
            AccessPolicy.EffectiveLevel(CustomerUser(CustA), Cust(CustA), Target(TargetA, CustA), GrantFor(CustB, TargetA, AccessLevel.Full)));

    [Fact]
    public void EffectiveLevel_InactiveCustomer_None() =>
        Assert.Equal(AccessLevel.None,
            AccessPolicy.EffectiveLevel(CustomerUser(CustA), Cust(CustA, active: false), Target(TargetA, CustA), GrantFor(CustA, TargetA, AccessLevel.Full)));

    [Fact]
    public void EffectiveLevel_InactiveTarget_None() =>
        Assert.Equal(AccessLevel.None,
            AccessPolicy.EffectiveLevel(CustomerUser(CustA), Cust(CustA), Target(TargetA, CustA, active: false), GrantFor(CustA, TargetA, AccessLevel.Full)));

    [Fact]
    public void EffectiveLevel_InactiveTargetForStaff_None() =>
        Assert.Equal(AccessLevel.None,
            AccessPolicy.EffectiveLevel(Staff(StaffRole.Owner), null, Target(TargetA, null, active: false), null));

    [Fact]
    public void EffectiveLevel_InactiveUser_None() =>
        Assert.Equal(AccessLevel.None,
            AccessPolicy.EffectiveLevel(Staff(StaffRole.Owner, active: false), null, Target(TargetA, null), null));

    [Fact]
    public void EffectiveLevel_NullUser_None() =>
        Assert.Equal(AccessLevel.None, AccessPolicy.EffectiveLevel(null, null, Target(TargetA, null), null));

    [Fact]
    public void EffectiveLevel_NullTarget_None() =>
        Assert.Equal(AccessLevel.None, AccessPolicy.EffectiveLevel(Staff(StaffRole.Owner), null, null, null));

    [Fact]
    public void EffectiveLevel_CustomerWithoutGrant_None() =>
        Assert.Equal(AccessLevel.None,
            AccessPolicy.EffectiveLevel(CustomerUser(CustA), Cust(CustA), Target(TargetA, CustA), null));

    [Fact]
    public void EffectiveLevel_CustomerUserWithStaffRoleOwner_NoGrant_None() =>
        Assert.Equal(AccessLevel.None,
            AccessPolicy.EffectiveLevel(CustomerUser(CustA) with { StaffRole = StaffRole.Owner }, Cust(CustA), Target(TargetA, CustA), null));

    [Fact]
    public void EffectiveLevel_CustomerUserWithoutCustomerId_None() =>
        Assert.Equal(AccessLevel.None,
            AccessPolicy.EffectiveLevel(CustomerUser(null), Cust(CustA), Target(TargetA, CustA), GrantFor(CustA, TargetA, AccessLevel.Full)));

    [Fact]
    public void EffectiveLevel_NullCustomerForCustomerUser_None() =>
        Assert.Equal(AccessLevel.None,
            AccessPolicy.EffectiveLevel(CustomerUser(CustA), null, Target(TargetA, CustA), GrantFor(CustA, TargetA, AccessLevel.Full)));

    [Fact]
    public void EffectiveLevel_CustomerRecordIdMismatch_None() =>
        Assert.Equal(AccessLevel.None,
            AccessPolicy.EffectiveLevel(CustomerUser(CustA), Cust(CustB), Target(TargetA, CustA), GrantFor(CustA, TargetA, AccessLevel.Full)));

    [Fact]
    public void EffectiveLevel_SharedTargetWithNullCustomerForCustomerUser_None() =>
        Assert.Equal(AccessLevel.None,
            AccessPolicy.EffectiveLevel(CustomerUser(CustA), Cust(CustA), Target(TargetA, null), GrantFor(CustA, TargetA, AccessLevel.Full)));
}
