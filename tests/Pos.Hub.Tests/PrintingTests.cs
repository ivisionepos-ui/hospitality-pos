using System.Net;
using System.Net.Http.Json;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Live;
using Pos.Hub.Printing;

namespace Pos.Hub.Tests;

/// <summary>Printing through the hub: kitchen tickets, receipts, copies, Z slips, previews and failures.</summary>
public sealed class PrintingTests : HubTestBase
{
    private static OrderLineRequest Doner => new(TestMenu.Doner, TestMenu.DonerRegular, 1, [TestMenu.Garlic]);

    private static OrderLineRequest Can => new(TestMenu.Can, TestMenu.CanRegular);

    private async Task<OrderView> Order(params OrderLineRequest[] lines)
    {
        await Manager.Put("/api/menu", TestMenu.State());
        return await (await Cashier.Post("/api/orders",
            new CreateOrderRequest(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, OrderType.Takeaway, null, null, lines))).Read<OrderView>();
    }

    /// <summary>Waits until the print worker has finished every job, and returns them all.</summary>
    private async Task<List<PrintJobView>> Settled(int expected)
    {
        for (var i = 0; i < 100; i++)
        {
            var jobs = await Cashier.GetJson<List<PrintJobView>>("/api/print-jobs");
            if (jobs.Count >= expected && jobs.All(j => j.Status != PrintJobStatus.Pending))
            {
                return jobs;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Print jobs did not finish.");
    }

    [Fact]
    public async Task Sending_to_the_kitchen_prints_a_ticket_and_later_items_print_as_additional()
    {
        var order = await Order(Doner);
        await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { });
        await Cashier.Post($"/api/orders/{order.Id}/lines", new AddLinesRequest([Can]));
        await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { });

        var jobs = await Settled(2);

        Assert.Equal([PrintJobKind.KitchenAdditional, PrintJobKind.KitchenNew], jobs.Select(j => j.Job.Kind));
        Assert.All(jobs, j => Assert.Equal(PrintJobStatus.Printed, j.Status));
        Assert.All(jobs, j => Assert.True(File.Exists(j.Output), j.Output));
        Assert.All(jobs, j => Assert.StartsWith(Path.Combine(Host.PrintFolder, "kitchen"), j.Output, StringComparison.Ordinal));
        Assert.Single(jobs[0].Job.LineIds); // only the can
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(jobs[0].Output!).AsSpan(0, 4)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_an_item_already_in_the_kitchen_prints_a_void_slip()
    {
        var order = await Order(Doner, Can);
        await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { });

        await Manager.Post($"/api/orders/{order.Id}/lines/{order.Lines[1].Id}/void", new VoidRequest("Out of cans"));

        var jobs = await Settled(2);
        var voidSlip = jobs.Single(j => j.Job.Kind == PrintJobKind.KitchenVoid);
        Assert.Equal([order.Lines[1].Id], voidSlip.Job.LineIds);
    }

