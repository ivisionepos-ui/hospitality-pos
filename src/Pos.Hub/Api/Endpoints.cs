using System.Text.Json;
using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Payments;
using Pos.Domain.Pricing;
using Pos.Domain.Staff;
using Pos.Hub.Backups;
using Pos.Hub.Printing;
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
        MapTill(api);
        MapPrinting(api);
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

    private static void MapTill(RouteGroupBuilder api)
    {
        static Money Gbp(long pence) => Money.Gbp(pence);
        static Guid TillId(HttpContext http)
        {
            var device = Caller(http).Device;
            return device.Type is DeviceType.Till or DeviceType.BackOffice
                ? device.Id
                : throw new AccessDeniedException("NotATill", "Payments and shifts are only on tills.", 403);
        }

        static PaymentsView View(OrderPayments ledger) => new(ledger.Total, ledger.Paid, ledger.Balance, ledger.Tips, ledger.Payments);

        // ----- Shifts -----
        api.MapGet("/shifts/current", (HttpContext http, TillService tills) => Handle(() =>
            tills.CurrentShift(TillId(http)) is { } shift ? Results.Ok(shift) : Results.NoContent()))
            .ForStaff(Permission.TakeOrders);

        api.MapPost("/shifts/open", (HttpContext http, TillService tills, OpenShiftRequest request) => Handle(() =>
            Results.Ok(tills.OpenShift(TillId(http), Caller(http).Staff!.Id, Gbp(request.OpeningFloatPence)))))
            .ForStaff(Permission.TakeOrders);

        api.MapPost("/shifts/paid-in", (HttpContext http, TillService tills, CashMovementRequest request) => Handle(() =>
            Results.Ok(tills.MoveCash(TillId(http), CashMovementType.PaidIn, Gbp(request.AmountPence), request.Reason, Caller(http).Staff!.Id, CashApprover(http)))))
            .ForStaff();

        api.MapPost("/shifts/paid-out", (HttpContext http, TillService tills, CashMovementRequest request) => Handle(() =>
            Results.Ok(tills.MoveCash(TillId(http), CashMovementType.PaidOut, Gbp(request.AmountPence), request.Reason, Caller(http).Staff!.Id, CashApprover(http)))))
            .ForStaff();

        // Blind count: staff enter what they counted; the expected figure and over/short are shown only after closing.
        api.MapPost("/shifts/close", (HttpContext http, TillService tills, CloseShiftRequest request) => Handle(() =>
            Results.Ok(tills.CloseShift(TillId(http), Caller(http).Staff!.Id, Gbp(request.CountedCashPence)))))
            .ForStaff(Permission.TakeOrders);

        api.MapGet("/shifts/current/x-report", (HttpContext http, TillService tills) => Handle(() =>
            Results.Ok(tills.XReport(TillId(http)))))
            .ForStaff(Permission.ViewReports);

        // ----- Payments -----
        api.MapGet("/orders/{orderId:guid}/payments", (OrderService orders, TillService tills, Guid orderId) => Handle(() =>
            Results.Ok(View(tills.Ledger(orders.Get(orderId))))))
            .ForStaff(Permission.TakeOrders);

        api.MapPost("/orders/{orderId:guid}/payments/cash", (HttpContext http, TillService tills, PrintService print, Guid orderId, CashPaymentRequest request) => Handle(() =>
        {
            var result = tills.TakeCash(orderId, TillId(http), Caller(http).Staff!.Id, request.PaymentId, Gbp(request.TenderedPence),
                request.AmountPence is { } a ? Gbp(a) : null, request.ForLineIds);
            ReceiptWhenPaid(print, orderId, result.Ledger, result.WasDuplicate);
            return Results.Ok(new PaymentResponse(result.Payment, View(result.Ledger), result.WasDuplicate));
        })).ForStaff(Permission.TakeOrders);

        api.MapPost("/orders/{orderId:guid}/payments/card", (HttpContext http, TillService tills, PrintService print, Guid orderId, CardPaymentRequest request) => Handle(() =>
        {
            var result = tills.TakeCard(orderId, TillId(http), Caller(http).Staff!.Id, request.PaymentId, Gbp(request.AmountPence),
                request.TipPence is { } t ? Gbp(t) : null, request.Reference, request.ForLineIds);
            ReceiptWhenPaid(print, orderId, result.Ledger, result.WasDuplicate);
            return Results.Ok(new PaymentResponse(result.Payment, View(result.Ledger), result.WasDuplicate));
        })).ForStaff(Permission.TakeOrders);

        api.MapPost("/orders/{orderId:guid}/payments/{paymentId:guid}/refund", (HttpContext http, TillService tills, Guid orderId, Guid paymentId, RefundRequest request) => Handle(() =>
        {
            var approver = Approver(http, Permission.RefundPayments)
                ?? throw new AccessDeniedException("ApprovalRequired", "Refunds need a manager. Ask a manager to enter their PIN.", 403);
            var result = tills.Refund(orderId, paymentId, TillId(http), Caller(http).Staff!.Id, approver, Gbp(request.AmountPence), request.Reason, request.RefundId);
            return Results.Ok(new PaymentResponse(result.Payment, View(result.Ledger), result.WasDuplicate));
        })).ForStaff();

        // ----- Splitting a bill -----
        api.MapGet("/orders/{orderId:guid}/split", (OrderService orders, TillService tills, Guid orderId, int parts) => Handle(() =>
            Results.Ok(new SplitView(BillSplitter.Equally(tills.Ledger(orders.Get(orderId)).Balance, parts)))))
            .ForStaff(Permission.TakeOrders);

        api.MapPost("/orders/{orderId:guid}/split-by-items", (OrderService orders, TillService tills, Guid orderId, SplitByItemsRequest request) => Handle(() =>
            Results.Ok(new SplitView([tills.AmountForLines(orders.Get(orderId), request.LineIds)]))))
            .ForStaff(Permission.TakeOrders);

        // ----- End of day -----
        api.MapPost("/reports/z", (HttpContext http, TillService tills, BackupService backups, ZReportRequest? request) => Handle(() =>
        {
            var report = tills.ZReport(request?.BusinessDate, Caller(http).Staff!.Id);
            backups.Request("Z report"); // end of the trading day: back up straight away
            return Results.Ok(report);
        })).ForStaff(Permission.ViewReports);

        api.MapGet("/backups", (BackupService backups) => Results.Ok(backups.Status()))
            .ForStaff(Permission.ViewReports);

        api.MapPost("/backups", (BackupService backups) =>
        {
            var result = backups.BackUp("Manual", force: true);
            return result.Ok ? Results.Ok(result) : Results.Json(result, PosJson.Options, statusCode: StatusCodes.Status500InternalServerError);
        }).ForStaff(Permission.ViewReports);
    }

    private static void MapPrinting(RouteGroupBuilder api)
    {
        api.MapPost("/orders/{orderId:guid}/print-receipt", (OrderService orders, PrintService print, Guid orderId) => Handle(() =>
        {
            _ = orders.Get(orderId);
            return Results.Ok(print.QueueReceipt(orderId));
        })).ForStaff(Permission.TakeOrders);

        // Receipt preview for the till screen (not printed, not counted as a print).
        api.MapGet("/orders/{orderId:guid}/receipt", (PrintService print, Guid orderId, string? format) => Handle(() =>
        {
            var pdf = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase);
            return Results.File(print.PreviewReceipt(orderId, pdf), pdf ? "application/pdf" : "image/png");
        })).ForStaff(Permission.TakeOrders);

        api.MapPost("/reports/z/print", (PrintService print, TillService tills, HubSettings hub, TimeProvider clock, ZReportRequest? request) => Handle(() =>
            Results.Ok(print.QueueZReport(request?.BusinessDate ?? BusinessDay.For(clock.GetUtcNow(), hub.TimeZone, hub.DayStartsAt)))))
            .ForStaff(Permission.ViewReports);

        api.MapGet("/print-jobs", (PrintService print, PrintJobStatus? status) => Handle(() => Results.Ok(print.List(status))))
            .ForStaff(Permission.TakeOrders);

        api.MapPost("/print-jobs/{jobId:guid}/retry", (PrintService print, Guid jobId) => Handle(() =>
        {
            print.Retry(jobId);
            return Results.NoContent();
        })).ForStaff(Permission.TakeOrders);
    }

    private static Guid CashApprover(HttpContext http) =>
        Approver(http, Permission.CashManagement)
            ?? throw new AccessDeniedException("ApprovalRequired", "Paid in and paid out need a manager. Ask a manager to enter their PIN.", 403);

    private static void MapMenu(RouteGroupBuilder api)
    {
        api.MapGet("/menu", (MenuService menu) => Handle(() => Results.Ok(menu.Read(m => m.ToState()))))
            .ForDevices();

        api.MapGet("/menu/changes", (MenuService menu, long? since) => Handle(() => Results.Ok(menu.ChangesSince(since ?? 0))))
            .ForDevices();

        api.MapPut("/menu", (HttpContext http, MenuService menu, MenuState state) => Handle(() =>
            Results.Ok(menu.Import(state, Caller(http).Staff!.Id))))
            .ForStaff(Permission.EditMenu);

        // Sample takeaway menu for trying the till; refused once the menu has items, so a real menu is never replaced.
        api.MapPost("/menu/sample", (HttpContext http, MenuService menu) => Handle(() =>
        {
            if (menu.Read(m => m.ToState().Products.Count) > 0)
            {
                throw new InvalidOperationException("The menu already has items; the sample menu is only for an empty menu.");
            }

            return Results.Ok(menu.Import(SampleMenu.Build(), Caller(http).Staff!.Id));
        })).ForStaff(Permission.EditMenu);

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

        // ----- Edit menu mode -----
        api.MapPut("/menu/products/{productId:guid}", (HttpContext http, MenuService menu, Guid productId, SaveProductRequest request) => Handle(() =>
        {
            if (request.Product.Id != productId)
            {
                throw new ArgumentException("The item id does not match the address.");
            }

            return Change(menu.Edit(m =>
            {
                ChangedElsewhereException.ThrowIfChanged(m.GetProduct(productId), request.Original);
                return m.UpdateProduct(request.Product, Caller(http).Staff!.Id);
            }));
        })).ForStaff(Permission.EditMenu);

        api.MapPost("/menu/products", (HttpContext http, MenuService menu, AddProductRequest request) => Handle(() =>
            Change(menu.Edit(m => m.AddProduct(request.Product, Caller(http).Staff!.Id, request.Position)))))
            .ForStaff(Permission.EditMenu);

        api.MapPost("/menu/products/{productId:guid}/move", (HttpContext http, MenuService menu, Guid productId, MoveRequest request) => Handle(() =>
            Change(menu.Edit(m => m.MoveProduct(productId, request.CategoryId ?? m.GetProduct(productId).CategoryId, request.Position, Caller(http).Staff!.Id)))))
            .ForStaff(Permission.EditMenu);

        api.MapPost("/menu/categories", (HttpContext http, MenuService menu, Category category) => Handle(() =>
            Change(menu.Edit(m => m.AddCategory(category, Caller(http).Staff!.Id)))))
            .ForStaff(Permission.EditMenu);

        api.MapPut("/menu/categories/{categoryId:guid}", (HttpContext http, MenuService menu, Guid categoryId, SaveCategoryRequest request) => Handle(() =>
        {
            if (request.Category.Id != categoryId)
            {
                throw new ArgumentException("The category id does not match the address.");
            }

            return Change(menu.Edit(m =>
            {
                ChangedElsewhereException.ThrowIfChanged(m.GetCategory(categoryId), request.Original);
                return m.UpdateCategory(request.Category, Caller(http).Staff!.Id);
            }));
        })).ForStaff(Permission.EditMenu);

        api.MapPost("/menu/categories/{categoryId:guid}/move", (HttpContext http, MenuService menu, Guid categoryId, MoveRequest request) => Handle(() =>
            Change(menu.Edit(m => m.MoveCategory(categoryId, request.Position, Caller(http).Staff!.Id)))))
            .ForStaff(Permission.EditMenu);

        api.MapPut("/menu/modifier-groups/{groupId:guid}", (HttpContext http, MenuService menu, Guid groupId, SaveModifierGroupRequest request) => Handle(() =>
        {
            if (request.Group.Id != groupId)
            {
                throw new ArgumentException("The extras id does not match the address.");
            }

            return Change(menu.Edit(m =>
            {
                if (request.Original is not null)
                {
                    ChangedElsewhereException.ThrowIfChanged(m.GetModifierGroup(groupId), request.Original);
                }

                return m.SaveModifierGroup(request.Group, Caller(http).Staff!.Id);
            }));
        })).ForStaff(Permission.EditMenu);

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

        // Totals for an order still being built on the till; nothing is saved and no order number is used.
        api.MapPost("/orders/quote", (HttpContext http, OrderService orders, CreateOrderRequest request) => Handle(() =>
            Results.Ok(OrderView.From(orders.Quote(request, Actor(http))))))
            .ForStaff(Permission.TakeOrders);

        api.MapGet("/orders", (OrderService orders, TillService tills, DateOnly? date, OrderStatus? status) =>
            Handle(() => Results.Ok(orders.ForDay(date, status).Select(o => WithMoney(OrderView.From(o), tills.Ledger(o))))))
            .ForDevices();

        api.MapGet("/orders/{orderId:guid}", (OrderService orders, TillService tills, Guid orderId) =>
            Handle(() =>
            {
                var order = orders.Get(orderId);
                return Results.Ok(WithMoney(OrderView.From(order), tills.Ledger(order)));
            }))
            .ForDevices();

        api.MapGet("/orders/{orderId:guid}/history", (OrderService orders, Guid orderId) =>
            Handle(() => Results.Ok(orders.Get(orderId).History)))
            .ForStaff(Permission.ViewReports);

        api.MapPost("/orders/{orderId:guid}/lines", (HttpContext http, OrderService orders, Guid orderId, AddLinesRequest request) =>
            Handle(() =>
            {
                var (order, duplicate) = orders.AddLines(orderId, request.Lines, Actor(http), request.RequestId);
                return Results.Ok(OrderView.From(order, duplicate));
            }))
            .ForStaff(Permission.TakeOrders);

        api.MapPost("/orders/{orderId:guid}/status", (HttpContext http, OrderService orders, TillService tills, PrintService print, Guid orderId, StatusRequest request) => Handle(() =>
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
                // Money rules: an order is completed only when fully paid, and cancelled only once any payment is refunded.
                var ledger = tills.Ledger(o);
                if (request.Status == OrderStatus.Completed && !ledger.IsSettled)
                {
                    throw new InvalidOperationException(ledger.Balance.IsNegative
                        ? $"{Money.Zero(ledger.Total.Currency) - ledger.Balance} must be refunded before this order can be completed."
                        : $"{ledger.Balance} is still to pay.");
                }

                if (request.Status is OrderStatus.Cancelled or OrderStatus.Rejected && ledger.Paid.MinorUnits != 0)
                {
                    throw new InvalidOperationException($"Refund the {ledger.Paid} paid before cancelling this order.");
                }

                if (request.Status == OrderStatus.Cancelled)
                {
                    o.Cancel(request.Reason ?? string.Empty, actor, approver);
                }
                else
                {
                    o.MoveTo(request.Status, actor, request.Reason);
                }

                // Normally the receipt printed when the order was paid; print one now only if it never was.
                if (request.Status == OrderStatus.Completed && print.Settings.AutoPrintReceipt && !print.HasReceipt(o.Id))
                {
                    print.QueueReceipt(o.Id);
                }

                return o;
            });
            return Results.Ok(OrderView.From(order));
        })).ForStaff();

        api.MapPost("/orders/{orderId:guid}/send-to-kitchen", (HttpContext http, OrderService orders, PrintService print, Guid orderId) => Handle(() =>
        {
            var sent = orders.Change(orderId, o =>
            {
                var sentBefore = o.Lines.Any(l => l.SentToKitchen && !l.IsVoided);
                var lines = o.SendToKitchen(Actor(http));
                if (lines.Count > 0 && print.Settings.AutoPrintKitchen)
                {
                    print.QueueKitchen(o, lines, sentBefore ? KitchenTicketKind.Additional : KitchenTicketKind.New);
                }

                return lines;
            });
            return Results.Ok(sent.Select(l => new { l.Id, l.Display, l.Pricing.Quantity, l.Note }));
        })).ForStaff(Permission.TakeOrders);

        api.MapPost("/orders/{orderId:guid}/lines/{lineId:guid}/void", (HttpContext http, OrderService orders, PrintService print, Guid orderId, Guid lineId, VoidRequest request) => Handle(() =>
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
                if (line.SentToKitchen && print.Settings.AutoPrintKitchen)
                {
                    print.QueueKitchen(o, [o.Lines.Single(l => l.Id == lineId)], KitchenTicketKind.Void);
                }

                return o;
            });
            return Results.Ok(OrderView.From(order));
        })).ForStaff();
    }

    private static IResult Change(MenuChange change) => Results.Ok(new MenuChangeView(change.Version, change.Summary));

    /// <summary>Turns refusals into clear answers for the till instead of server errors.</summary>
    private static OrderView WithMoney(OrderView view, OrderPayments ledger) => view with { Paid = ledger.Paid, Balance = ledger.Balance };

    /// <summary>Prints the receipt automatically the moment an order becomes fully paid (once).</summary>
    private static void ReceiptWhenPaid(PrintService print, Guid orderId, OrderPayments ledger, bool duplicate)
    {
        if (!duplicate && print.Settings.AutoPrintReceipt && ledger.IsSettled && ledger.Paid.MinorUnits > 0 && !print.HasReceipt(orderId))
        {
            print.QueueReceipt(orderId);
        }
    }

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
        catch (ChangedElsewhereException ex)
        {
            return Error("ChangedElsewhere", ex.Message, StatusCodes.Status409Conflict);
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
