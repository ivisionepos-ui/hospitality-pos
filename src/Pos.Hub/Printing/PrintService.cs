using System.Globalization;
using System.Threading.Channels;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Hub.Data;
using Pos.Hub.Live;
using Pos.Hub.Security;

namespace Pos.Hub.Printing;

/// <summary>What a print job prints.</summary>
public enum PrintJobKind
{
    /// <summary>Customer receipt.</summary>
    Receipt,

    /// <summary>Reprinted receipt, marked COPY.</summary>
    ReceiptCopy,

    /// <summary>First kitchen ticket for an order.</summary>
    KitchenNew,

    /// <summary>Items added later.</summary>
    KitchenAdditional,

    /// <summary>Items removed after being sent.</summary>
    KitchenVoid,

    /// <summary>End-of-day Z report.</summary>
    ZReport,

    /// <summary>Open the cash drawer on the receipt printer (tried once, never later).</summary>
    DrawerKick,
}

/// <summary>Where a print job is.</summary>
public enum PrintJobStatus
{
    /// <summary>Waiting to print (or waiting to try again).</summary>
    Pending,

    /// <summary>Printed.</summary>
    Printed,

    /// <summary>Gave up after several tries; can be retried by staff.</summary>
    Failed,
}

/// <summary>A print job as saved.</summary>
/// <param name="Id">Id.</param>
/// <param name="Kind">What it prints.</param>
/// <param name="Target">Receipt or kitchen printer.</param>
/// <param name="OrderId">The order, for receipts and kitchen tickets.</param>
/// <param name="LineIds">The lines, for kitchen tickets.</param>
/// <param name="BusinessDate">The day, for Z reports.</param>
public sealed record PrintJob(Guid Id, PrintJobKind Kind, PrintTarget Target, Guid? OrderId, IReadOnlyList<Guid> LineIds, DateOnly? BusinessDate);

/// <summary>A print job and how it went, for the "failed prints" screen.</summary>
/// <param name="Job">The job.</param>
/// <param name="Status">Pending, printed or failed.</param>
/// <param name="Attempts">Tries so far.</param>
/// <param name="CreatedAt">When it was asked for.</param>
/// <param name="PrintedAt">When it printed.</param>
/// <param name="LastError">Why the last try failed.</param>
/// <param name="Output">File or printer it went to.</param>
public sealed record PrintJobView(PrintJob Job, PrintJobStatus Status, int Attempts, DateTimeOffset CreatedAt, DateTimeOffset? PrintedAt, string? LastError, string? Output);

