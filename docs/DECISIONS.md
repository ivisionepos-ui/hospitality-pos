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
