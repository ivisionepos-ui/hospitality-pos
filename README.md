# Hospitality POS Platform

Point of sale, kitchen display and online ordering for restaurants, coffee shops and takeaways.
UK first (GBP, UK VAT). English by default; Arabic names can be switched on per shop.

The full architecture and requirements are in the "Hospitality POS Platform – Architecture & SRS" document.

## Repository layout

```
src/
  Pos.Domain/            Business rules shared by the cloud and the Store Hub (no database, no UI)
    Common/              Money (pence), rounding, proportional sharing, weekly time windows
    Tax/                 UK VAT rules and VAT summaries
    Promotions/          Scheduled offers (e.g. Tuesday & Wednesday prices)
    MealDeals/           Sharing a deal price across its items
    Delivery/            Delivery charges by distance
    Pricing/             Order pricing: offers, extras, deals, discounts, delivery fee, service charge, VAT
    Menus/               Menu, Edit-menu rules, change log, undo, checking order choices
    Orders/              Orders, statuses, numbers, business day, duplicate-proof intake
  Pos.Hub/               Store Hub: runs on the shop PC, SQLite database, web API for tills and screens
    Printing/            Receipt, kitchen ticket and Z layouts; drawing (Arabic optional); PDF/PNG/Windows outputs; print queue
    wwwroot/pos/         The till screen: plain HTML, CSS and JavaScript served by the hub (no install, works offline)
    Backups/             Automatic checked backups, second copy, tidy-up, restore; one-hub-per-data-folder lock
    Data/                Database (schema upgrades, WAL), order and menu storage
    Api/                 Web API routes and request/response formats
tests/
  Pos.Domain.Tests/      Automated tests for Pos.Domain
  Pos.Hub.Tests/         End-to-end tests: real HTTP calls to a real hub with a real database
  ui/                    Till screen tests: a real browser (Playwright/Chromium) against a real hub
tools/
  windows/               Double-click launchers for the shop PC: start hub, print tests, restore a backup
docs/
  DECISIONS.md           Decision log
  MODULES.md             Completed modules and their test status
  CODING-STANDARDS.md    Naming and coding rules
```

Projects still to come: `Pos.Cloud.Api`, `Pos.Sync`, customer display, back office and ordering website.

## Requirements

