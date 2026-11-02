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
| Store Hub web API: orders (create, list, status, add items, send to kitchen, void) and menu (import, price, sold out, rename, undo, change log) | 2 | Done 2026-11-02 | 16 end-to-end tests | Localhost only until device/staff login exists |

Total: 206 automated tests passing (190 business-rule tests + 16 end-to-end hub tests), including 1,000 random orders checking that totals and VAT always reconcile.

## Not yet covered (next modules)
- Device registration, staff PIN login and permissions on the hub (before it is opened to the shop network)
- Live updates to tills and kitchen screens (SignalR)
- Scheduled offers, delivery charge table and discount limits stored on the hub (offers currently not loaded)
- Remaining Edit-menu operations over the API (add, move, edit panel save, extras, categories)
- Automatic hourly local backups of the hub database
- Syncing menu and order changes between tills, hub and cloud
- Card tips taken at payment, and tip allocation records (Tipping Act) — with the payments module
