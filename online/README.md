# Online ordering website

The shop's own ordering website: customers order for **collection or delivery** and **pay when they collect or when it
arrives** (no card fees). It runs on Cloudflare's free plan (100,000 requests a day, free database, free HTTPS on your
own domain) and the shop's hub connects out to it every 5 seconds, so nothing on the shop's router is opened.

```
Customer's phone ──▶ order.yourshop.co.uk (Cloudflare Worker + D1 database)
                                ▲ every 5 s: menu out, orders in, progress out
                      Shop hub (till PC) ──▶ tills (Website order 015: accept / turn down) ──▶ kitchen
```

* **Menu**: sent by the hub by itself whenever it changes (items, sizes, extras, sold out, allergens). Meal deals are
  not on the website yet.
* **Orders are taken only** while the hub is checking in (shop PC on, internet up), "Take website orders now" is ticked,
  and the shop is open (opening times in More → Website). Otherwise the website says why and to phone.
* **On the till** a flashing *Website order 015* button and a beep every 20 seconds until it is accepted (with a ready
  time the customer sees) or turned down (with a reason the customer sees). Accepting sends it to the kitchen.
* **The customer's page** follows the order: sent → accepted (ready about 19:45) → ready → collected / on its way.
* **Safety**: the hub uses a secret shop key; customers can see only their own order (secret link); at most 5 orders
  per visitor per 30 minutes; prices and items are checked against the menu online and again on the till; orders and
  customers' details are deleted from the website after 30 days; no tracking or advertising cookies.

## Put it live (once, about 30 minutes)

You need: the domain name, a free Cloudflare account, and this GitHub repository.

1. **Cloudflare account and domain**: sign up at cloudflare.com (free). *Add a domain* → your domain → Free plan.
   Cloudflare shows two *nameservers*: put them in place of the current ones where you bought the domain. (If the domain
   already has a website or email, Cloudflare copies those settings over; check they are listed before changing.)
2. **Database**: in Cloudflare, *Storage & databases → D1 → Create database*, name it `pos-online`. Copy its **database
   ID**.
3. **API token**: *My profile → API tokens → Create token → "Edit Cloudflare Workers"* template; add the permission
   *Account → D1 → Edit*. Copy the token. Copy your **account ID** (on the Workers & Pages overview page).
4. **Shop key**: on the hub's till, *More → Website (online orders) → Make a new key*. Copy it (and Save there later).
5. **GitHub**: in this repository, *Settings → Secrets and variables → Actions*:
   * *Secrets*: `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID`, `SHOP_KEY` (the three values above);
   * *Variables*: `D1_DATABASE_ID`.
6. **Deploy**: *Actions → Website → Run workflow*. When it is green, the website is at
   `https://pos-online.<your-account>.workers.dev`.
7. **Your address**: Cloudflare → *Workers & Pages → pos-online → Settings → Domains & Routes → Add → Custom domain*,
   e.g. `order.yourshop.co.uk` (or the bare domain).
8. **Connect the shop**: on the till, *More → Website*: tick *Connect*, type the website address, check the opening
   times, collection and delivery, tick *Take website orders now*, Save. Within a few seconds the screen says
   *Connected* and the menu is on the website.

Updating the website later: run *Actions → Website* again. A new shop key: make it on the till, put it in the GitHub
secret `SHOP_KEY`, run the workflow, then Save on the till.

## Trying it on a computer

```
node --no-warnings online/dev/local.mjs 8787        # the website at http://127.0.0.1:8787 (shop key from POS_SHOP_KEY)
node --no-warnings --test online/test/online.test.mjs
```

`dev/local.mjs` runs the same code with Node's built-in SQLite instead of Cloudflare's D1, so nothing needs installing.