- .NET SDK 10.0 (https://dotnet.microsoft.com/download)
- Git

## Build and test

```
dotnet build
dotnet test
```

Every change must build with zero warnings and pass all tests before it is merged.

## Running the Store Hub

```
dotnet run --project src/Pos.Hub
```

It listens on http://localhost:5080 (this PC) and https on port 5443 (shop network), and keeps its database in `data/hub.db`. Settings are in
`src/Pos.Hub/appsettings.json` under `Hub` (branch code, time zone, trading-day start, staff discount limits).

### First-time set-up

1. On the hub PC, start the hub, then send `POST /api/setup` with the owner's name, a PIN and a name for this PC.
   This only works on the hub PC itself, and only once. It returns this PC's device key (shown once) and logs the owner in.
2. To add a till or screen: the owner creates a 6-digit pairing code (`POST /api/devices/pairing-code`, valid 10 minutes),
   and the new device sends it with `POST /api/devices/pair`. The device receives its own key, shown once.
3. The owner or a manager adds staff with a role and a PIN (`POST /api/staff`).
4. Staff log in on a registered till with their PIN (`POST /api/auth/login`). Logins last 12 hours.

Every request (except health and set-up) must carry `X-Device-Id` and `X-Device-Key`. Anything that changes data also
needs a staff login: `Authorization: Bearer <token>`. Where a manager must approve (removing an item already in the
kitchen, cancelling), the manager types their PIN on the same till and it is sent as `X-Manager-Pin`.

Five wrong PINs on a device lock PIN entry on that device for 5 minutes. Ten wrong pairing codes block pairing for 10 minutes.

### Connections

| Port | What | Who can reach it |
| --- | --- | --- |
| 5080 (HTTP) | Set-up and local tools | This PC only |
| 5443 (HTTPS) | Tills, kitchen screens, customer displays | The shop network, encrypted |

The hub creates its own small certificate authority (the "shop CA") the first time it starts, and issues itself an
HTTPS certificate for this PC's name and network addresses. It renews that certificate automatically a month before it
ends, or when the PC's address changes; tills do not need anything new when that happens.

**Another Windows PC as a till or kitchen screen (the easy way):** never open `localhost` on it (that means "this PC",
where no hub runs). On the hub PC run `Allow tills to connect (hub PC, once).bat` (Windows firewall rule for port 5443,
local network only). On the hub's till, More → Add a till or screen shows the hub's network address(es)
(`networkAddresses` in `/api/health`), the certificate check code and a 6-digit code. On the other PC run
`Connect this PC to the hub (other tills).bat`: it downloads `/ca.crt`, shows its check code to compare, installs it for
the Windows user, saves the address in `hub-address.txt` (used by `Open till (full screen).bat`) and opens the till.

**Setting up a till to trust the hub by hand (tablets, once per till):**

1. On the hub PC, open http://localhost:5080/api/health and note the `caCheckCode` (e.g. `A5F1-0592-6B9D-AB91`).
2. Copy http://localhost:5080/ca.crt to the till (USB stick or shared folder) and install it as a trusted root certificate:
   - Windows: double-click it → Install Certificate → Local Machine → "Trusted Root Certification Authorities".
   - Android / Sunmi: Settings → Security → Encryption & credentials → Install a certificate → CA certificate.
   - iPad: open the file, install the profile in Settings, then turn it on in Settings → General → About → Certificate Trust Settings.
3. On the till, check that the certificate's SHA-256 fingerprint starts with the same check code.
4. Give the hub PC a fixed address in the router (DHCP reservation), then point the till at https://<hub address>:5443.

Files next to the database: `hub.db` (data), `hub.key` (protects PINs and certificate files), `hub-ca.pfx` and
`hub-tls.pfx` (certificates). The hub backs all of them up together (see Backups); keep backups safe, as they hold
the shop CA's private key.

### Till screen
Open `http://localhost:5080/` on the hub PC, or `https://<hub address>:5443/` on another till (after installing the
shop CA). The screen is plain HTML, CSS and JavaScript in `src/Pos.Hub/wwwroot/pos`, served by the hub: nothing to
install on tills, no internet needed, and a hub update reaches every till on the next reload.

- **First time:** on the hub PC the screen offers the first-time set-up (owner, PIN, till name). Other tills are
  registered with a 6-digit code from More → Add a till (owner). The till's device key is kept in the browser.
- **Login:** staff PIN on a keypad. Wrong PINs lock out after several tries (hub rule).
- **Taking orders:** Takeaway / Eat in / Phone; colour-coded category tabs; tap items. Items with sizes or extras
  open a panel that enforces required choices (e.g. kebab sauce); meal deals step through their choices. Lines can
  be changed (+/−, note, remove) until sent. Totals (offers, deals, VAT) come from the hub as you go (`/orders/quote`).
- **Send / Pay:** Send saves the order and prints the kitchen ticket. Pay does the same, then takes cash (quick notes,
  change shown big) or card (key the amount into the Worldpay terminal, then confirm). Part payments and split
  card/cash work; each payment has its own id so a retry never charges twice. The receipt prints when fully paid.
- **Orders:** today's orders with status and paid/due. Open one to add items (Send new), remove items (a manager's PIN
  if already in the kitchen; the kitchen gets a void slip), pay, reprint the receipt, cancel, or mark Collected.
- **Edit menu (managers):** More → Edit menu. Same layout as taking orders, with an amber bar so it is never
  mistaken for selling. Tap an item to change its name, button name, sizes and prices (price keypad), VAT type,
  extras, sold out or hidden, or move it earlier/later or to another category. Tap the selected category tab to
  change its name, colour, food type (receipt order), hidden or position. "+ Add item", "+ Category", new extras and
  extra choices with prices. Undo last and the change log are in the bar. Changes show on every till at once; if
  two people edit the same item, the second save is refused (nothing is overwritten) and the latest is shown.
- **Phone orders and delivery:** choose Collection or Delivery. The customer panel finds past customers as you type
  part of their phone number (name, address and driver notes fill in). For delivery: house number and street,
  postcode and **Check postcode**, which gives the distance from the shop (free postcodes.io data, remembered by
  the hub so repeat postcodes work without internet). No internet: type the miles. The delivery charge comes from
  the distance bands; Send and Pay are blocked, with the reason, when the order is under the minimum or too far,
  unless a manager agrees a different fee (Change delivery fee, manager PIN). Sending a delivery prints the kitchen
  ticket and the receipt for the bag, with the customer, address, notes and AMOUNT DUE (or PAID). Address and
  distance can be corrected on a saved order (⋯ → Change customer or address).
- **Sold out items (everyone taking orders):** More → Sold out items, one tap per item.
- **More:** open/close the till with a blind cash count, paid in/out, X and Z reports, failed printouts, back up now,
  add a till (each shown only to roles allowed to use it).
- **Live:** orders, payments and menu changes (e.g. sold out) appear on every till at once; printer and backup
  failures pop up. After a network drop the till reconnects and catches up by itself.
