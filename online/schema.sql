-- Online ordering database (Cloudflare D1, which is SQLite). Run once: wrangler d1 execute pos-online --file=schema.sql
-- Only what the website needs: the menu and shop details the hub publishes, website orders until the hub has them
-- (kept 30 days for the customer's status page, then deleted), and a short record of order attempts against abuse.

CREATE TABLE IF NOT EXISTS shop (
    id            INTEGER PRIMARY KEY CHECK (id = 1),
    info_json     TEXT NOT NULL,          -- name, address, phone, hours, delivery rules, lead times (published by the hub)
    menu_json     TEXT NOT NULL,          -- categories, items, sizes, extras, allergens (published by the hub)
    published_at  TEXT NOT NULL,
    hub_seen_at   TEXT,                   -- last time the hub checked in (orders are only taken while it does)
    accepting     INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS orders (
    id              TEXT PRIMARY KEY,     -- random id, also the hub's duplicate-proof key for this order
    token           TEXT NOT NULL,        -- secret in the customer's status link
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL,
    status          TEXT NOT NULL,        -- Placed, Received, Accepted, Ready, OutForDelivery, Completed, Rejected, Cancelled
    type            TEXT NOT NULL,        -- Collection or Delivery
    customer_json   TEXT NOT NULL,        -- name, phone, address, postcode, notes
    lines_json      TEXT NOT NULL,        -- the customer's choices (ids), checked against the menu
    goods_pence     INTEGER NOT NULL,
    delivery_pence  INTEGER NOT NULL DEFAULT 0,
    delivery_miles  REAL,
    total_pence     INTEGER NOT NULL,     -- the website's total; the shop's total replaces it when accepted
    hub_number      TEXT,                 -- the order number on the till
    ready_at        TEXT,                 -- when the shop expects it ready (or delivered)
    reason          TEXT,                 -- why the shop could not take it
    hub_received    INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_orders_waiting ON orders (hub_received, created_at);
CREATE INDEX IF NOT EXISTS ix_orders_created ON orders (created_at);

CREATE TABLE IF NOT EXISTS attempts (
    who  TEXT NOT NULL,                   -- a hash of the visitor's address, never the address itself
    at   TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_attempts ON attempts (who, at);
