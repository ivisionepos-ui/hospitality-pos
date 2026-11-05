namespace Pos.Domain.Staff;

/// <summary>Things a staff member may be allowed to do.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Permission is the business term.")]
public enum Permission
{
    /// <summary>Take orders, add items, send to the kitchen, complete orders.</summary>
    TakeOrders,

    /// <summary>Accept or reject website and delivery-app orders.</summary>
    AcceptOnlineOrders,

    /// <summary>Remove items not yet sent to the kitchen.</summary>
    VoidBeforeKitchen,

    /// <summary>Remove items already sent to the kitchen (or approve it).</summary>
    VoidAfterKitchen,

    /// <summary>Cancel orders (or approve it).</summary>
    CancelOrders,

    /// <summary>Approve discounts above the staff limit.</summary>
    ApproveDiscounts,

    /// <summary>Use Edit menu: names, prices, sold out, layout.</summary>
    EditMenu,

    /// <summary>Mark orders preparing and ready on the kitchen screen.</summary>
    KitchenDisplay,

    /// <summary>See reports and takings.</summary>
    ViewReports,

    /// <summary>Refund payments (or approve a refund).</summary>
    RefundPayments,

    /// <summary>Put cash into or take cash out of a till drawer outside a sale (or approve it).</summary>
    CashManagement,

    /// <summary>Add staff, change roles and PINs.</summary>
    ManageStaff,

    /// <summary>Register and remove tills and screens.</summary>
    ManageDevices,
}

/// <summary>A staff member's role. Each role has a fixed set of permissions.</summary>
public enum StaffRole
{
    /// <summary>Kitchen staff: kitchen screen only.</summary>
    Kitchen,

    /// <summary>Delivery driver: sees the deliveries they are taking; the shop sends orders out with them.</summary>
    Driver,

    /// <summary>Waiter: takes table orders.</summary>
    Waiter,

    /// <summary>Cashier: counter, phone and online orders.</summary>
    Cashier,

    /// <summary>Manager: everything in the shop, including approvals, menu and staff (not owners or devices).</summary>
    Manager,

    /// <summary>Owner: everything.</summary>
    Owner,
}

/// <summary>What each role may do.</summary>
public static class RolePermissions
{
    private static readonly Dictionary<StaffRole, HashSet<Permission>> Map = new()
    {
        [StaffRole.Kitchen] = [Permission.KitchenDisplay],
        [StaffRole.Driver] = [],
        [StaffRole.Waiter] = [Permission.TakeOrders, Permission.VoidBeforeKitchen],
        [StaffRole.Cashier] = [Permission.TakeOrders, Permission.VoidBeforeKitchen, Permission.AcceptOnlineOrders],
        [StaffRole.Manager] =
        [
            Permission.TakeOrders, Permission.VoidBeforeKitchen, Permission.AcceptOnlineOrders, Permission.VoidAfterKitchen,
            Permission.CancelOrders, Permission.ApproveDiscounts, Permission.EditMenu, Permission.KitchenDisplay,
            Permission.ViewReports, Permission.ManageStaff, Permission.RefundPayments, Permission.CashManagement,
        ],
        [StaffRole.Owner] = [.. Enum.GetValues<Permission>()],
    };

    /// <summary>True when the role includes the permission.</summary>
    public static bool Has(StaffRole role, Permission permission) => Map[role].Contains(permission);

    /// <summary>All permissions of a role.</summary>
    public static IReadOnlySet<Permission> For(StaffRole role) => Map[role];

    /// <summary>
    /// True when someone in <paramref name="actor"/> role may create or change a staff member with <paramref name="target"/> role.
    /// Owners manage everyone; managers manage only roles below manager.
    /// </summary>
    public static bool CanManage(StaffRole actor, StaffRole target) =>
        Has(actor, Permission.ManageStaff) && (actor == StaffRole.Owner || target < StaffRole.Manager);
}