- **Security:** the page may load only its own files and talk only to the hub (Content-Security-Policy); all text from
  the menu is shown as text, never as HTML.
- **Try it:** with an empty menu, managers see "Load sample menu" (a typical takeaway menu; refused once a menu exists).

Till screen tests: after a Release build, `npm install --prefix tests/ui`, `npx --prefix tests/ui playwright install chromium`,
then `node --test --test-concurrency=1 tests/ui/till.test.cjs`. They start their own hub on a temporary folder.

### Kitchen screen
Any PC, tablet or Android box with a browser. Register it like a till (More → Add a till or screen gives the code;
choose **Kitchen screen**), then a kitchen staff member logs in with their PIN. Kitchen staff logging in on any
device also get the kitchen board.
- Orders appear the moment the till sends them (live), oldest first, with order number, type, customer name for
  phone orders, every item with size, extras, deal choices and notes highlighted. A beep sounds for each new order.
- The timer turns amber after 10 minutes and red after 20. Items added later are marked NEW for 3 minutes; removed
  items are crossed out "REMOVED – DO NOT MAKE". Tapping an item ticks it off (this screen only).
- **Start** (amber card = preparing) and **Ready** (off the board, into the Ready bar; the tills are told
  "Order 012 is ready"). **Recall** puts an order marked ready by mistake back on the board (recorded in its history).
- **Sold out** is on the kitchen screen too.

### Delivery settings
In `appsettings.json` → `Delivery` (the shop postcode is set in More → Shop details): `MaximumMiles`,
`MinimumOrderPence`, `FreeOverPence`, and either `Bands` (from/to miles, fee, optional minimum order per band) or
`RatePerMilePence` + `MinimumFeePence`. Distances are straight-line × `RoadFactor` (1.3) until a road-distance
service is added.

### Address lookup: Google, Royal Mail online, or the local Royal Mail file
The owner chooses in More → Address lookup (stored in the hub database, table `settings`):
1. **Google** — Places API (New) autocomplete + place details; any text; needs a Google Maps Platform key with
   Places API (New) enabled. Gives the location, so the distance comes straight from it. "Results from Google" is
   shown with the results.
2. **Royal Mail online** — AddressNow (Loqate Capture: Find v1.10 / Retrieve v1.20, json3); a postcode opens into its
   addresses; needs an AddressNow / Loqate key; latitude/longitude requested so the distance comes from it.
3. **Local Royal Mail file** (default) — the PAF files below; postcode, postcode + words, or words alone; no
   internet, no cost per search.
Keys are kept on the hub only (never sent back to a till; the screen shows the last 4 characters) and each service has
a **Test** button. If the chosen online service fails (no internet, wrong key, out of credit), the till falls back to
the local file and says why (switchable). Endpoints and settings `Delivery:GoogleEndpoint` /
`Delivery:RoyalMailEndpoint` exist so tests can use stand-ins; they never need changing in a shop.

### Address files (house-by-house addresses)
Royal Mail Postcode Address File (PAF) CSV files, one per postcode district (e.g. `B12.csv`), 14 columns without a
header: organisation, department, PO box, building name, sub-building, building number, dependent thoroughfare,
thoroughfare, double dependent locality, dependent locality, post town, postcode, postcode type, delivery point
suffix. Put them in the `addresses` folder next to the hub (setting `Delivery:AddressFolder`): new or changed files
load when the hub starts. Or upload them from the till: More → Address files (owner). A reload replaces that file's
addresses in one transaction; a bad file changes nothing. PO-box-only rows are skipped (nowhere to deliver).
On the till, Check postcode lists every address in the postcode (works without internet); typing narrows the list;
tapping fills street, flat/building/business and town. The files are licensed Royal Mail data and are never
committed to the repository.

### Backups
Each backup is one zip in `data/backups` named `hub-backup-<branch>-<yyyyMMdd-HHmmss>.zip` (shop time). It holds a
consistent snapshot of the database taken with SQLite's backup (never half a change), the PIN key and both
certificates, plus `backup.json` listing every file with its SHA-256 fingerprint. Each backup is integrity-checked
and its fingerprints re-read before it is given its final name, so a half-written file never looks like a backup.

- **When:** at start-up if the newest backup is older than the interval; every `IntervalMinutes` (60) if anything
  changed; straight after each Z report; on demand (`POST /api/backups`).
- **Second copy:** set `Backup:CopyFolder` to a USB stick, second disk or network folder. Each copy is checked too.
  If the copy fails (stick unplugged) the main backup is still kept.
