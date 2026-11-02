# Hospitality POS Platform

Point of sale, kitchen display and online ordering for restaurants, coffee shops and takeaways.
UK first (GBP, UK VAT), Arabic and English.

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
    Data/                Database (schema upgrades, WAL), order and menu storage
    Api/                 Web API routes and request/response formats
tests/
  Pos.Domain.Tests/      Automated tests for Pos.Domain
  Pos.Hub.Tests/         End-to-end tests: real HTTP calls to a real hub with a real database
docs/
  DECISIONS.md           Decision log
  MODULES.md             Completed modules and their test status
  CODING-STANDARDS.md    Naming and coding rules
```

Projects still to come: `Pos.Cloud.Api`, `Pos.Sync`, and the React apps (POS, kitchen display, customer display, back office, ordering website).

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

It listens on http://localhost:5080 and keeps its database in `data/hub.db`. Settings are in
`src/Pos.Hub/appsettings.json` under `Hub` (branch code, time zone, trading-day start, staff discount limits).

**Not for use in a shop yet:** there is no login for tills or staff, so the hub only listens on this PC (localhost).
Device registration, staff PINs and licence activation come before it is opened to the shop network.

### API (version 1)

| Method | Route | What it does |
| --- | --- | --- |
| GET | /api/health | Is the hub running |
| GET | /api/menu | The whole menu |
| PUT | /api/menu | Import a whole menu (checked) |
| GET | /api/menu/changes?since=N | Menu change log after version N |
| POST | /api/menu/products/{id}/price | Change a size's price |
| POST | /api/menu/products/{id}/sold-out | Sold out / back in stock |
| POST | /api/menu/products/{id}/rename | Rename (English/Arabic, button name) |
| POST | /api/menu/undo | Undo the last menu change |
| POST | /api/orders | New order from what was picked; safe to resend (same key = same order) |
| GET | /api/orders?date=&status= | Orders for a trading day (default today) |
| GET | /api/orders/{id} | One order |
| GET | /api/orders/{id}/history | Everything that happened to it |
| POST | /api/orders/{id}/lines | Add items to an open order |
| POST | /api/orders/{id}/status | Move status (reason for cancel/reject) |
| POST | /api/orders/{id}/send-to-kitchen | Send new items to the kitchen |
| POST | /api/orders/{id}/lines/{lineId}/void | Remove an item (manager if already in the kitchen) |

Refusals come back as `{ "code": "...", "message": "..." }` with 400 (invalid), 403 (needs approval),
404 (not found), 409 (not allowed now / duplicate key) or 422 (choices not valid, with a list of problems).
