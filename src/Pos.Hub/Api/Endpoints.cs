using System.Text.Json;
using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Pricing;

namespace Pos.Hub.Api;

/// <summary>The hub's web API used by tills, kitchen screens and the back office on the shop network.</summary>
public static class Endpoints
{
    /// <summary>Registers all routes.</summary>
    public static void MapHubApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup("/api");

        api.MapGet("/health", (HubSettings s, TimeProvider clock) =>
            Results.Ok(new { status = "ok", branch = s.BranchCode, time = clock.GetUtcNow() }));

        // ----- Menu -----
        api.MapGet("/menu", (MenuService menu) => Handle(() => Results.Ok(menu.Read(m => m.ToState()))));

        api.MapGet("/menu/changes", (MenuService menu, long? since) =>
            Handle(() => Results.Ok(menu.ChangesSince(since ?? 0))));

        api.MapPut("/menu", (MenuService menu, ImportMenuRequest request) =>
            Handle(() => Results.Ok(menu.Import(request.Menu, request.UserId))));

        api.MapPost("/menu/products/{productId:guid}/price", (MenuService menu, Guid productId, PriceRequest request) =>
            Handle(() => Change(menu.Edit(m => m.SetPrice(productId, request.VariantId, new Money(request.PricePence, m.Currency), request.UserId)))));

        api.MapPost("/menu/products/{productId:guid}/sold-out", (MenuService menu, Guid productId, SoldOutRequest request) =>
            Handle(() => Change(menu.Edit(m => m.SetSoldOut(productId, request.SoldOut, request.UserId)))));

        api.MapPost("/menu/products/{productId:guid}/rename", (MenuService menu, Guid productId, RenameRequest request) =>
            Handle(() => Change(menu.Edit(m => m.RenameProduct(productId, request.Name, request.UserId, request.ButtonName)))));

        api.MapPost("/menu/undo", (MenuService menu, ActorRequest request) =>
            Handle(() => Change(menu.Edit(m => m.UndoLast(request.UserId)))));

        // ----- Orders -----
        api.MapPost("/orders", (OrderService orders, CreateOrderRequest request) => Handle(() =>
        {
            var result = orders.Submit(request);
            var view = OrderView.From(result.Order, result.WasDuplicate);
            return result.WasDuplicate ? Results.Ok(view) : Results.Created($"/api/orders/{result.Order.Id}", view);
        }));

        api.MapGet("/orders", (OrderService orders, DateOnly? date, OrderStatus? status) =>
            Handle(() => Results.Ok(orders.ForDay(date, status).Select(o => OrderView.From(o)))));

        api.MapGet("/orders/{orderId:guid}", (OrderService orders, Guid orderId) =>
            Handle(() => Results.Ok(OrderView.From(orders.Get(orderId)))));

        api.MapGet("/orders/{orderId:guid}/history", (OrderService orders, Guid orderId) =>
            Handle(() => Results.Ok(orders.Get(orderId).History)));

        api.MapPost("/orders/{orderId:guid}/lines", (OrderService orders, Guid orderId, AddLinesRequest request) =>
            Handle(() => Results.Ok(OrderView.From(orders.AddLines(orderId, request.Lines, new OrderActor(request.UserId, request.DeviceId))))));

        api.MapPost("/orders/{orderId:guid}/status", (OrderService orders, Guid orderId, StatusRequest request) => Handle(() =>
        {
            var actor = new OrderActor(request.UserId, request.DeviceId);
            var order = orders.Change(orderId, o =>
            {
                if (request.Status == OrderStatus.Cancelled)
                {
                    o.Cancel(request.Reason ?? string.Empty, actor, request.ApprovedByUserId);
                }
                else
                {
                    o.MoveTo(request.Status, actor, request.Reason, request.ApprovedByUserId);
                }

                return o;
            });
            return Results.Ok(OrderView.From(order));
        }));

        api.MapPost("/orders/{orderId:guid}/send-to-kitchen", (OrderService orders, Guid orderId, ActorRequest request) => Handle(() =>
        {
            var sent = orders.Change(orderId, o => o.SendToKitchen(new OrderActor(request.UserId, request.DeviceId)));
            return Results.Ok(sent.Select(l => new { l.Id, l.Display, l.Pricing.Quantity, l.Note }));
        }));

        api.MapPost("/orders/{orderId:guid}/lines/{lineId:guid}/void", (OrderService orders, Guid orderId, Guid lineId, VoidRequest request) => Handle(() =>
        {
            var order = orders.Change(orderId, o =>
            {
                o.VoidLine(lineId, request.Reason, new OrderActor(request.UserId, request.DeviceId), request.ApprovedByUserId);
                return o;
            });
            return Results.Ok(OrderView.From(order));
        }));
    }

    private static IResult Change(MenuChange change) => Results.Ok(new MenuChangeView(change.Version, change.Summary));

    /// <summary>Turns refusals into clear answers for the till instead of server errors.</summary>
    private static IResult Handle(Func<IResult> action)
    {
        try
        {
            return action();
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