- **Warnings:** a failed backup or copy is logged and sent live to the tills (`BackupFailed`).
- **Kept:** every backup from the last 48 hours, then the last of each day for 30 days, never fewer than 3.
  Files not named like backups are never touched.
- **Upgrades:** before a new version upgrades the database, a copy is kept as `hub.db.before-upgrade-v<N>-<time>.db`.
  A database from a newer version is refused rather than changed.
- **Restore:** close the hub, then `Pos.Hub --restore data/backups/<file>.zip` (or drag the file onto
  `3 - Restore a backup (drag a backup onto this).bat`). The backup is checked first; the files it replaces are
  moved to `data/before-restore-<time>/`, never deleted. Refused while the hub is running.
- **One hub per data folder:** the hub holds `data/hub.lock` while running, so a second copy (or a restore) cannot
  use the same database at the same time.

### Live updates

Tills and screens keep one live connection open to the hub (SignalR over WebSockets at `/live`):

1. `POST /api/live/ticket` (device headers) returns a ticket valid for 60 seconds and the latest event number.
2. Connect to `/live` with the ticket as `access_token`. The hub sends `event` messages:
   `OrderCreated`, `OrderUpdated` (with the whole order as it is now) and `MenuChanged` (reload the menu).
3. Every event has a number (`seq`) one higher than the last. If a screen sees a gap, or reconnects after being
   away, it calls `GET /api/live/events?since=<last seq seen>` (repeat while `more` is true).

Tills and the back office get every event; kitchen screens get accepted orders and menu changes; customer displays
get menu changes. Events are saved in the same database transaction as the change, and only sent once it is saved,
so a screen never sees something that did not happen. A removed device is disconnected at once.

### Printing

**Thermal printers and the cash drawer (More → Printers, owner).** For each of the receipt and kitchen printers choose:
**Network printer** (a thermal printer on the shop network: type its address from its self-test page, e.g.
192.168.1.50; port 9100 unless given), **USB printer** (a thermal printer installed in Windows on the hub PC, sent
ESC/POS straight through its driver), **Windows printer** (any printer, printed as a picture, no cut or drawer),
**Save as PDF** or **Off**; paper 80 or 58 mm; cut on or off. Thermal printers are sent ESC/POS: the printout is drawn
exactly as the PDF and sent as a picture (GS v 0, in bands of 255 rows), so the £ sign, bold text and layout never
depend on the printer's character set; then feed and partial cut. Choose the **printer type**: **ESC/POS** for Epson (e.g. TM-m30), Xprinter
(e.g. XP-Q801) and most others; **Star TSP100 / TSP143** for Star's picture-only printers, which do not understand
ESC/POS and get STAR Graphic Mode instead (ESC * r R, ESC * r A, ESC * r P "0", ESC * r E "13" for feed and partial cut,
one "b n1 n2" line per row of dots, ESC FF EOT, ESC * r B; drawer by BEL, socket 1 only). **Test receipt printer / Test kitchen printer / Test drawer**
print or open straight away and say why if they cannot.

**Several kitchen printers.** Up to 10 kitchen printers, each with a name (printed at the top of its tickets when
there is more than one), its own connection, and what it prints: **Everything**, or **Only what I choose**
(categories, plus single items marked **＋ Also here** or **✕ Not here**). An item prints on **every** printer that
chose its category or marked it "also here", except where it is "not here"; "Everything" printers always print it; so
one item can print on several printers (move an item: "also here" on one printer, "not here" on the other). An item
no printer prints goes to the **first** kitchen printer, so nothing is missed. A **meal deal** goes whole to a printer that chose the deal (or
its category); otherwise it is split, each printer getting the deal marked "(part)" with only its items. Additions
and void slips go to the same printers. Kitchen PDF files carry the printer's name. Printer set-ups saved by earlier
versions load as one "Kitchen" printer that prints everything; 0.16.0 set-ups (where a chosen item moved) get "not here" on the
other printers, so they print exactly as before.

The **cash drawer** plugs into the receipt printer (socket pin 2, or 5). It opens for cash payments, cash refunds and
paid in/out (can be turned off), through the print queue so it never interrupts a printout, and is tried once only
(never opens late; the tills are told if it could not open). **Open drawer (no sale)** (More → Till) needs a reason
and a manager's PIN and is recorded; the X report lists today's no-sale openings.


Receipts, kitchen tickets and Z reports are drawn as pictures at thermal-printer width (80 mm or 58 mm), so Arabic
prints correctly on any printer. Each printer in `appsettings.json` → `Printing` has an `Output`:

