using System.Text.Json;
using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Pricing;
using Pos.Domain.Staff;
using Pos.Hub.Security;
using static Pos.Hub.Security.AccessFilter;

namespace Pos.Hub.Api;

/// <summary>The hub's web API used by tills, kitchen screens and the back office on the shop network.</summary>
public static class Endpoints
{
    /// <summary>Registers all routes.</summary>
    public static void MapHubApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup("/api");

        api.MapGet("/health", (HubSettings s, TimeProvider clock, HubCertificates certificates) =>
            Results.Ok(new { status = "ok", branch = s.BranchCode, time = clock.GetUtcNow(), caCheckCode = certificates.CaCheckCode }));

        // The shop CA's public certificate, for installing on tills (not secret). Compare its check code with the hub screen.
        app.MapGet("/ca.crt", (HubCertificates certificates) =>
            Results.File(System.Text.Encoding.ASCII.GetBytes(certificates.CaPem), "application/x-pem-file", "pos-hub-ca.crt"));

        MapSetupAndAccess(api);
        MapLive(api);
        MapMenu(api);
        MapOrders(api);
    }

    private static void MapSetupAndAccess(RouteGroupBuilder api)
    {
        // First-time set-up: only from the hub PC itself, and only before any staff exist.
        api.MapGet("/setup", (AccessService access) => Results.Ok(new { isSetUp = access.HasStaff }));

        api.MapPost("/setup", (HttpContext http, AccessService access, SetupRequest request) => Handle(() =>
        {
            if (!IsLocal(http))
            {
                throw new AccessDeniedException("LocalOnly", "Set-up must be done on the hub PC itself.", 403);
            }

            var (owner, device, session) = access.FirstTimeSetup(request.OwnerName, request.Pin, request.DeviceName);
            return Results.Ok(new SetupResult(owner, device, session));
        }));

        // Devices
        api.MapPost("/devices/pairing-code", (HttpContext http, AccessService access, TimeProvider clock) => Handle(() =>
            Results.Ok(new PairingCodeView(access.CreatePairingCode(Caller(http).Staff!), clock.GetUtcNow() + AccessService.PairingCodeLife))))
            .ForStaff(Permission.ManageDevices);

        api.MapPost("/devices/pair", (AccessService access, PairRequest request) => Handle(() =>
            Results.Ok(access.Pair(request.Code, request.Name, request.Type))));

        api.MapGet("/devices", (AccessService access) => Handle(() => Results.Ok(access.ListDevices())))
            .ForStaff(Permission.ManageDevices);

        api.MapDelete("/devices/{deviceId:guid}", (HttpContext http, AccessService access, Live.LiveUpdates live, Guid deviceId) => Handle(() =>
        {
            access.RemoveDevice(Caller(http).Staff!, deviceId);
            live.DisconnectDevice(deviceId);
            return Results.NoContent();
        })).ForStaff(Permission.ManageDevices);

        // Staff logins
        api.MapPost("/auth/login", (HttpContext http, AccessService access, LoginRequest request) => Handle(() =>
            Results.Ok(access.Login(Caller(http).Device.Id, request.Pin))))
            .ForDevices();

        api.MapPost("/auth/logout", (HttpContext http, AccessService access) => Handle(() =>
        {
            access.Logout(http.Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim());
            return Results.NoContent();
        })).ForStaff();

        api.MapGet("/auth/me", (HttpContext http) => Handle(() =>
        {
            var caller = Caller(http);
            return Results.Ok(new { caller.Staff, caller.Device, Permissions = RolePermissions.For(caller.Staff!.Role) });
        })).ForStaff();

        // Staff management
        api.MapGet("/staff", (AccessService access) => Handle(() => Results.Ok(access.ListStaff())))
            .ForStaff(Permission.ManageStaff);

        api.MapPost("/staff", (HttpContext http, AccessService access, AddStaffRequest request) => Handle(() =>
            Results.Ok(access.AddStaff(Caller(http).Staff!, request.Name, request.Role, request.Pin))))
            .ForStaff(Permission.ManageStaff);

        api.MapPost("/staff/{staffId:guid}/pin", (HttpContext http, AccessService access, Guid staffId, ChangePinRequest request) => Handle(() =>
        {
            access.ChangePin(Caller(http).Staff!, staffId, request.Pin);
            return Results.NoContent();
        })).ForStaff();

        api.MapDelete("/staff/{staffId:guid}", (HttpContext http, AccessService access, Guid staffId) => Handle(() =>
        {
            access.Deactivate(Caller(http).Staff!, staffId);
            return Results.NoContent();
        })).ForStaff(Permission.ManageStaff);
    }

    private static void MapLive(RouteGroupBuilder api)
    {
        // A 60-second pass for opening the live connection at /live (WebSockets), plus the latest event number.
        api.MapPost("/live/ticket", (HttpContext http, Live.LiveUpdates live) => Handle(() =>
            Results.Ok(live.IssueTicket(Caller(http).Device))))
            .ForDevices();

        // Events missed while away: ask with the last number you saw; repeat while "more" is true.
        api.MapGet("/live/events", (HttpContext http, Live.LiveUpdates live, long? since) => Handle(() =>
            Results.Ok(live.Since(since ?? 0, Caller(http).Device.Type))))
            .ForDevices();
    }

    private static void MapMenu(RouteGroupBuilder api)
    {
        api.MapGet("/menu", (MenuService menu) => Handle(() => Results.Ok(menu.Read(m => m.ToState()))))
            .ForDevices();

        api.MapGet("/menu/changes", (MenuService menu, long? since) => Handle(() => Results.Ok(menu.ChangesSince(since ?? 0))))
            .ForDevices();

        api.MapPut("/menu", (HttpContext http, MenuService menu, MenuState state) => Handle(() =>
            Results.Ok(menu.Import(state, Caller(http).Staff!.Id))))
            .ForStaff(Permission.EditMenu);

        api.MapPost("/menu/products/{productId:guid}/price", (HttpContext http, MenuService menu, Guid productId, PriceRequest request) => Handle(() =>
            Change(menu.Edit(m => m.SetPrice(productId, request.VariantId, new Money(request.PricePence, m.Currency), Caller(http).Staff!.Id)))))
            .ForStaff(Permission.EditMenu);

        // Sold out can be set by anyone taking orders, so the counter can react straight away.
        api.MapPost("/menu/products/{productId:guid}/sold-out", (HttpContext http, MenuService menu, Guid productId, SoldOutRequest request) => Handle(() =>
            Change(menu.Edit(m => m.SetSoldOut(productId, request.SoldOut, RequireAny(http, Permission.EditMenu, Permission.TakeOrders, Permission.KitchenDisplay).Id)))))
            .ForStaff();

        api.MapPost("/menu/products/{productId:guid}/rename", (HttpContext http, MenuService menu, Guid productId, RenameRequest request) => Handle(() =>
            Change(menu.Edit(m => m.RenameProduct(productId, request.Name, Caller(http).Staff!.Id, request.ButtonName)))))
            .ForStaff(Permission.EditMenu);

        api.MapPost("/menu/undo", (HttpContext http, MenuService menu) => Handle(() =>
            Change(menu.Edit(m => m.UndoLast(Caller(http).Staff!.Id)))))
            .ForStaff(Permission.EditMenu);
    }

    private static void MapOrders(RouteGroupBuilder api)
    {
        api.MapPost("/orders", (HttpContext http, OrderService orders, CreateOrderRequest request) => Handle(() =>
        {
            var result = orders.Submit(request, Actor(http));
            var view = OrderView.From(result.Order, result.WasDuplicate);
            return result.WasDuplicate ? Results.Ok(view) : Results.Created($"/api/orders/{result.Order.Id}", view);
        })).ForStaff(Permission.TakeOrders);

        api.MapGet("/orders", (OrderService orders, DateOnly? date, OrderStatus? status) =>
            Handle(() => Results.Ok(orders.ForDay(date, status).Select(o => OrderView.From(o)))))
            .ForDevices();

        api.MapGet("/orders/{orderId:guid}", (OrderService orders, Guid orderId) =>
            Handle(() => Results.Ok(OrderView.From(orders.Get(orderId)))))
            .ForDevices();

        api.MapGet("/orders/{orderId:guid}/history", (OrderService orders, Guid orderId) =>
            Handle(() => Results.Ok(orders.Get(orderId).History)))
            .ForStaff(Permission.ViewReports);

        api.MapPost("/orders/{orderId:guid}/lines", (HttpContext http, OrderService orders, Guid orderId, AddLinesRequest request) =>
            Handle(() => Results.Ok(OrderView.From(orders.AddLines(orderId, request.Lines, Actor(http))))))
            .ForStaff(Permission.TakeOrders);

        api.MapPost("/orders/{orderId:guid}/status", (HttpContext http, OrderService orders, Guid orderId, StatusRequest request) => Handle(() =>
        {
            var actor = Actor(http);
            Guid? approver = null;
            switch (request.Status)
            {
                case OrderStatus.Accepted or OrderStatus.Rejected:
                    Require(http, Permission.AcceptOnlineOrders);
                    break;
                case OrderStatus.Preparing or OrderStatus.Ready:
                    RequireAny(http, Permission.TakeOrders, Permission.KitchenDisplay);
                    break;
                case OrderStatus.Cancelled:
                    approver = Approver(http, Permission.CancelOrders)
                        ?? throw new AccessDeniedException("ApprovalRequired", "Cancelling needs a manager. Ask a manager to enter their PIN.", 403);
                    break;
                default:
                    Require(http, Permission.TakeOrders);
                    break;
            }

            var order = orders.Change(orderId, o =>
            {
                if (request.Status == OrderStatus.Cancelled)
                {
                    o.Cancel(request.Reason ?? string.Empty, actor, approver);
                }
                else
                {
                    o.MoveTo(request.Status, actor, request.Reason);
                }

                return o;
            });
            return Results.Ok(OrderView.From(order));
        })).ForStaff();

        api.MapPost("/orders/{orderId:guid}/send-to-kitchen", (HttpContext http, OrderService orders, Guid orderId) => Handle(() =>
        {
            var sent = orders.Change(orderId, o => o.SendToKitchen(Actor(http)));
            return Results.Ok(sent.Select(l => new { l.Id, l.Display, l.Pricing.Quantity, l.Note }));
        })).ForStaff(Permission.TakeOrders);

        api.MapPost("/orders/{orderId:guid}/lines/{lineId:guid}/void", (HttpContext http, OrderService orders, Guid orderId, Guid lineId, VoidRequest request) => Handle(() =>
        {
            var line = orders.Get(orderId).Lines.FirstOrDefault(l => l.Id == lineId) ?? throw new KeyNotFoundException("Line not found on this order.");
            Guid? approver = null;
            if (line.SentToKitchen)
            {
                approver = Approver(http, Permission.VoidAfterKitchen)
                    ?? throw new AccessDeniedException("ApprovalRequired", "This item has gone to the kitchen. Ask a manager to enter their PIN.", 403);
            }
            else
            {
                Require(http, Permission.VoidBeforeKitchen);
            }

            var order = orders.Change(orderId, o =>
            {
                o.VoidLine(lineId, request.Reason, Actor(http), approver);
                return o;
            });
            return Results.Ok(OrderView.From(order));
        })).ForStaff();
    }

    private static IResult Change(MenuChange change) => Results.Ok(new MenuChangeView(change.Version, change.Summary));

    /// <summary>Turns refusals into clear answers for the till instead of server errors.</summary>
    private static IResult Handle(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (AccessDeniedException ex)
        {
            return Error(ex.Code, ex.Message, ex.StatusCode);
        }
        catch (InvalidSelectionException ex)
        {
            return Results.Json(new ErrorView("InvalidSelection", ex.Message, ex.Problems), PosJson.Options, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (IdempotencyConflictException ex)
        {
            return Error("DuplicateKey", ex.Message, StatusCodes.Status409Conflict);
        }
        catch (DiscountApprovalRequiredException ex)
        {
            return Error("ApprovalRequired", ex.Message, StatusCodes.Status403Forbidden);
        }
        catch (KeyNotFoundException ex)
        {
            return Error("NotFound", ex.Message, StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return Error("NotAllowed", ex.Message, StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Error("Invalid", ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (JsonException ex)
        {
            return Error("Invalid", ex.Message, StatusCodes.Status400BadRequest);
        }
    }

    private static IResult Error(string code, string message, int status) =>
        Results.Json(new ErrorView(code, message), PosJson.Options, statusCode: status);
}
