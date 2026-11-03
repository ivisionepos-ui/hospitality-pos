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
