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