| Output | What happens | Use it for |
| --- | --- | --- |
| `Pdf` (default) | A PDF the width of the paper roll is saved in `Folder` | Testing without printers, on any PC |
| `Png` | A picture is saved in `Folder` | Checking layouts |
| `Windows` | Sent to the Windows printer called `PrinterName` | Microsoft Print to PDF now; any Windows-driver printer later |
| `None` | Nothing is printed | Turning a printer off |

**Using Microsoft Print to PDF** (the hub must run on Windows):

```json
"Printing": {
  "Receipt": { "Output": "Windows", "PrinterName": "Microsoft Print to PDF", "PaperWidthMm": 80, "Folder": "C:\\POS\\Receipts" },
  "Kitchen": { "Output": "Windows", "PrinterName": "Microsoft Print to PDF", "PaperWidthMm": 80, "Folder": "C:\\POS\\Kitchen" }
}
```

For a printer whose name contains "PDF", the hub tells Windows the file name in advance, so PDFs are saved into
`Folder` without a "Save as" window. The receipt prints at its real width (72 mm) at the top of an A4 page; long
receipts continue on the next page.

**Checking a printer:** `Pos.Hub --print-test` prints a sample receipt and kitchen ticket on the configured printers
and says where they went (or why they failed), then stops. Settings can be given on the command line, e.g.
`Pos.Hub --print-test --Printing:Receipt:Output=Windows --Printing:Receipt:PrinterName="Microsoft Print to PDF"`.

When printing happens:
- **Kitchen ticket** when items are sent to the kitchen ("NEW ORDER", then "ADDITIONAL ITEMS" for later items).
- **Void slip** when an item already in the kitchen is removed.
- **Receipt** when an order is completed; asking again prints a copy marked **COPY**.
- **Z report slip** on request, once the Z report has been taken.

Prints are queued and saved with the change that asked for them, then printed in order. If a printer fails, the hub
tries again (`MaxAttempts`, `RetrySeconds`), then marks it failed, tells the tills (`PrintFailed` live event), and
staff can retry it. Printer problems never hold up taking orders. The cash drawer opens only on a real receipt
printer (to come). The `Pdf` output embeds the font, so each file is about 0.7 MB; clear the folder now and then.

Shop name, address, postcode, phone, VAT number and receipt message are entered by the owner on the till in
**More → Shop details** (stored in the hub database; used at once for receipts, and the postcode for delivery
distances). Until then the values under `Printing` → `ReceiptText` and `Delivery:ShopPostcode` in appsettings.json
are used. Language `en` (default), `ar` or `both` stays in appsettings.json. UK VAT receipts must show the VAT number when the business is VAT registered.

### Menu spreadsheet (Excel)

The quickest way to put a whole menu on the till. **More → Menu spreadsheet (Excel)** (staff who can edit the menu):

1. **Download the menu spreadsheet**: `menu.xlsx` with the menu as it is now (examples if it is empty), an
   **Extras** sheet and a **How to** sheet.
2. Fill it in with Excel (or any program that saves .xlsx or .csv): **one row per size of each item**: Category,
   Item, Size (empty for a single price), Price (incl. VAT), VAT (**Hot food**, **Cold food** or **Standard**),
   Description, Extras (group names from the Extras sheet, separated by ;), Sold out (Yes or empty). An empty
   Category or Item carries on from the row above. Extras sheet: Group, Extra, Price, Choose at least, Choose at most.
