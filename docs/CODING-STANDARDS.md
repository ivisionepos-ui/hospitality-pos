# Coding standards

## Money and numbers
- Money is always `Money` (whole pence, `long`). Never `float`, `double` or `decimal` for amounts.
- Percentages and VAT rates are basis points (`int`): 2000 = 20%, 1500 = 15%.
- Rounding is always `Rounding.DivideRounded` (half away from zero). No other rounding.
- Arithmetic on money uses `checked` so overflow throws instead of giving a wrong total.

## Prices and VAT
- Menu prices are VAT-inclusive (UK practice).
- VAT is summarised per rate and rounded once per rate, so the summary adds up to the total.
- Orders store a copy of names and prices at the moment of sale.

## Time
- Store moments as UTC (`DateTimeOffset`). Convert to the branch time zone only to apply rules (offers, menus, business day).
- Offer rules use the time the order is placed, not the collection time.

## Naming
- C#: PascalCase for types, methods and properties; `_camelCase` for private fields; file-scoped namespaces.
- One public concept per file where practical; folder = namespace.
- Database: `snake_case`, plural table names, English only.
- Test names describe behaviour in plain English: `Offer_does_not_apply_on_thursday`.

## Quality gates
- Warnings are errors. Nullable reference types are on.
- Every module has automated tests written with the code; nothing is "done" until its tests pass.
- Business rules live in `Pos.Domain` and never depend on databases, UI or hardware.
