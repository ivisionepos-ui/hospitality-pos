# Decision log

The master list (D-001 to D-022) is in the Architecture & SRS document. Decisions made while coding are recorded here.

| ID | Date | Decision | Reason |
| --- | --- | --- | --- |
| C-001 | 2026-11-02 | Target .NET 10 (current LTS) | Long-term support until late 2028 |
| C-002 | 2026-11-02 | Money as `long` pence in a `Money` value type, currency checked on every operation | No rounding errors; mixing currencies is caught |
| C-003 | 2026-11-02 | Rounding: half away from zero, whole pence | Usual UK commercial rule; one rule everywhere |
| C-004 | 2026-11-02 | VAT extracted from VAT-inclusive prices and rounded once per rate per order | UK menus show VAT-inclusive prices; summary always equals the total |
| C-005 | 2026-11-02 | UK VAT categories: HotFood, ColdFood, AlwaysStandard. Delivery treated like takeaway | Matches VAT Notice 709/1; to be confirmed with the accountant |
| C-006 | 2026-11-02 | Offers apply to the item or size price only; extras are always at normal price | Matches how shops advertise offers; simpler to explain on receipts |
| C-007 | 2026-11-02 | Offer conflicts: highest priority wins, then the lowest price for the customer; offers never stack | Predictable; stacking can be added later if needed |
| C-008 | 2026-11-02 | Time windows that pass midnight belong to the day they start | "Friday 22:00–02:00" means Friday night |
| C-009 | 2026-11-02 | Meal deal price shared by normal prices using the largest-remainder method; upcharges and extras stay with their own item | Shares add up to the penny; VAT correct per item |
| C-010 | 2026-11-02 | Order discount, delivery fee and compulsory service charge are spread across items by value (largest remainder) | Each VAT rate gets its fair share; per-item reports show the discount |
| C-011 | 2026-11-02 | Order discounts need a reason; above the staff limit (% or £) they need a manager. Default: every discount needs a manager | Control and audit; safe default |
| C-012 | 2026-11-02 | Discounts apply to food and drink only, after scheduled offers; never below zero | Delivery and service charges are not discounted |
| C-013 | 2026-11-02 | Delivery bands include their start and exclude their end; the last band includes its end | "3–5 miles" covers exactly 5 miles; no gaps or double matches |
| C-014 | 2026-11-02 | Free-delivery and minimum-order checks use the food total after the order discount | The customer's actual spend decides |
| C-015 | 2026-11-02 | Delivery fee VAT follows the food it delivers (apportioned) | HMRC treats delivery as part of the supply; accountant to confirm |
| C-016 | 2026-11-02 | Optional service charge is outside the scope of VAT; compulsory service charge carries VAT. Service charge is worked out after the discount | UK VAT treatment; matches what the customer actually pays |
| C-017 | 2026-11-02 | The pricer reports "too far" and "below minimum order" but does not refuse; the till screen blocks or warns | The business rule (refuse or warn) is a setting for the screen |
| C-018 | 2026-11-02 | Menu records are immutable; every edit replaces the record and is logged with before/after state, user, time and a menu version number | Gives undo, a full audit log and a simple way for tills to catch up |
| C-019 | 2026-11-02 | The edit panel saves the whole item; position and category change only through Move (drag and drop) | Matches the Edit menu screen; keeps positions consistent |
| C-020 | 2026-11-02 | Undo steps back one change at a time and refuses if the item was changed again elsewhere | Never silently overwrites another till's change |
| C-021 | 2026-11-02 | Every item has at least one size; size ids are unique across the menu | Offers and deals can point at a size unambiguously |
| C-022 | 2026-11-02 | Meal-deal options can point at a size, a product or a category; the most specific match sets the upcharge | "12-inch only", "any kebab", "any drink, +50p for a bottle" all expressible |
| C-023 | 2026-11-02 | Arabic names are optional; English is used when Arabic is missing | Menus can be built in English first |
| C-024 | 2026-11-02 | Order statuses: Pending → Received → Accepted → Preparing → Ready → Completed, plus Cancelled and Rejected; only listed moves allowed | Matches the SRS; Pending exists only in the cloud until the shop acknowledges |
| C-025 | 2026-11-02 | Orders taken by staff start Accepted; website and delivery-app orders start Received and must be accepted before the kitchen sees them | Staff orders are accepted by definition; online orders need a human check |
| C-026 | 2026-11-02 | Order numbers restart each business day per branch ("045"); a full reference adds branch and date ("LDN1-261103-0045") | Easy to call out; still unique |
| C-027 | 2026-11-02 | Business day starts at a set time (e.g. 05:00) in branch local time | Late-night orders count towards the evening's trading |
| C-028 | 2026-11-02 | Duplicate prevention: each submission carries a key; a repeat with the same content returns the stored order, a reused key with different content is refused | Safe retries after dropped connections, without hiding mistakes |
| C-029 | 2026-11-02 | Order lines keep the price and names from when they were added; removed lines stay on the order, marked void with a reason | Menu changes never alter orders; full audit trail |
| C-030 | 2026-11-02 | Voiding an item already sent to the kitchen, or cancelling once food has gone to the kitchen, needs a manager | Prevents waste and theft |
| C-031 | 2026-11-02 | Delivery-app orders use the app's prices; our offers are not applied | Apps run their own promotions and pay us their price |
| C-032 | 2026-11-02 | Offers on an order use the time it was created, even for items added later to an open table | One consistent price basis per order |
| C-033 | 2026-11-02 | One JSON format (PosJson) for database, API and sync: camelCase, enums as words, money as pence + currency | No translation layers; what is stored is what is sent |
| C-034 | 2026-11-02 | Hub stores menus and orders as full JSON state plus indexed columns (status, date, key, reference) | Simple, exact, and easy to sync; searchable where it matters |
| C-035 | 2026-11-02 | Hub uses one SQLite connection with writes one at a time, WAL and synchronous=FULL | A shop's load is small; strict ordering and power-cut safety matter more |
| C-036 | 2026-11-02 | Schema changes are numbered migrations applied at start-up in a transaction | Upgrades happen automatically and never half-apply |
| C-037 | 2026-11-02 | Tills send what was picked, never prices; the hub prices from its menu | A till cannot charge a wrong or tampered price |
| C-038 | 2026-11-02 | Refused changes are rolled back: nothing is saved and no order number is used | Database always matches the rules |
| C-039 | 2026-11-02 | Hub listens on localhost only until device registration and staff login exist | No unauthenticated access from the shop network |
| C-040 | 2026-11-02 | The menu change log text is kept after restarts, but undo only covers changes since the hub started | Full undo history across restarts is not needed for the MVP |
| C-041 | 2026-11-02 | Trading day starts at 05:00 | Confirmed by the owner |
| C-042 | 2026-11-02 | Every call identifies a registered device (id + secret key); changes also need a staff PIN login on that device; who acted comes from the login, never from the request | Orders and audit logs cannot be faked by a till |
| C-043 | 2026-11-02 | Fixed roles (Owner, Manager, Cashier, Waiter, Kitchen) with fixed permissions; managers manage only staff below manager; only the owner registers devices | Simple to explain; editable roles can come later |
| C-044 | 2026-11-02 | PINs (4–8 digits, unique, no 1111/1234) stored as a keyed hash with the key in a separate file; protection by lockout (5 wrong in 5 min per device) | Short PINs cannot be protected by hashing alone; lockout stops guessing |
| C-045 | 2026-11-02 | Device keys and session tokens are 256-bit random secrets; only their hashes are stored | A copied database does not give access |
| C-046 | 2026-11-02 | Manager approval by typing the manager's PIN on the same till (X-Manager-Pin), recorded as the approver | Matches how shops work; no need to switch user |
| C-047 | 2026-11-02 | First-time set-up only from the hub PC and only once; new devices join with a single-use 6-digit code valid 10 minutes | Nobody on the network can take over the hub |
| C-048 | 2026-11-02 | Staff logins last 12 hours; changing a PIN, removing a staff member or removing a device ends their logins at once | Lost or shared logins do not linger |
| C-049 | 2026-11-02 | Each hub has its own certificate authority (ECDSA P-256, 10 years, can only sign hub certificates); tills install it once | Browsers on tills trust the hub without warnings; no internet or paid certificates needed |
| C-050 | 2026-11-02 | Hub HTTPS certificate valid 397 days, renewed automatically 30 days before the end or when the PC's names/addresses change; checked hourly | Meets browser and Apple limits; survives address changes |
| C-051 | 2026-11-02 | Plain HTTP only on localhost (5080); the shop network gets HTTPS only (5443) | PINs and logins never cross the network unencrypted |
| C-052 | 2026-11-02 | Certificate files are password-protected with a password derived from hub.key | A copied certificate file alone is useless |
| C-053 | 2026-11-02 | Tills check the shop CA with a 16-character check code shown on the hub | Catches a wrong or swapped certificate during set-up |
| C-054 | 2026-11-02 | Hub PC gets a fixed address in the router | Tills always find the hub; the certificate still renews itself if the address changes |
| C-055 | 2026-11-03 | Live updates by SignalR over WebSockets only; events are numbered, stored in the same transaction as the change, and sent only after it is saved | Screens never see changes that were rolled back; nothing is lost if a send fails |
| C-056 | 2026-11-03 | Screens detect gaps by event number and fetch missed events (max 500 per call) | Simple, reliable catch-up after Wi-Fi drops or restarts |
| C-057 | 2026-11-03 | Live connection opened with a 60-second, device-bound ticket from a normal authenticated request | Browsers cannot send device keys on a WebSocket; keys never go in URLs |
| C-058 | 2026-11-03 | Kitchen screens receive only accepted orders; customer displays only menu changes | Online orders are checked before the kitchen starts; displays show nothing private |
| C-059 | 2026-11-03 | Payments and refunds are separate records, never changed; refunds are negative and point at the payment refunded | Full audit trail; totals are simple sums |
| C-060 | 2026-11-03 | Every payment belongs to the open shift of the till it was taken on; only tills take payments | Each drawer's cash can be reconciled |
| C-061 | 2026-11-03 | Complete only when fully paid; cancel/reject only after refunding anything paid | No order is left half-paid or with money unaccounted |
| C-062 | 2026-11-03 | Refunds use the original method, never exceed what is left of that payment, need a reason and a manager; tips are not refunded through this | Matches card-terminal practice; prevents refund fraud |
| C-063 | 2026-11-03 | Paid-in/paid-out need a manager and a reason; paid-out cannot overdraw the drawer | Cash control |
| C-064 | 2026-11-03 | Closing a shift is a blind count; expected cash shown after | Standard cash-control practice |
| C-065 | 2026-11-03 | Shift business date is the day it opens; its payments count on that day | Z report and drawer always agree |
| C-066 | 2026-11-03 | Z report needs all shifts closed; numbered and stored; repeat returns the same; no new shift after a day's Z | A Z is final, as tax records require |
| C-067 | 2026-11-03 | Refund VAT is taken off pro rata to the refunded order's VAT rates | Correct VAT when refunding mixed-rate orders without picking items |
| C-068 | 2026-11-03 | Payments carry a till-made id; a resent payment returns the one already taken | Safe retries after a dropped connection, no double charge |
| C-069 | 2026-11-03 | All printouts are laid out once (PrintDocument) and drawn as pictures at printer resolution (203 dpi; 576/384 dots) | One layout for PDF, Windows printers and thermal printers; Arabic prints anywhere |
| C-070 | 2026-11-03 | Arabic shaped with HarfBuzz; lines split into Arabic and non-Arabic runs and placed in reading order | Correct joined, right-to-left Arabic, also mixed with English |
| C-071 | 2026-11-03 | Bundled font DejaVu Sans (free licence, includes Arabic); font files are a setting | Same output on every PC; can switch to a Windows font later |
| C-072 | 2026-11-03 | While there are no printers: PDF files by default; "Microsoft Print to PDF" supported as a Windows printer, saving without a dialog | Owner can see every printout without hardware |
| C-073 | 2026-11-03 | Print jobs saved in the same transaction as the change; printed after saving, in order; retried, then failed with a live alert and a manual retry | Kitchen tickets never lost or printed for changes that did not happen; printer faults never stop sales |
| C-074 | 2026-11-03 | Receipt printed on completion; later prints marked COPY; void slip when a sent item is removed | Standard practice; the kitchen always knows to stop |
| C-075 | 2026-11-03 | English only by default on receipts, kitchen tickets and the print test; Arabic kept as a setting (`Language` = `both`/`ar`) | Owner needs English only for now; Arabic stays available for other shops |
| C-076 | 2026-11-03 | Backups are one zip of database snapshot + PIN key + certificates + SHA-256 list; checked before being named | A backup is only useful if it is complete and undamaged; the key and certificates are needed for PINs and tills to keep working |
| C-077 | 2026-11-03 | Database copied with SQLite's online backup while holding the hub's database lock | Consistent snapshot without stopping trading; never half a change |
| C-078 | 2026-11-03 | Backups hourly when something changed, at start-up if due, after each Z report and on demand; optional checked second copy (USB/other disk) | One hour of orders at most at risk; end of day always saved; protects against disk failure or theft when a copy folder is set |
| C-079 | 2026-11-03 | Keep 48 h of hourly backups, then one a day for 30 days, never fewer than 3; only files named like backups are deleted | Enough history to recover from a mistake noticed late, without filling the disk |
| C-080 | 2026-11-03 | Restore is a command run with the hub closed; replaced files moved aside, never deleted | A restore can itself be undone; nothing is lost by a wrong choice |
| C-081 | 2026-11-03 | Data-folder lock file (hub.lock): one hub per database; restore refused while it is held | Two programs writing one database, or a restore under a running hub, could damage the data |
| C-082 | 2026-11-03 | Copy of the database kept before any schema upgrade; databases from newer versions refused | A failed or unwanted upgrade can be rolled back; an older program never damages newer data |
| C-083 | 2026-11-03 | Windows launchers (.bat) kept in tools/windows | Owner runs the hub, tests and restores by double-click, no command line needed |
| C-084 | 2026-11-03 | Till screen built as plain HTML/CSS/JavaScript modules served by the hub, no framework or build step (replaces the React plan for the till) | No install on tills, works offline, nothing to download (npm is unreachable from the build environment); fewer moving parts for the owner to maintain |
| C-085 | 2026-11-03 | Small built-in client for SignalR's JSON protocol instead of the npm package | Same protocol, no download; reconnects with back-off and catches up by event number |
| C-086 | 2026-11-03 | An order being built lives on the till until Send or Pay; the hub prices it live with /orders/quote | No order numbers wasted on abandoned orders; totals always match the hub's rules |
| C-087 | 2026-11-03 | Receipt prints automatically when an order becomes fully paid; on completion only if it never printed | Takeaway customers pay first and want the receipt then; no unwanted copies |
| C-088 | 2026-11-03 | Order list and single order include paid and balance | The till can show what is still due without one call per order |
| C-089 | 2026-11-03 | Sample takeaway menu built into the hub, loadable only into an empty menu | Lets the owner try the till at once; can never overwrite a real menu |
| C-090 | 2026-11-03 | Device key and staff login kept in the till browser's local storage | A till is a dedicated device; logins survive a page reload; logout clears it |
| C-091 | 2026-11-03 | Strict Content-Security-Policy on the screen; all names inserted as text | Blocks injected scripts even if a menu name contained code |
| C-092 | 2026-11-03 | Typing on a pre-filled amount replaces it | Matches what staff expect from a selected field; fewer taps |
| C-093 | 2026-11-03 | Browser tests (Playwright + Node's test runner) run against a real hub, locally and in CI | The screen is only "done" when a real browser has used it end to end |
| C-094 | 2026-11-03 | (Resolved by C-097) Adding items to a saved order was not protected against a duplicate after a dropped connection | — |
| C-095 | 2026-11-03 | Edit menu mode uses the order screen's layout: same tabs and buttons on the right, edit panel in place of the order on the left; amber bar | Owner's request; staff already know where everything is; the colour makes the mode obvious |
| C-096 | 2026-11-03 | Edits carry the item as loaded; the hub refuses the save if it changed since (409 ChangedElsewhere) | Two managers on two tills never silently overwrite each other |
| C-097 | 2026-11-03 | Adding items to an order carries a till-made request id, recorded in the same transaction (table order_line_requests, schema 6); a resend returns the order unchanged | A dropped connection can never add the same items twice |
| C-098 | 2026-11-03 | Moving buttons uses Earlier / Later and Move-to-category buttons instead of drag and drop for now | Reliable on every touch screen; drag and drop can be added later on top of the same move route |
| C-099 | 2026-11-03 | Meal-deal choices are not edited on the till yet (name, price, VAT, sold out and hidden are) | Deal slots are complex; they come with the back office |
| C-100 | 2026-11-03 | Sold out can be switched by anyone taking orders from More → Sold out items | The counter must react straight away when something runs out |
| C-101 | 2026-11-04 | Version number shown on the login screen and in More, and in /api/health | Owner and support can see at once which version a till is talking to |
| C-102 | 2026-11-04 | Clear start-up message when the port or data folder is already used by another hub | An old hub left running made the new version look broken (owner report: "edit menu is not active") |
| C-103 | 2026-11-04 | More menu in three columns, Menu first | On 768-pixel-high till screens the old single list hid Edit menu below the fold |
| C-104 | 2026-11-04 | Postcode check and distance through postcodes.io (free ONS data, no account); house-by-house address lists left for a paid PAF service later | Owner's "avoid paid services where possible"; staff type the house number and street |
| C-105 | 2026-11-04 | Every postcode looked up is kept in the hub database forever (postcode_cache) | Postcodes do not move; repeat customers work without internet; fewer outside calls |
| C-106 | 2026-11-04 | Distance = straight line × 1.3 (setting) until a road-distance service (D-018: self-hosted OSRM) is added | Simple and close enough for charge bands; replaceable without changing the till |
| C-107 | 2026-11-04 | Customers remembered by phone number (digits only, +44 → 0); a blank field never wipes a saved one | Fast repeat orders; ready for caller ID |
| C-108 | 2026-11-04 | Customer details and address stored on the order itself (OrderContact), recorded in history when changed | Receipts, tickets and records stay right even if the customer's saved address changes later |
| C-109 | 2026-11-04 | Delivery orders need address, phone and distance; a changed delivery fee needs a manager (PIN) and a reason; the order and the customer save are rolled back together if not approved | Nothing half-saved; fee changes are accountable |
| C-110 | 2026-11-04 | Till blocks Send/Pay below the minimum order or beyond the maximum distance (with the reason); a manager-agreed fee lifts the block | Implements C-017 on the screen |
| C-111 | 2026-11-04 | Sending a delivery prints the receipt for the bag at once, with the customer block and AMOUNT DUE / PAID | The driver knows where to go and what to collect |
| C-112 | 2026-11-04 | "Phone" order mode renamed Collection; Delivery added | Clearer for staff |
| C-113 | 2026-11-04 | Newest dialog or working cover is always on top | A manager PIN asked while the till is working stays usable, and buttons cannot be pressed twice while working |
| C-114 | 2026-11-04 | House-by-house addresses from the owner's own Royal Mail PAF CSV files, loaded into the hub database (schema 8) | Owner's choice; works offline, no per-search cost; replaces the paid lookup idea for addresses (C-104) |
| C-115 | 2026-11-04 | PAF column order taken from the owner's B12 file (14 columns, no header); rows with a bad postcode or PO box only are skipped | Matches the files the owner has; nothing undeliverable in the list |
| C-116 | 2026-11-04 | Address lines follow Royal Mail's layout: number-like building names (25-27, 11a) go with the street; business, flat and building names and areas go on line 2; town in title case | Reads the way addresses are written and keeps business names for the driver |
| C-117 | 2026-11-04 | Pick list in street / house-number / flat order with natural number sorting; typing filters by the start of each word | 2 before 10, Flat 2 before Flat 10; "flat 10" finds it at once |
| C-118 | 2026-11-04 | Files load from the hub's addresses folder at start-up (only new or changed ones) or by upload from the till (owner); each load replaces that file in one transaction | Easy to update a district; a bad file never wipes good data |
| C-119 | 2026-11-04 | Postcode check still lists addresses when the distance service is unreachable, and opens "type the miles" itself | The counter keeps working without internet |
| C-120 | 2026-11-04 | Royal Mail files are never committed to the repository (licensed data); tests use made-up addresses in the same layout | Licence compliance |
| C-121 | 2026-11-04 | Three address services, chosen by the owner on the till, in this order: Google, Royal Mail online (AddressNow/Loqate), local Royal Mail file (default) | Owner's requirement |
| C-122 | 2026-11-04 | Online services are called by the hub, not the till; keys stored in the hub database and never returned (last 4 shown) | Keys are not exposed to browsers; one place to change them |
| C-123 | 2026-11-04 | Automatic fallback to the local file when the online service fails (on by default), with the reason shown | The counter keeps taking deliveries without internet or with an expired key |
| C-124 | 2026-11-04 | Distance from the online service's location when it gives one; otherwise the postcode check | Fewer outside calls; works for Google places without a postcode lookup |
| C-125 | 2026-11-04 | One "Find address" box for all services; Enter searches afresh and picks a single match; postcodes/streets from Royal Mail open into their addresses | Same habit for staff whatever the service |
| C-126 | 2026-11-04 | Database closes only after work in progress; the data-folder lock is released on stop even if shutdown fails elsewhere | Found by tests: a restart could be refused because the old hub kept the folder |
| C-127 | 2026-11-04 | Google and Royal Mail code tested against stand-ins in their documented formats; a live test with real keys is needed in the shop | No internet access to those services from the build environment |
| C-128 | 2026-11-04 | Shop details (name, address, postcode, phone, VAT number, receipt message) entered by the owner on the till and stored in the hub database; applied at once to receipts and delivery distances | Owner asked how to add the shop address; editing JSON by hand is error-prone |
| C-129 | 2026-11-04 | Shop details are checked and tidied: UK postcode, phone written "01134 960000", VAT number written "GB 123 4567 89" (9 or 12 digits) | Receipts look right and VAT numbers are valid in shape |
| C-130 | 2026-11-04 | Example address removed from the default settings | It would have printed on real receipts |
| C-131 | 2026-11-04 | Shop details has the same Find address box as the delivery panel (one shared component), and Check postcode confirms the shop's postcode is a real location | Owner report: "postcode lookup is not working in shop details" (there was none) |
| C-132 | 2026-11-04 | Kitchen screen is part of the same web app: chosen by device type (Kitchen screen) or by a kitchen-only role | One thing to install and update; any browser device works |
| C-133 | 2026-11-04 | Kitchen screens need a staff PIN login (Kitchen role) | Every Start/Ready/Recall is recorded against a person |
| C-134 | 2026-11-04 | Ready → Preparing allowed ("Recall"), recorded in the order history | Mistaken taps happen in a busy kitchen; the record shows who and when |
| C-135 | 2026-11-04 | Timers amber at 10 minutes, red at 20; NEW marks items added to an order already on the board, counted from when they are sent | Common kitchen practice; new orders are not all "NEW" |
| C-136 | 2026-11-04 | Ticking items off is per screen only (not saved) | A cook's working aid, not part of the order record |
| C-137 | 2026-11-04 | Tills show "Order X is ready" when the kitchen marks it | The counter knows when to call the customer |
| C-138 | 2026-11-04 | Other PCs connect to the hub's network address (https://<address>:5443), never localhost; the till shows the address under More → Add a till or screen | "localhost refused to connect" on a second PC: localhost means that PC itself |
| C-139 | 2026-11-04 | Double-click files for the firewall rule (hub PC, local subnet only, port 5443) and for connecting another PC (certificate check code compared by the person before installing) | Shop staff can set up a till without typing commands; the check code stops a wrong certificate being trusted |
| C-140 | 2026-11-04 | The Windows package is self-contained (no .NET install needed), without debug files | A PC without .NET could not start the hub |
| C-141 | 2026-11-05 | Nobody can change their own role or remove themselves | No self-promotion; the shop can never lose its last owner |
| C-142 | 2026-11-05 | Managers manage only cashiers, waiters and kitchen staff; only the owner manages managers and owners | Same rule as adding staff; a manager cannot raise their own team's power |
| C-143 | 2026-11-05 | Changing your own PIN needs your current PIN; wrong ones count towards the device lockout | Someone using a till left logged in cannot take over a PIN |
| C-144 | 2026-11-05 | A manager can set a new PIN for a person who forgot theirs, without knowing the old one | Forgotten PINs are common; the change is recorded as "PIN reset" |
| C-145 | 2026-11-05 | A role change logs the person out everywhere; a name change does not | New permissions apply at once; renaming should not interrupt a shift |
| C-146 | 2026-11-05 | Removed staff are kept (records stay linked) and can be brought back only with a new PIN | Their old PIN was wiped on removal, so it can never work again |
| C-147 | 2026-11-05 | Two active staff cannot have the same name (ignoring case and extra spaces); names up to 40 characters | Reports, approvals and history must say clearly who did what |
| C-148 | 2026-11-05 | Every staff change is recorded (when, who, whom, what) in staff_history, never the PIN; staff already on the list get a starting entry | An audit trail for disputes and for licensing to other shops |
| C-149 | 2026-11-05 | The till in use cannot remove itself | Stops the owner locking themselves out of the PC they are on |
| C-150 | 2026-11-05 | The staff discount limit is checked once, when the discount is given; re-pricing later never refuses it | Found by a test: a 10% discount within the limit made the order refuse new items once 10% passed £5, and a stricter limit could have stopped saved orders loading |
| C-151 | 2026-11-05 | Discount limits are an owner setting on the till (whole %, £), saved in the hub; the shipped default is 0 = always a manager | Each shop decides; safe until decided |
| C-152 | 2026-11-05 | A discount that would take the total below what is already paid is refused | Money back is a refund, with its own approval and record |
| C-153 | 2026-11-05 | Giving a discount saves a new order without sending it to the kitchen; Send or Pay sends it | Discounts are usually agreed before paying; the kitchen should not get a ticket for an order still being settled |
| C-154 | 2026-11-05 | Each refund records how much of it lowers the sale (money back for goods) and how much returns an overpayment | Found while testing: a complaint refund made the order owe the money again, and refunds after a removed item or before a cancel would have taken VAT off twice |
| C-155 | 2026-11-05 | Refunds of orders that end cancelled or rejected take nothing off the Z report's sales or VAT | Those orders were never counted as sales |
| C-156 | 2026-11-05 | Card refunds are recorded on the till and done by hand on the card machine (until Worldpay is linked); app and online payments are refunded in the app | The till cannot move card or app money itself yet |
| C-157 | 2026-11-05 | Money in staff messages shows as £12.50 (was "GBP 12.50") | Plain English for staff |
| C-158 | 2026-11-05 | New order status Out with driver (OutForDelivery), set only by choosing a driver; coming back only through Back to shop (reason) | Every delivery out has a named driver, and the record shows who and why it came back |
| C-159 | 2026-11-05 | Deliveries can go out straight from Accepted or Preparing, not only Ready | Shops without a kitchen screen never mark orders Ready |
| C-160 | 2026-11-05 | An order goes out only when everything on it has gone to the kitchen | No driver leaves with food nobody was asked to make |
| C-161 | 2026-11-05 | Any active staff member can take a delivery; drivers are listed first | Owners and managers deliver too in small shops |
| C-162 | 2026-11-05 | Money from a driver is taken on the till they hand it to (cash into that drawer, card on their machine), then the order is delivered | Cash counts and the Z report stay right without a separate driver float |
| C-163 | 2026-11-05 | A driver's day counts only payments taken after the order went out | A phone payment taken in the shop before is not the driver's money |
| C-164 | 2026-11-05 | Drivers log in to a read-only My deliveries screen | Addresses and amounts at hand; changes stay with the shop staff |
| C-165 | 2026-11-05 | Order statuses shown in plain words (Out with driver) | Staff-facing text |
| C-166 | 2026-11-05 | Split bill lives on the payment screen: Whole bill, Equal shares, By items | One place to take money; any mix of cash and card |
| C-167 | 2026-11-05 | Each item can be paid for once; a payment for items must be exactly their share (worked out by the hub) | Found while building: nothing stopped two guests paying for the same item, or an item payment at the wrong amount |
| C-168 | 2026-11-05 | Equal shares are worked out again from what is left after each person pays | Someone paying a different amount does not break the rest |
| C-169 | 2026-11-05 | A line with a quantity is paid as one line when splitting by items | Kept simple for now; equal shares cover sharing a line |
| C-170 | 2026-11-05 | Sales reports count completed orders by trading day; refunds and payments by the day they were made; the Z report stays each day's record | Same rules as the Z report, so figures agree |
| C-171 | 2026-11-05 | Item and category sales are after each item's share of order discounts and before delivery charges | What each item really earned |
| C-172 | 2026-11-05 | Staff reports credit an order to the person who created it; discounts to who gave them; refunds to who made them | Clear responsibility |
| C-173 | 2026-11-05 | Reports are limited to 366 days per request; managers and owners only | Keeps the hub fast during trading; sales figures are sensitive |
| C-174 | 2026-11-05 | CSV cells starting with = + - @ (unless a plain number) are saved as text; files carry a UTF-8 mark | Stops formula injection through item or staff names; £ shows correctly in Excel |
| C-175 | 2026-11-05 | Category names in reports come from the current menu; a deleted category shows as Other | Simple; categories are rarely deleted |
| C-176 | 2026-11-05 | Thermal printers get the printout as an ESC/POS picture (GS v 0), drawn by the same renderer as the PDF | The £ sign, bold and layout print the same on every printer, with no code-page problems; one layout to test |
| C-177 | 2026-11-05 | Picture sent in bands of at most 255 rows; feed then partial cut (GS V 66) | Small printer buffers; a partial cut keeps the receipt from falling |
| C-178 | 2026-11-05 | Network printers on raw port 9100 (4-second timeout, clear message); USB printers through the Windows driver in RAW mode | Covers the two ways shops connect thermal printers without extra software |
| C-179 | 2026-11-05 | Printers are chosen on the till (More → Printers) and saved in the hub; file folders cannot be changed from the till | No settings files to edit; a till cannot make the hub write files elsewhere |
| C-180 | 2026-11-05 | The cash drawer opens through the print queue for cash in/out, tried once only, and only on a thermal receipt printer | Never interrupts a printout and never springs open late |
| C-181 | 2026-11-05 | No sale needs a reason and a manager (cash management), and every opening is recorded and shown on the X report | Drawer openings without a sale are a common way cash goes missing |
| C-182 | 2026-11-05 | One receipt printer and drawer for the shop for now | Matches a one-counter takeaway; per-till printers later |
| C-183 | 2026-11-05 | Printer type per thermal printer: ESC/POS (Epson TM-m30, Xprinter XP-Q801, most) or STAR Graphic Mode (Star TSP100/TSP143) | The owner's Star TSP100 has no fonts and ignores ESC/POS (Star's own statement and Graphic Mode specification); Epson lists GS v 0, GS V and ESC p for the TM-m30 |
| C-184 | 2026-11-05 | Star printouts: ESC * r R, A, P "0", E "13", "b n1 n2" per dot row, ESC FF EOT, ESC * r B; numbers as text digits; drawer by BEL | From the STAR Graphic Mode Command Specifications Rev. 2.32 ("decimal notation using ASCII characters"), matching working open-source drivers; to be confirmed on the shop's printer |
| C-185 | 2026-11-05 | Star printers: drawer socket 1 only (pin 5 refused) | BEL drives the first drawer; a second drawer on a Star is not needed |
| C-186 | 2026-11-06 | Test fix: the damaged-backup test flips a byte instead of writing a fixed value | Found while preparing the 0.15.1 package: the old test failed about 1 run in 256 (the random key sometimes already held that value). The backup check itself was right; the program is unchanged |
| C-187 | 2026-11-06 | Up to 10 kitchen printers, each printing Everything or only chosen categories and single items | The owner's request: pizza, kebab, drinks and dessert printers |
| C-188 | 2026-11-06 | A chosen item overrides its category; "Everything" printers always print; an item nobody chose goes to the first kitchen printer | Each item is made where it should be, and a ticket is never lost through a gap in the set-up |
| C-189 | 2026-11-06 | Meal deals are split by item between printers (marked "(part)"), unless a printer chose the deal or its category | A pizza-and-drink deal needs the pizza at the oven and the drink at the drinks station |
| C-190 | 2026-11-06 | Routing is set on the Printers screen (by category and item), not in the menu editor | One place to set up printers; the menu stays as it is |
| C-191 | 2026-11-06 | Each ticket remembers its printer; a ticket queued before the printers were changed prints all its items if its printer would now get none | Nothing is lost when the set-up changes during service |
| C-192 | 2026-11-06 | Printer set-ups saved by 0.15 load as one "Kitchen" printer that prints everything | Updating never changes what an existing shop prints |
| C-193 | 2026-11-06 | One item can print on several kitchen printers: single items are "＋ also here" or "✕ not here" per printer (replaces C-188's item override) | The owner needs some items on more than one kitchen printer; with the override, choosing an item on a second printer took it off the first |
| C-194 | 2026-11-06 | An item no printer prints (e.g. "not here" everywhere) still prints on the first kitchen printer | A ticket is never lost through the set-up |
| C-195 | 2026-11-06 | 0.16.0 set-ups are converted on loading: each chosen item gets "not here" on the other printers | They print exactly as before |
| C-196 | 2026-11-06 | The menu is entered in bulk with a spreadsheet (Excel .xlsx or CSV) imported on the till | The owner asked for an easy way to add the menu; every shop has Excel; no paid service. Copying from Just Eat was blocked by its robot check, which we do not get around |
| C-197 | 2026-11-06 | One row per size of each item; rows with the same category and item are one button; empty Category or Item carries on from the row above | The simplest layout to type and to check; matches how menus are printed |
| C-198 | 2026-11-06 | Preview first: nothing changes until the sheet has no problems and Import is pressed; every problem names its sheet and row | Mistakes are found before they reach the till |
| C-199 | 2026-11-06 | Existing categories, items, sizes and extras are found by name and keep their ids; importing the same file twice changes nothing | Kitchen printer choices, reports and open orders stay linked; a sheet can be edited and imported again safely |
| C-200 | 2026-11-06 | A column left out keeps what the till has; items not in the sheet are kept, or hidden if chosen, never deleted; meal deals are never changed by a sheet | A partial sheet cannot wipe out set-up done in Edit menu; deleting is never needed and loses history |
| C-201 | 2026-11-06 | VAT type is required for new items (Hot food, Cold food or Standard); no guessing | VAT mistakes are tax mistakes |
| C-202 | 2026-11-06 | A backup is made before every import and before undo; the import is refused if the backup fails; the last import can be undone while the menu is unchanged since | Data integrity first; undo cannot overwrite later edits |
| C-203 | 2026-11-06 | The spreadsheet is read with our own small reader: .xlsx and .csv only, 2 MB file, 30 MB unzipped part, 10,000 rows, no DTDs, values only (no formulas or macros run); written with text cells and prices as numbers | No paid library or extra software; a spreadsheet file cannot harm the hub |
| C-204 | 2026-11-06 | The Foleshill Road Dixy Chicken menu (from Foodhub) is used only as a test menu, kept outside the code repository | Testing with a real takeaway menu; it is another branch's menu and prices, not the owner's B12 shop |
| C-205 | 2026-11-07 | Collection and delivery (phone) orders must have the customer's name and a valid UK phone number; checked on the till and on the hub, also when the details are corrected later; counter takeaway and eat-in need none | The owner's rule: the shop must be able to call the customer and call out the name at collection. Replaces the earlier "name or phone (optional)" for collection and "phone only" for delivery |
| C-206 | 2026-11-07 | Every new order must have its type chosen (Takeaway, Eat in, Collection or Delivery) before items are added; no default; the hub refuses an order without source and type | The owner's rule. A default (Takeaway) let eat-in orders be charged takeaway VAT by mistake, and phone orders be taken without the customer's details |