    [Fact]
    public async Task Completing_prints_the_receipt_and_a_reprint_is_a_copy()
    {
        var order = await Order(Doner);
        await Cashier.Post("/api/shifts/open", new OpenShiftRequest(0));
        await Cashier.Post($"/api/orders/{order.Id}/payments/cash", new CashPaymentRequest(1000));
        await Cashier.Post($"/api/orders/{order.Id}/status", new StatusRequest(OrderStatus.Completed));
        await Cashier.Post($"/api/orders/{order.Id}/print-receipt", new { });

        var jobs = await Settled(2);

        Assert.Equal([PrintJobKind.ReceiptCopy, PrintJobKind.Receipt], jobs.Select(j => j.Job.Kind));
        Assert.All(jobs, j => Assert.StartsWith(Path.Combine(Host.PrintFolder, "receipts"), j.Output, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Receipt_preview_comes_as_png_or_pdf_and_is_not_counted_as_printed()
    {
        var order = await Order(Doner);

        var png = await Cashier.GetByteArrayAsync(new Uri($"/api/orders/{order.Id}/receipt", UriKind.Relative));
        var pdf = await Cashier.GetByteArrayAsync(new Uri($"/api/orders/{order.Id}/receipt?format=pdf", UriKind.Relative));

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Empty(await Cashier.GetJson<List<PrintJobView>>("/api/print-jobs"));
    }

    [Fact]
    public async Task Z_report_slip_prints_once_the_z_is_taken()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Manager.Post("/api/reports/z/print", new ZReportRequest(null))).StatusCode);

        await Manager.Post("/api/reports/z", new ZReportRequest(null));
        Assert.Equal(HttpStatusCode.OK, (await Manager.Post("/api/reports/z/print", new ZReportRequest(null))).StatusCode);

        var job = Assert.Single(await Settled(1));
        Assert.Equal(PrintJobKind.ZReport, job.Job.Kind);
        Assert.Equal(PrintJobStatus.Printed, job.Status);
    }

    [Fact]
    public async Task Cashier_cannot_print_the_z_report()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.Post("/api/reports/z/print", new ZReportRequest(null))).StatusCode);
    }
}

/// <summary>A kitchen printer that cannot print: jobs are retried, then marked failed and the tills are told.</summary>
public sealed class PrintFailureTests : HubTestBase
{
    // "Windows" output only works on Windows; these tests run on Linux, so every kitchen print fails.
    internal override IReadOnlyDictionary<string, string> ExtraSettings => OperatingSystem.IsWindows()
        ? new Dictionary<string, string> { ["Printing:Kitchen:Output"] = "Windows", ["Printing:Kitchen:PrinterName"] = "No Such Printer 123" }
        : new Dictionary<string, string> { ["Printing:Kitchen:Output"] = "Windows" };

    [Fact]
    public async Task Failed_print_is_retried_then_reported_and_can_be_tried_again()
    {
        await Manager.Put("/api/menu", TestMenu.State());
        var order = await (await Cashier.Post("/api/orders", new CreateOrderRequest(DeviceIds.New(), "k1", OrderSource.Counter, OrderType.Takeaway, null, null,
            [new OrderLineRequest(TestMenu.Can, TestMenu.CanRegular)]))).Read<OrderView>();

        // Sending still works: the order is not held up by the printer.
        Assert.Equal(HttpStatusCode.OK, (await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { })).StatusCode);

        var failed = await WaitFor(PrintJobStatus.Failed);
        Assert.Equal(2, failed.Attempts);
        Assert.NotNull(failed.LastError);

        var events = await TillOnly.GetJson<CatchUpResult>("/api/live/events?since=0");
        Assert.Contains(events.Events, e => e.Kind == LiveEventKinds.PrintFailed && e.Summary!.Contains("kitchen ticket", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.NoContent, (await Cashier.Post($"/api/print-jobs/{failed.Job.Id}/retry", new { })).StatusCode);
        var again = await WaitFor(PrintJobStatus.Failed, after: failed);
        Assert.Equal(2, again.Attempts);
        Assert.Equal(HttpStatusCode.Conflict, (await Cashier.Post($"/api/print-jobs/{Guid.NewGuid()}/retry", new { })).StatusCode);
    }

    private async Task<PrintJobView> WaitFor(PrintJobStatus status, PrintJobView? after = null)
    {
        for (var i = 0; i < 100; i++)
        {
            var job = (await Cashier.GetJson<List<PrintJobView>>($"/api/print-jobs?status={status}")).FirstOrDefault();
            if (job is not null && (after is null || job.Attempts > 0 && !ReferenceEquals(job, after)))
            {
                if (after is null || (await Cashier.GetJson<List<PrintJobView>>("/api/print-jobs?status=Pending")).Count == 0)
                {
                    return job;
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No print job reached {status}.");
    }
}
