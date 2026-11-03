# Module log

| Module | Phase | Status | Tests | Notes |
| --- | --- | --- | --- | --- |
| Project setup: solution, build rules, CI, docs | 0 | Done 2026-11-02 | Build with warnings as errors | CI runs on GitHub on every push |
| Money, rounding, proportional sharing | 2 | Done 2026-11-02 | 25 passing | |
| UK VAT rules and VAT summary | 2 | Done 2026-11-02 | 17 passing | Accountant to confirm VAT categories for the menu |
| Weekly time windows | 2 | Done 2026-11-02 | 5 passing | Reused later for time-based menus |
| Scheduled offers | 2 | Done 2026-11-02 | 17 passing | Uses branch local time incl. British Summer Time |
| Meal deal price sharing | 2 | Done 2026-11-02 | 5 passing (incl. 2,000 random deals) | |
| Order pricing (items, offers, deals, VAT) | 2 | Done 2026-11-02 | 8 passing | |
| Order discounts with staff limits and manager approval | 2 | Done 2026-11-02 | in 17 order-charge tests | Reason always required |
| Delivery charges by distance (bands or per mile) | 2 | Done 2026-11-02 | 15 passing + order-charge tests | Override needs a reason; screen must block "too far" / "below minimum" |
| Service charge (optional or compulsory) | 2 | Done 2026-11-02 | in 17 order-charge tests | Recorded for the Tipping Act |
| Menu model: categories, items, sizes, extras, meal-deal choices, English/Arabic names | 2 | Done 2026-11-02 | in 20 menu-editing tests | |
| Menu editing (Edit menu mode rules): rename, prices, sold out, add, drag-and-drop order, change log, undo | 2 | Done 2026-11-02 | 20 passing | Screens come with the POS front end |
| Order choices checked against the menu and turned into priced lines | 2 | Done 2026-11-02 | 10 passing | Sizes, extras limits, sold out, meal-deal rules |
| Order statuses, daily order numbers, business day, device-made ids | 2 | Done 2026-11-02 | 27 passing | Business day start time is a branch setting (e.g. 05:00) |
| Orders: lines with name/price snapshots, notes, send to kitchen, voids, cancel/reject, discounts and charges, full history | 2 | Done 2026-11-02 | in 18 order tests | Manager approval for kitchen voids and cancels |
| Duplicate-proof order intake (same order sent twice is stored once) | 2 | Done 2026-11-02 | in 18 order tests | Reused key with different content is refused |
| Saving menus and orders as JSON and loading them back exactly | 2 | Done 2026-11-02 | 6 passing | One JSON format for database, API and sync |
| Store Hub: SQLite database (WAL, full sync, schema upgrades), order and menu storage, order-number counter | 2 | Done 2026-11-02 | in 16 hub tests | Survives restarts; numbering carries on |
| Store Hub web API: orders (create, list, status, add items, send to kitchen, void) and menu (import, price, sold out, rename, undo, change log) | 2 | Done 2026-11-02 | 18 end-to-end tests | |
| Roles and permissions (Owner, Manager, Cashier, Waiter, Kitchen) | 4 | Done 2026-11-02 | 18 passing | |
| Hub access control: first-time set-up, till pairing codes, device keys, staff PIN login with lockout, 12-hour sessions, manager-PIN approvals, staff and device management | 4 | Done 2026-11-02 | 22 end-to-end tests | |
| HTTPS on the shop network: shop CA, hub certificate with automatic renewal, CA download, HTTP for this PC only | 4 | Done 2026-11-02 | 11 passing, incl. real encrypted connections | Each till installs the shop CA once |
| Live updates: numbered events saved with each change, sent after saving (SignalR/WebSockets), per-screen groups, catch-up, ticket access, disconnect on device removal | 4 | Done 2026-11-03 | 11 passing, real live connections | |
| Payments: cash with change, card with tip and reference, part payments, split equally / by items, refunds with manager | 3 | Done 2026-11-03 | 14 rule tests + hub tests | Card amount keyed into the Worldpay terminal for now |
| Shifts: float, paid in/out, blind close, expected vs counted cash, X report | 3 | Done 2026-11-03 | 5 rule tests + hub tests | |
| Z report: sales, sources, payments by method, VAT after refunds, cancels, voids, refunds, open orders, cash over/short, tips by staff | 3 | Done 2026-11-03 | 3 rule tests + full-day hub test | Numbered and stored; accountant to review layout |
| Printing: receipts (UK VAT details, food-group order, English by default, Arabic optional), kitchen tickets (new/additional/void), Z slip; PDF, PNG and Windows-printer outputs; print queue with retry and failure alerts; receipt preview | 3 | Done 2026-11-03 | 11 layout/drawing tests + 7 hub tests | Windows printer output (Microsoft Print to PDF) tested by the owner on Windows 2026-11-03 |
| Till screen (POS order screen): set-up, register a till, PIN login, categories and item buttons, sizes/extras with required choices, meal deals, live totals, send to kitchen, cash/card/part payments with change, open orders, add items, remove with manager PIN, cancel, reprint, collected, open/close till with blind count, paid in/out, X/Z reports, failed prints, backup now, add a till, live updates between tills | 3 | Done 2026-11-03 | 6 browser tests (real Chromium, real hub) + 4 hub tests | Edit menu mode is next |
| Hub additions for the till: order quote, sample menu, paid/balance on orders, receipt printed when fully paid | 3 | Done 2026-11-03 | 4 hub tests | |
| Backups: hourly / start-up / after Z report / on demand; checked zip of database snapshot, PIN key and certificates; second copy folder; keep 48 h then daily for 30 days; restore command; live warning on failure; one hub per data folder; copy before schema upgrades | 2 | Done 2026-11-03 | 13 passing, plus restore run on the real program | Owner to set a USB/second-disk copy folder |

Total: 343 automated tests passing (230 business-rule tests + 107 hub tests + 6 till-screen browser tests), including 1,000 random orders checking that totals and VAT always reconcile.

## Not yet covered (next modules)
- ESC/POS thermal printers over USB and network, and the cash drawer kick (when printers are available)
- Kitchen stations: routing tickets to different kitchen printers
- Group headings on receipts ("STARTERS", "DRINKS")
- Windows Device Agent installer that installs the shop CA automatically
- Scheduled offers, delivery charge table and discount limits stored on the hub (offers currently not loaded)
- Edit menu mode on the till (same layout as ordering): rename, prices, sold out, add, move; plus the remaining menu API operations
- Delivery orders on the till (address, postcode search, delivery charge), discounts and refunds on screen, split bill screen
- Till-made line ids so adding items to a saved order is safe to resend
- Encrypted off-site (cloud) backup copy
- Syncing menu and order changes between tills, hub and cloud
- Tip allocation between staff (tronc) and the written tipping policy record (tips by staff are already in the Z report)
- Worldpay terminal integration (amount sent from the till)