3. **Choose the filled-in file.** The till shows what will change (new and changed items, price changes like
   "12" £11.99 → £12.49") or every problem with its row ("Menu row 14: Price "eight" is not a price"). Nothing
   changes until there are no problems and **Import** is pressed.

Items, sizes, categories and extras already on the till are found **by name** and keep their ids, so kitchen printer
choices, reports and open orders stay linked, and importing the same file twice changes nothing. A column left out of
the sheet is left as it is on the till. Items on the till that the sheet does not list are **left as they are**, or
**hidden** if chosen (never deleted). Meal deals are set up in Edit menu and are never changed by a sheet. A
**backup is made before every import**, and **Undo last import** puts the menu back exactly, as long as nothing has
changed it since. Files: .xlsx (Excel 2007 and later) or .csv, at most 2 MB; old .xls files are refused with how to
save them as .xlsx. Only cell values are read (no formulas or macros run).

### Money rules

- Every payment goes on the open shift of the till it was taken on. Tills only (not kitchen screens or displays).
- An order can be completed only when fully paid, and cancelled only after anything paid is refunded.
- Nobody can pay more than is owed; cash change is worked out by the hub. Card tips are recorded separately.
- Refunds go back the same way they were paid, never more than was paid, always with a reason and a manager.
- Paid-in and paid-out need a manager and a reason; a paid-out cannot take the drawer below zero.
- Closing a shift is a blind count: the expected cash and over/short are shown only after the count is entered.
- The Z report needs every shift of the day closed; it is kept, numbered, and taking it again returns the same one.

### Sales reports (back office)

More → **Sales reports** (managers and owners): Today, Yesterday, This week, Last 7 days, This month, Last month, or
any dates (up to 366 days). Tabs: **Summary** (sales, orders, average order, net sales after refunds, offers,
discounts, delivery and service charges, cancels, removed items, by order type and source), **Items** and
**Categories** (sold and sales after their share of discounts), **Hours** and **Days** (bars), **Staff** (orders
taken, discounts given, items removed, refunds made), **Payments** (by method, with refunds and tips) and **VAT**
(after refunds for what was sold). **Save as spreadsheet** saves the table on screen as a CSV file that opens in
Excel (cells that could run as formulas are made plain text). Sales are completed orders by trading day; refunds and
payments by the day they were made. Each day's Z report stays the record for that day.

### Split bill

On the payment screen choose **Whole bill**, **Equal shares** or **By items**:
- **Equal shares**: set how many people; the till shows what each pays, to the penny (the first shares take any odd
  penny), and works the shares out again from what is left after each person pays.
- **By items**: tap what this person had; the amount includes their fair share of any discount and delivery charge.
  Each item can be paid for once only (the hub refuses a second payment for it, and the amount must be exactly the
  items' share). An item payment refunded in full frees the item again. A line with a quantity (e.g. 2 × Coke) is
  paid as one line; to share it, use equal shares.
Cash or card works with every option; the order is paid when nothing is left.

### Deliveries and drivers

Add drivers in More → Staff with the role **Driver**. On the till, **Drivers** shows:
- **Ready to go out**: delivery orders sent to the kitchen and not yet out. Tap the orders, then the driver (drivers
  first; "Someone else" lists other staff, as managers sometimes deliver). They become **Out with driver**.
- **Out now**, by driver, with what to collect. When the driver is back: **Cash £x** (taken into this till's drawer,
  so the cash count is right), **Card** (paid on the driver's card machine), **Delivered** (already paid), or **Back**
  (not delivered, with a reason: it is ready in the shop again, with no driver).
- **Today**, per driver: delivered, out, cash handed in and card taken (only money taken after the order went out, so
  a phone payment taken before is not counted as the driver's).

A driver who logs in with their PIN sees **My deliveries**: their orders with name, address, phone (tap to call),
notes and what to collect, updated live. Drivers cannot change anything. An order goes out only after everything on it
has gone to the kitchen; only delivery orders go out; a removed staff member cannot be given deliveries; an unpaid
delivery cannot be marked delivered.

### Discounts and refunds

**Discount** (button under the order, or ⋯ on a saved order): % off (quick 5–100%) or £ off, with a reason that
prints on the receipt. A new order is saved first but not sent to the kitchen (Send or Pay sends it). Cashiers and
waiters give discounts alone up to the owner's limits (More → Discount limits; the shipped setting is 0 = always a
manager); above either limit a manager enters their PIN. Managers and owners never need a second PIN. The limit is
checked once, when the discount is given: an order that grows later, or a change of limits, never undoes it. A
discount that would take the total below what has already been paid is refused (give a refund instead).

**Refund** (⋯ on an order that has been paid, today's or an earlier day's via Orders → Previous day): choose the
payment, the amount (all or part), a reason; a manager approves. Cash comes out of this till's drawer; for a card the
till reminds staff to do the refund on the card machine (until the card machine is linked). Delivery-app and online
payments are refunded through the app. A receipt showing the refund can be printed.

How refunds count: money given back for what was sold (a complaint, a return) lowers the sale and its VAT, and the
customer does not owe it again. Money given back because more was paid than the order now comes to (an item removed
after paying) does not lower the sale again, because the order total already went down. Each refund records which part
is which. Refunds of orders that end up cancelled or rejected take nothing off the day's sales or VAT (those orders
were never counted as sales).

### Roles

| Role | Can do |
| --- | --- |
| Kitchen | Kitchen screen: mark orders preparing and ready; mark items sold out |
| Driver | My deliveries screen only (read only) |
| Waiter | Take orders, remove items not yet in the kitchen |
| Cashier | Waiter + accept/reject online orders |
| Manager | Everything in the shop: approvals, cancels, menu, staff below manager, reports |
| Owner | Everything, including devices and managers |

**Staff screen** (More → Staff, managers and owners): add staff (name, role, PIN twice), edit name or role, set a
new PIN for someone who forgot theirs, remove (logged out everywhere at once; records kept) and bring back with a new
PIN. Rules: nobody changes their own role or removes themselves (so there is always an owner); managers manage only
cashiers, waiters and kitchen staff; a role change logs the person out so the new permissions apply at once; two
active staff cannot share a name; PINs are 4–8 digits, not easy (1111, 1234) and unique. Every change is recorded
(who, whom, what, when; never the PIN) and shown under History / All changes.
**Change my PIN** (everyone, also on the kitchen screen) needs the current PIN; wrong ones count towards the
5-wrong-PINs lockout. **Tills and screens** (owner) lists registered devices and removes a lost or replaced one
(not the one in use).

### API (version 1)

| Method | Route | Who | What it does |
| --- | --- | --- | --- |
| GET | /api/health | anyone | Is the hub running, and the shop CA check code |
| POST | /api/live/ticket | any device | 60-second pass for the live connection |
| GET | /api/live/events?since=N | any device | Events missed since N |
| GET | /api/shifts/current | cashier | The till's open shift |
| POST | /api/shifts/open, /api/shifts/close | cashier | Open with a float; close with a blind cash count |
| POST | /api/shifts/paid-in, /paid-out | manager (or manager PIN) | Cash in or out of the drawer, with a reason |
| GET | /api/shifts/current/x-report | manager | Figures so far for this till's shift |
| GET | /api/orders/{id}/payments | cashier | Total, paid, balance, tips, payments |
| POST | /api/orders/{id}/payments/cash, /card | cashier | Take cash (change worked out) or record a card payment; safe to resend |
| POST | /api/orders/{id}/payments/{paymentId}/refund | manager (or manager PIN) | Refund all or part of a payment, same method |
| GET | /api/drivers | staff taking orders | People who can take deliveries (drivers first) |
| POST | /api/orders/{id}/send-out | staff taking orders | Send a delivery out with a driver (or hand to another) |
| POST | /api/orders/{id}/back-to-shop | staff taking orders | Driver brought it back (reason) |
| GET | /api/drivers/today | staff taking orders | Each driver's day |
| GET | /api/reports/sales?from=&to= | manager, owner | Sales report for a range of trading days |
| GET | /api/reports/sales.csv?from=&to=&table= | manager, owner | One table as CSV (summary, items, categories, hours, days, types, staff, payments, vat) |
| GET / PUT | /api/settings/printers | owner | Printer choices (and printers installed in Windows) |
| POST | /api/settings/printers/test | owner | Print a test page now on the receipt or kitchen printer |
| POST | /api/settings/printers/test-drawer | owner | Open the cash drawer now |
| POST | /api/drawer/open | staff (manager approves) | No sale: open the drawer, with a reason; recorded |
| GET | /api/drawer/opens?date= | manager, owner | No-sale openings of a day |
| POST / DELETE | /api/orders/{id}/discount | staff (manager above the limits) | Give or remove an order discount |
| GET / PUT | /api/settings/discounts | staff / owner | Discount limits for staff |
| GET | /api/orders/{id}/split?parts=N | cashier | Split the balance equally |
| POST | /api/orders/{id}/split-by-items | cashier | Amount for chosen items |
| POST | /api/reports/z | manager | End-of-day Z report (all shifts must be closed); starts a backup |
| GET | /api/backups | manager | Backup status, last result, backups in both folders |
| POST | /api/backups | manager | Make a backup now |
| POST | /api/orders/{id}/print-receipt | cashier | Print the receipt (COPY if printed before); it also prints by itself when an order is fully paid |
| GET | /api/orders/{id}/receipt?format=png\|pdf | cashier | Receipt preview, not printed |
| POST | /api/reports/z/print | manager | Print the Z report slip |
| GET | /api/print-jobs?status= | cashier | Print jobs (e.g. Failed) |
| POST | /api/print-jobs/{id}/retry | cashier | Try a failed print again |
| GET | /ca.crt | anyone | The shop CA's public certificate, to install on tills |
| GET/POST | /api/setup | hub PC, once | First-time set-up |
| POST | /api/devices/pairing-code | owner | Code to register a new till or screen |
| POST | /api/devices/pair | new device | Register with a pairing code |
| GET / DELETE | /api/devices[/{id}] | owner | List or remove devices |
| POST | /api/auth/login, /api/auth/logout | device / staff | PIN login, logout |
| GET | /api/auth/me | staff | Who is logged in and what they may do |
| GET / POST / PUT / DELETE | /api/staff[/{id}] | manager, owner | List, add, edit name and role, remove staff |
| POST | /api/staff/{id}/reactivate | manager, owner | Bring back a removed person with a new PIN |
| GET | /api/staff/history[?staffId=] | manager, owner | Record of staff changes |
| GET | /api/delivery/check?postcode= | cashier | Postcode check: tidy postcode, town, distance from the shop, and the addresses in it (offline: addresses without distance; 503 only if neither is known) |
| GET | /api/delivery/settings | any device | Delivery bands, minimum order, maximum distance |
| GET | /api/customers?phone= | cashier | Past customers whose phone contains these digits (4+) |
| GET | /api/addresses?postcode= | cashier | Every address in a postcode, from the shop's address files |
| GET | /api/addresses/search?q=&group=&session= | cashier | "Find address" with the chosen service (falls back to the local file) |
| GET | /api/addresses/resolve?id=&session= | cashier | Full address (and distance) for a Google or Royal Mail suggestion |
| GET / PUT | /api/settings/shop | staff / owner | Shop name, address, postcode, phone, VAT number, receipt message |
| GET | /api/delivery/locate?postcode= | owner | Is this a real postcode, and where (Shop details) |
| GET / PUT | /api/settings/address-lookup | owner | Chosen service, keys (write-only), fallback |
| POST | /api/settings/address-lookup/test?provider=&q= | owner | Try a service with the saved key (no fallback) |
| GET / POST | /api/addresses/files[?name=B12.csv] | owner | Address files loaded / upload one (body = the CSV) |
| POST | /api/addresses/files/reload | owner | Load new or changed files from the addresses folder |
| POST | /api/orders/{id}/contact | cashier | Change the customer's details on an open order |
| POST | /api/orders/{id}/delivery | cashier (manager for a changed fee) | Change the distance or fee on an open delivery order |
| POST | /api/staff/{id}/pin | self (with current PIN), manager | Change or reset a PIN |
| GET | /api/menu, /api/menu/changes?since=N | any device | Menu and change log |
| PUT | /api/menu | manager | Import a whole menu (checked) |
| POST | /api/menu/products/{id}/price, /rename | manager | Change price or name |
| POST | /api/menu/products/{id}/sold-out | cashier, kitchen, manager | Sold out / back in stock |
| PUT | /api/menu/products/{id} | manager | Save an item's edit panel (refused if changed elsewhere since loaded) |
| POST | /api/menu/products | manager | Add an item |
| POST | /api/menu/products/{id}/move | manager | Move an item (position, optional category) |
| POST / PUT | /api/menu/categories[/{id}] | manager | Add a category / save its panel |
| POST | /api/menu/categories/{id}/move | manager | Move a category tab |
| PUT | /api/menu/modifier-groups/{id} | manager | Add or save a group of extras |
| GET | /api/menu/spreadsheet | manager | The menu as menu.xlsx (Menu, Extras and How-to sheets) |
| POST | /api/menu/spreadsheet/preview?name=&missing=keep\|hide | manager | Body: the .xlsx/.csv file. What an import would do, or its problems by row; changes nothing |
| POST | /api/menu/spreadsheet/import?name=&missing=keep\|hide | manager | Backs up, then imports (refused if the file has problems or changes nothing) |
| GET / POST | /api/menu/spreadsheet/last, /undo | manager | The last import; undo it (only while the menu is unchanged since) |
| POST | /api/menu/undo | manager | Undo the last menu change |
| POST | /api/orders | cashier, waiter | New order from what was picked (with contact and delivery for phone orders; a changed delivery fee needs a manager); safe to resend |
| POST | /api/orders/quote | cashier, waiter | Totals for an order being built; nothing saved, no number used |
| POST | /api/menu/sample | manager | Load the sample takeaway menu (empty menu only) |
| GET | /api/orders?date=&status=, /api/orders/{id} | any device | Orders for a trading day, one order (with paid and balance) |
| GET | /api/orders/{id}/history | manager | Everything that happened to it |
| POST | /api/orders/{id}/lines | cashier, waiter | Add items to an open order; with a requestId, safe to resend |
| POST | /api/orders/{id}/status | depends on status | Accept/reject (cashier), preparing/ready/recall (kitchen), complete, cancel (manager) |
| POST | /api/orders/{id}/send-to-kitchen | cashier, waiter | Send new items to the kitchen |
| POST | /api/orders/{id}/lines/{lineId}/void | cashier; manager if in the kitchen | Remove an item |

Refusals come back as `{ "code": "...", "message": "..." }` with 400 (invalid), 401 (not registered or not logged in),
403 (role not allowed, or needs approval), 429 (locked out),
404 (not found), 409 (not allowed now / duplicate key) or 422 (choices not valid, with a list of problems).
