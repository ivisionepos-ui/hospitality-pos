using System.Net;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Staff;
using Pos.Hub.Api;

namespace Pos.Hub.Security;

/// <summary>
/// Checks every protected request: the device must be registered (X-Device-Id and X-Device-Key headers),
/// and for anything that changes data a staff member must be logged in on that device
/// (Authorization: Bearer token) with the right permission.
/// </summary>
public sealed class AccessFilter(bool staffRequired, Permission? permission) : IEndpointFilter
{
    private const string CallerKey = "pos.caller";

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var access = http.RequestServices.GetRequiredService<AccessService>();
        var headers = http.Request.Headers;

        if (!Guid.TryParse(headers["X-Device-Id"], out var deviceId)
            || access.AuthenticateDevice(deviceId, headers["X-Device-Key"]) is not { } device)
        {
            return Deny("DeviceNotRegistered", "This device is not registered with the hub.", StatusCodes.Status401Unauthorized);
        }

        StaffMember? staff = null;
        var authorization = headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            staff = access.AuthenticateSession(deviceId, authorization["Bearer ".Length..].Trim());
        }

        if ((staffRequired || permission is not null) && staff is null)
        {
            return Deny("NotLoggedIn", "Please log in with your PIN.", StatusCodes.Status401Unauthorized);
        }

        if (permission is { } needed && !staff!.Can(needed))
        {
            return Deny("Forbidden", $"Your role ({staff.Role}) cannot do this.", StatusCodes.Status403Forbidden);
        }

        http.Items[CallerKey] = new HubCaller(device, staff);
        return await next(context);
    }

    /// <summary>The caller checked by the filter for this request.</summary>
    public static HubCaller Caller(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Items[CallerKey] as HubCaller ?? throw new InvalidOperationException("Route is missing its access check.");
    }

    /// <summary>The logged-in staff member as the actor on an order.</summary>
    public static OrderActor Actor(HttpContext http)
    {
        var caller = Caller(http);
        return new OrderActor(caller.Staff?.Id ?? throw new AccessDeniedException("NotLoggedIn", "Please log in with your PIN.", 401), caller.Device.Id);
    }

    /// <summary>Throws unless the logged-in staff member has the permission.</summary>
    public static StaffMember Require(HttpContext http, Permission permission)
    {
        var staff = Caller(http).Staff ?? throw new AccessDeniedException("NotLoggedIn", "Please log in with your PIN.", 401);
        return staff.Can(permission) ? staff : throw new AccessDeniedException("Forbidden", $"Your role ({staff.Role}) cannot do this.", 403);
    }

    /// <summary>Throws unless the logged-in staff member has at least one of the permissions.</summary>
    public static StaffMember RequireAny(HttpContext http, params Permission[] permissions)
    {
        var staff = Caller(http).Staff ?? throw new AccessDeniedException("NotLoggedIn", "Please log in with your PIN.", 401);
        return permissions.Any(staff.Can) ? staff : throw new AccessDeniedException("Forbidden", $"Your role ({staff.Role}) cannot do this.", 403);
    }

    /// <summary>
    /// Who approves an action that needs a manager: the logged-in staff member if their role allows it,
    /// otherwise the manager whose PIN is in the X-Manager-Pin header. Null if neither.
    /// </summary>
    public static Guid? Approver(HttpContext http, Permission permission)
    {
        ArgumentNullException.ThrowIfNull(http);
        var caller = Caller(http);
        if (caller.Staff?.Can(permission) == true)
        {
            return caller.Staff.Id;
        }

        var pin = http.Request.Headers["X-Manager-Pin"].ToString();
        if (string.IsNullOrEmpty(pin))
        {
            return null;
        }

        var access = http.RequestServices.GetRequiredService<AccessService>();
        var approver = access.VerifyApprover(caller.Device.Id, pin, permission)
            ?? throw new AccessDeniedException("CannotApprove", "That person's role cannot approve this.", 403);
        return approver.Id;
    }

    /// <summary>True when the request comes from the hub PC itself.</summary>
    public static bool IsLocal(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var remote = http.Connection.RemoteIpAddress;
        return remote is null || IPAddress.IsLoopback(remote); // null only in the in-memory test server
    }

    private static IResult Deny(string code, string message, int status) =>
        Results.Json(new ErrorView(code, message), PosJson.Options, statusCode: status);
}

/// <summary>Shortcuts for protecting routes.</summary>
public static class AccessExtensions
{
    /// <summary>Any registered device may call this (e.g. reading the menu).</summary>
    public static RouteHandlerBuilder ForDevices(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter(new AccessFilter(staffRequired: false, permission: null));

    /// <summary>A logged-in staff member is required; the handler checks finer permissions.</summary>
    public static RouteHandlerBuilder ForStaff(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter(new AccessFilter(staffRequired: true, permission: null));

    /// <summary>A logged-in staff member with this permission is required.</summary>
    public static RouteHandlerBuilder ForStaff(this RouteHandlerBuilder route, Permission permission) =>
        route.AddEndpointFilter(new AccessFilter(staffRequired: true, permission));
}
