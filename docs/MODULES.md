# Module log

| Module | Phase | Status | Tests | Notes |
| --- | --- | --- | --- | --- |
| Project setup: solution, build rules, CI, docs | 0 | Done 2026-11-02 | Build with warnings as errors | CI runs on GitHub on every push |
| Money and rounding | 2 | Done 2026-11-02 | 22 passing | |
| UK VAT rules and VAT summary | 2 | Done 2026-11-02 | 17 passing | Accountant to confirm VAT categories for the menu |
| Weekly time windows | 2 | Done 2026-11-02 | 5 passing | Reused later for time-based menus |
| Scheduled offers | 2 | Done 2026-11-02 | 17 passing | Uses branch local time incl. British Summer Time |
| Meal deal price sharing | 2 | Done 2026-11-02 | 5 passing (incl. 2,000 random deals) | |
| Order pricing | 2 | Done 2026-11-02 | 8 passing | Order-level discounts, service charge and delivery fee come next |

Total: 74 automated tests passing.

## Not yet covered (next modules)
- Order-level discounts (amount or %), with manager approval, and how they spread across VAT rates
- Delivery charge by distance bands (section 7.4)
- Discretionary service charge and tips (outside the scope of VAT; recorded for the Tipping Act)
- Menu data model (products, sizes, modifier groups, meal deal slots) and validation of choices
