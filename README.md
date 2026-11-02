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
tests/
  Pos.Domain.Tests/      Automated tests for Pos.Domain
docs/
  DECISIONS.md           Decision log
  MODULES.md             Completed modules and their test status
  CODING-STANDARDS.md    Naming and coding rules
```

Projects still to come: `Pos.Hub` (Store Hub service), `Pos.Cloud.Api`, `Pos.Sync`, and the React apps.

## Requirements

- .NET SDK 10.0 (https://dotnet.microsoft.com/download)
- Git

## Build and test

```
dotnet build
dotnet test
```

Every change must build with zero warnings and pass all tests before it is merged.
