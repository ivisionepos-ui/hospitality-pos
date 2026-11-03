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
| Printing: receipts (UK VAT details, food-group order, English by default, Arabic optional), kitchen tickets (new/additional/void), Z slip; PDF, PNG and Windows-printer outputs; print queue with retry and failure alerts; receipt preview | 3 | Done 2026-11-03 | 11 layout/drawing tests + 7 hub tests | Windows printer output (Microsoft Print to PDF) written but **not yet tested on Windows** |

Total: 321 automated tests passing (230 business-rule tests + 91 hub tests), including 1,000 random orders checking that totals and VAT always reconcile.

## Not yet covered (next modules)
- Test the Windows printer output (Microsoft Print to PDF) on the owner's Windows PC
- ESC/POS thermal printers over USB and network, and the cash drawer kick (when printers are available)
- Kitchen stations: routing tickets to different kitchen printers
- Group headings on receipts ("STARTERS", "DRINKS")
- Windows Device Agent installer that installs the shop CA automatically
- Scheduled offers, delivery charge table and discount limits stored on the hub (offers currently not loaded)
- Remaining Edit-menu operations over the API (add, move, edit panel save, extras, categories)
- Automatic hourly local backups of the hub database
- Syncing menu and order changes between tills, hub and cloud
- Tip allocation between staff (tronc) and the written tipping policy record (tips by staff are already in the Z report)
- Worldpay terminal integration (amount sent from the till)
