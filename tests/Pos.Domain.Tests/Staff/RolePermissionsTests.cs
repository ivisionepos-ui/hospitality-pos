using Pos.Domain.Staff;

namespace Pos.Domain.Tests.Staff;

public class RolePermissionsTests
{
    [Theory]
    [InlineData(StaffRole.Kitchen, Permission.KitchenDisplay, true)]
    [InlineData(StaffRole.Kitchen, Permission.TakeOrders, false)]
    [InlineData(StaffRole.Waiter, Permission.TakeOrders, true)]
    [InlineData(StaffRole.Waiter, Permission.AcceptOnlineOrders, false)]
    [InlineData(StaffRole.Cashier, Permission.AcceptOnlineOrders, true)]
    [InlineData(StaffRole.Cashier, Permission.VoidAfterKitchen, false)]
    [InlineData(StaffRole.Cashier, Permission.EditMenu, false)]
    [InlineData(StaffRole.Manager, Permission.VoidAfterKitchen, true)]
    [InlineData(StaffRole.Manager, Permission.EditMenu, true)]
    [InlineData(StaffRole.Manager, Permission.ManageDevices, false)]
    [InlineData(StaffRole.Owner, Permission.ManageDevices, true)]
    public void Roles_have_the_right_permissions(StaffRole role, Permission permission, bool expected)
    {
        Assert.Equal(expected, RolePermissions.Has(role, permission));
    }

    [Fact]
    public void Owner_has_every_permission()
    {
        Assert.All(Enum.GetValues<Permission>(), p => Assert.True(RolePermissions.Has(StaffRole.Owner, p)));
    }

    [Theory]
    [InlineData(StaffRole.Owner, StaffRole.Owner, true)]
    [InlineData(StaffRole.Owner, StaffRole.Manager, true)]
    [InlineData(StaffRole.Manager, StaffRole.Cashier, true)]
    [InlineData(StaffRole.Manager, StaffRole.Manager, false)]
    [InlineData(StaffRole.Manager, StaffRole.Owner, false)]
    [InlineData(StaffRole.Cashier, StaffRole.Waiter, false)]
    public void Who_may_manage_whom(StaffRole actor, StaffRole target, bool expected)
    {
        Assert.Equal(expected, RolePermissions.CanManage(actor, target));
    }
}
