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

**Still local only:** the hub listens on this PC (localhost) until encrypted connections (HTTPS) for the shop network
are in place; then tills on the network can connect.

### Roles

| Role | Can do |
| --- | --- |
| Kitchen | Kitchen screen: mark orders preparing and ready; mark items sold out |
| Waiter | Take orders, remove items not yet in the kitchen |
| Cashier | Waiter + accept/reject online orders |
| Manager | Everything in the shop: approvals, cancels, menu, staff below manager, reports |
| Owner | Everything, including devices and managers |

### API (version 1)

| Method | Route | Who | What it does |
| --- | --- | --- | --- |
| GET | /api/health | anyone | Is the hub running |
| GET/POST | /api/setup | hub PC, once | First-time set-up |
| POST | /api/devices/pairing-code | owner | Code to register a new till or screen |
| POST | /api/devices/pair | new device | Register with a pairing code |
| GET / DELETE | /api/devices[/{id}] | owner | List or remove devices |
| POST | /api/auth/login, /api/auth/logout | device / staff | PIN login, logout |
| GET | /api/auth/me | staff | Who is logged in and what they may do |
| GET / POST / DELETE | /api/staff[/{id}] | manager, owner | List, add, remove staff |
| POST | /api/staff/{id}/pin | self, manager | Change a PIN |
| GET | /api/menu, /api/menu/changes?since=N | any device | Menu and change log |
| PUT | /api/menu | manager | Import a whole menu (checked) |
| POST | /api/menu/products/{id}/price, /rename | manager | Change price or name |
| POST | /api/menu/products/{id}/sold-out | cashier, kitchen, manager | Sold out / back in stock |
| POST | /api/menu/undo | manager | Undo the last menu change |
| POST | /api/orders | cashier, waiter | New order from what was picked; safe to resend |
| GET | /api/orders?date=&status=, /api/orders/{id} | any device | Orders for a trading day, one order |
| GET | /api/orders/{id}/history | manager | Everything that happened to it |
| POST | /api/orders/{id}/lines | cashier, waiter | Add items to an open order |
| POST | /api/orders/{id}/status | depends on status | Accept/reject (cashier), preparing/ready (kitchen), complete, cancel (manager) |
| POST | /api/orders/{id}/send-to-kitchen | cashier, waiter | Send new items to the kitchen |
| POST | /api/orders/{id}/lines/{lineId}/void | cashier; manager if in the kitchen | Remove an item |

Refusals come back as `{ "code": "...", "message": "..." }` with 400 (invalid), 401 (not registered or not logged in),
403 (role not allowed, or needs approval), 429 (locked out),
404 (not found), 409 (not allowed now / duplicate key) or 422 (choices not valid, with a list of problems).