/// <summary>
/// Queues printouts and prints them in order. A job is saved in the same transaction as the change that asked for it
/// (so a kitchen ticket is never lost, and never printed for a change that did not happen), then printed once that is saved.
/// Failed prints are tried again; after several failures the tills are told and staff can retry.
/// </summary>
public sealed class PrintService(
    HubDatabase db,
    PrintSettings settings,
    HubSettings hub,
    OrderService orders,
    TillService tills,
    AccessService access,
    PrintOutputs outputs,
    Renderer renderer,
    LiveUpdates live,
    TimeProvider clock)
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Jobs waiting to be printed (read by the print worker).</summary>
    public ChannelReader<Guid> Queue => _queue.Reader;

    /// <summary>The printing settings.</summary>
    public PrintSettings Settings => settings;

    // ---------- Asking for printouts (call inside the transaction that makes the change) ----------

    /// <summary>True when a receipt has already been printed (or queued) for this order.</summary>
    public bool HasReceipt(Guid orderId) => db.Run(() => db.Scalar(
        "SELECT 1 FROM print_jobs WHERE order_id = $o AND kind IN ('Receipt', 'ReceiptCopy') LIMIT 1;", ("$o", orderId.ToString())) is not null);

    /// <summary>Queues a receipt. If one was printed before for this order, it is marked COPY.</summary>
    public PrintJob QueueReceipt(Guid orderId)
    {
        var printedBefore = HasReceipt(orderId);
        return Save(new PrintJob(NewId(), printedBefore ? PrintJobKind.ReceiptCopy : PrintJobKind.Receipt, PrintTarget.Receipt, orderId, [], null));
    }

    /// <summary>Queues a kitchen ticket for lines just sent (or voided).</summary>
    public PrintJob QueueKitchen(Order order, IReadOnlyList<OrderLine> lines, KitchenTicketKind kind)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(lines);
        var jobKind = kind switch
        {
            KitchenTicketKind.Void => PrintJobKind.KitchenVoid,
            KitchenTicketKind.Additional => PrintJobKind.KitchenAdditional,
            _ => PrintJobKind.KitchenNew,
        };
        return Save(new PrintJob(NewId(), jobKind, PrintTarget.Kitchen, order.Id, [.. lines.Select(l => l.Id)], null));
    }

    /// <summary>Queues the Z report slip for a day whose Z report has been taken.</summary>
    public PrintJob QueueZReport(DateOnly businessDate) =>
        tills.FindZReport(businessDate) is null
            ? throw new KeyNotFoundException("No Z report has been taken for that day.")
            : Save(new PrintJob(NewId(), PrintJobKind.ZReport, PrintTarget.Receipt, null, [], businessDate));

    /// <summary>True when the receipt printer is a thermal printer that can open a cash drawer.</summary>
    public bool HasDrawer => settings.Receipt.IsEscPos;

    /// <summary>
    /// Opens the cash drawer through the print queue (so it never interrupts a printout). Does nothing when the receipt
    /// printer cannot open a drawer. Returns true when the drawer was asked to open.
    /// </summary>
    public bool QueueDrawer()
    {
        if (!HasDrawer)
        {
            return false;
        }

        Save(new PrintJob(NewId(), PrintJobKind.DrawerKick, PrintTarget.Receipt, null, [], null));
        return true;
    }

    // ---------- Staff screens ----------

    /// <summary>Jobs, newest first, optionally only with one status.</summary>
    public IReadOnlyList<PrintJobView> List(PrintJobStatus? status, int max = 100) => db.Run(() =>
    {
        using var command = db.Command(
            """
            SELECT payload_json, status, attempts, created_at, printed_at, last_error, output FROM print_jobs
            WHERE ($s IS NULL OR status = $s) ORDER BY created_at DESC, rowid DESC LIMIT $n;
            """,
            ("$s", status?.ToString()), ("$n", max));
        using var reader = command.ExecuteReader();
        var result = new List<PrintJobView>();
        while (reader.Read())
        {
            result.Add(new PrintJobView(
                PosJson.Deserialize<PrintJob>(reader.GetString(0)),
                Enum.Parse<PrintJobStatus>(reader.GetString(1)),
                reader.GetInt32(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return result;
    });

    /// <summary>Tries a failed job again.</summary>
    public void Retry(Guid jobId) => db.RunInTransaction(() =>
    {
        if (db.Execute("UPDATE print_jobs SET status = 'Pending', attempts = 0, last_error = NULL WHERE id = $id AND status = 'Failed';", ("$id", jobId.ToString())) == 0)
        {
            throw new InvalidOperationException("Only failed prints can be tried again.");
        }

        db.AfterCommit(() => _queue.Writer.TryWrite(jobId));
        return 0;
    });

    /// <summary>A receipt as it would print (for the till's preview), as PNG or PDF, without printing it.</summary>
    public byte[] PreviewReceipt(Guid orderId, bool pdf)
    {
        var printer = settings.Receipt;
        var document = ReceiptDocument(orderId, isCopy: false);
        return pdf ? renderer.RenderPdf(document, printer.WidthDots, printer.PaperWidthMm) : renderer.RenderPng(document, printer.WidthDots);
    }

    // ---------- Printing (used by the print worker) ----------

    /// <summary>Jobs still waiting (after a restart, these are printed first).</summary>
    public IReadOnlyList<Guid> PendingJobIds() => db.Run(() =>
    {
        using var command = db.Command("SELECT id FROM print_jobs WHERE status = 'Pending' ORDER BY created_at, rowid;");
        using var reader = command.ExecuteReader();
        var ids = new List<Guid>();
        while (reader.Read())
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return ids;
    });

    /// <summary>
    /// Prints one job. Returns true when done (printed, already printed, or given up),
    /// false when it should be tried again later.
    /// </summary>
    public bool PrintOne(Guid jobId)
    {
        var row = db.Run(() =>
        {
            using var command = db.Command("SELECT payload_json, status, attempts FROM print_jobs WHERE id = $id;", ("$id", jobId.ToString()));
            using var reader = command.ExecuteReader();
            return reader.Read() ? (Job: PosJson.Deserialize<PrintJob>(reader.GetString(0)), Status: reader.GetString(1), Attempts: reader.GetInt32(2)) : default;
        });

        if (row.Job is null || row.Status != nameof(PrintJobStatus.Pending))
        {
            return true;
        }

        try
        {
            string? output;
            if (row.Job.Kind == PrintJobKind.DrawerKick)
            {
                output = outputs.OpenDrawer(settings.Receipt) ? "cash drawer" : "(no cash drawer)";
            }
            else
            {
                output = outputs.Send(Document(row.Job), settings.For(row.Job.Target), row.Job.Id);
            }

            db.Execute(
                "UPDATE print_jobs SET status = 'Printed', attempts = attempts + 1, printed_at = $at, output = $out, last_error = NULL WHERE id = $id;",
                ("$at", Iso(clock.GetUtcNow())), ("$out", output), ("$id", jobId.ToString()));
            return true;
        }
#pragma warning disable CA1031 // Any printer problem must be recorded and retried, never crash the hub.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var attempts = row.Attempts + 1;
            // A drawer is never opened late: one try, then the tills are told.
            var giveUp = attempts >= Math.Max(1, settings.MaxAttempts) || ex is KeyNotFoundException || row.Job.Kind == PrintJobKind.DrawerKick;
            db.Execute(
                "UPDATE print_jobs SET status = $s, attempts = $a, last_error = $e WHERE id = $id;",
                ("$s", giveUp ? nameof(PrintJobStatus.Failed) : nameof(PrintJobStatus.Pending)), ("$a", attempts), ("$e", ex.Message), ("$id", jobId.ToString()));
            if (giveUp)
            {
                live.PrintFailed(row.Job.Kind == PrintJobKind.DrawerKick
                    ? $"Could not open the cash drawer: {ex.Message}"
                    : $"Could not print {Describe(row.Job)}: {ex.Message}");
            }

            return giveUp;
        }
    }

    /// <summary>Puts a job back on the queue after a delay (used for retries).</summary>
    public async Task RequeueLater(Guid jobId, CancellationToken cancel)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.05, settings.RetrySeconds)), clock, cancel).ConfigureAwait(false);
        _queue.Writer.TryWrite(jobId);
    }

    /// <summary>Puts a job on the queue now.</summary>
    public void Enqueue(Guid jobId) => _queue.Writer.TryWrite(jobId);

    // ---------- Internals ----------

    private PrintJob Save(PrintJob job) => db.RunInTransaction(() =>
    {
        db.Execute(
            "INSERT INTO print_jobs (id, kind, target, order_id, status, created_at, payload_json) VALUES ($id, $k, $t, $o, 'Pending', $at, $json);",
            ("$id", job.Id.ToString()), ("$k", job.Kind.ToString()), ("$t", job.Target.ToString()), ("$o", job.OrderId?.ToString()),
            ("$at", Iso(clock.GetUtcNow())), ("$json", PosJson.Serialize(job)));
        db.AfterCommit(() => _queue.Writer.TryWrite(job.Id));
        return job;
    });

    private PrintDocument Document(PrintJob job)
    {
        switch (job.Kind)
        {
            case PrintJobKind.Receipt or PrintJobKind.ReceiptCopy:
                return ReceiptDocument(job.OrderId!.Value, job.Kind == PrintJobKind.ReceiptCopy);

            case PrintJobKind.ZReport:
                var report = tills.FindZReport(job.BusinessDate!.Value) ?? throw new KeyNotFoundException("Z report not found.");
                return Layouts.ZReport(report, settings.ReceiptText, access.StaffName);

            default:
                var order = orders.Get(job.OrderId!.Value);
                var lines = order.Lines.Where(l => job.LineIds.Contains(l.Id)).ToList();
                var kind = job.Kind switch
                {
                    PrintJobKind.KitchenVoid => KitchenTicketKind.Void,
                    PrintJobKind.KitchenAdditional => KitchenTicketKind.Additional,
                    _ => KitchenTicketKind.New,
                };
                return Layouts.KitchenTicket(order, lines, kind, settings.ReceiptText, hub.TimeZone);
        }
    }

    private PrintDocument ReceiptDocument(Guid orderId, bool isCopy)
    {
        var order = orders.Get(orderId);
        var servedBy = order.History.Count > 0 ? access.StaffName(order.History[0].UserId) : null;
        return Layouts.Receipt(order, tills.Ledger(order), settings.ReceiptText, hub.TimeZone, servedBy, isCopy);
    }

    private static string Describe(PrintJob job) => job.Kind switch
    {
        PrintJobKind.ZReport => "the Z report",
        PrintJobKind.Receipt or PrintJobKind.ReceiptCopy => "a receipt",
        _ => "a kitchen ticket",
    };

    private Guid NewId() => Guid.CreateVersion7(clock.GetUtcNow());

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>Prints queued jobs one at a time, in order; prints anything left waiting after a restart first.</summary>
public sealed class PrintWorker(PrintService print) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var id in print.PendingJobIds())
        {
            print.Enqueue(id);
        }

        await foreach (var jobId in print.Queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!print.PrintOne(jobId))
            {
                _ = print.RequeueLater(jobId, stoppingToken);
            }
        }
    }
}
