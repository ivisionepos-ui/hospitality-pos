// Rules shared by the online service and the website: prices, delivery charges, opening hours, phone numbers.
// They mirror the hub's rules so the customer sees the same figures; the shop's till has the final say.

/** Pence → "£12.50". */
export const pounds = pence => `£${(pence / 100).toFixed(2)}`;

// ---------- Delivery (same as the hub: straight-line distance × road factor, to 0.1 mile; bands or per mile) ----------

const EARTH_RADIUS_MILES = 3958.8;

/** Estimated road miles between two points, rounded to one decimal like the hub. */
export function miles(from, to, roadFactor = 1.3) {
  const rad = d => (d * Math.PI) / 180;
  const dLat = rad(to.latitude - from.latitude);
  const dLon = rad(to.longitude - from.longitude);
  const a = Math.sin(dLat / 2) ** 2 + Math.cos(rad(from.latitude)) * Math.cos(rad(to.latitude)) * Math.sin(dLon / 2) ** 2;
  const straight = 2 * EARTH_RADIUS_MILES * Math.asin(Math.min(1, Math.sqrt(a)));
  return roundHalfAway(straight * roadFactor * 10) / 10;
}

const roundHalfAway = x => Math.sign(x) * Math.round(Math.abs(x));

/**
 * The delivery charge for a distance and food total, from the table the hub publishes:
 * { bands: [{ fromMiles, toMiles, feePence, minimumOrderPence?, freeOverPence? }], ratePerMilePence?, minimumFeePence?,
 *   maximumFeePence?, maximumMiles, minimumOrderPence?, freeOverPence? }.
 * Returns { status: 'Ok' | 'TooFar' | 'BelowMinimum', feePence, minimumOrderPence, free }.
 */
export function deliveryQuote(table, distanceMiles, goodsPence) {
  if (distanceMiles > table.maximumMiles) return { status: 'TooFar', feePence: 0, minimumOrderPence: null, free: false };
  let fee;
  let minimum = table.minimumOrderPence ?? null;
  let freeOver = table.freeOverPence ?? null;
  if (table.ratePerMilePence != null) {
    fee = Math.max(roundHalfAway(distanceMiles * table.ratePerMilePence), table.minimumFeePence ?? 0);
    if (table.maximumFeePence != null) fee = Math.min(fee, table.maximumFeePence);
  } else {
    // Bands include their start and exclude their end, except the last band, which includes its end.
    const bands = table.bands ?? [];
    const last = bands[bands.length - 1];
    const band = bands.find(b => distanceMiles >= b.fromMiles && (distanceMiles < b.toMiles || (b === last && distanceMiles === b.toMiles)));
    if (!band) return { status: 'TooFar', feePence: 0, minimumOrderPence: null, free: false };
    fee = band.feePence;
    minimum = band.minimumOrderPence ?? minimum;
    freeOver = band.freeOverPence ?? freeOver;
  }

  if (minimum != null && goodsPence < minimum) return { status: 'BelowMinimum', feePence: fee, minimumOrderPence: minimum, free: false };
  const free = freeOver != null && goodsPence >= freeOver;
  return { status: 'Ok', feePence: free ? 0 : fee, minimumOrderPence: minimum, free };
}

// ---------- UK postcodes and phone numbers ----------

/** "b12 9qx" → "B12 9QX"; null if it is not a UK postcode shape. */
export function postcode(text) {
  const t = String(text ?? '').toUpperCase().replace(/\s+/g, '');
  if (!/^[A-Z]{1,2}[0-9][A-Z0-9]?[0-9][A-Z]{2}$/.test(t)) return null;
  return `${t.slice(0, -3)} ${t.slice(-3)}`;
}

/** A UK phone number as digits starting 0 (e.g. "07700900123"); null if it is not one. */
export function ukPhone(text) {
  let d = String(text ?? '').replace(/[\s()-]/g, '');
  if (d.startsWith('+44')) d = `0${d.slice(3)}`;
  else if (d.startsWith('0044')) d = `0${d.slice(4)}`;
  if (!/^0\d{9,10}$/.test(d)) return null;
  return d;
}

// ---------- Opening hours (UK time) ----------

const DAYS = ['sun', 'mon', 'tue', 'wed', 'thu', 'fri', 'sat'];

/** The UK day and minutes since midnight now (handles British Summer Time). */
export function ukNow(date = new Date()) {
  const parts = Object.fromEntries(new Intl.DateTimeFormat('en-GB', {
    timeZone: 'Europe/London', weekday: 'short', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  }).formatToParts(date).map(p => [p.type, p.value]));
  return { day: parts.weekday.toLowerCase().slice(0, 3), minutes: Number(parts.hour) * 60 + Number(parts.minute) };
}

const toMinutes = hhmm => { const [h, m] = hhmm.split(':').map(Number); return h * 60 + m; };

/**
 * Whether the shop is open now. hours = { mon: [{ open: '17:00', close: '23:30' }], ... }. A close at or before the open
 * time runs past midnight (e.g. 17:00–02:00), and then counts on the next morning too.
 */
export function isOpen(hours, date = new Date()) {
  if (!hours) return false;
  const { day, minutes } = ukNow(date);
  const today = DAYS.indexOf(day);
  const yesterday = DAYS[(today + 6) % 7];
  for (const slot of hours[day] ?? []) {
    const open = toMinutes(slot.open);
    const close = toMinutes(slot.close);
    if (close > open ? minutes >= open && minutes < close : minutes >= open) return true;
  }

  for (const slot of hours[yesterday] ?? []) {
    const open = toMinutes(slot.open);
    const close = toMinutes(slot.close);
    if (close <= open && minutes < close) return true; // after midnight, from yesterday's late opening
  }

  return false;
}

// ---------- The basket, checked against the published menu ----------

export const LIMITS = { lines: 50, quantity: 50, note: 140 };

/**
 * Checks the customer's choices against the menu and prices them. lines = [{ productId, variantId, quantity,
 * optionIds: [], note }]. Returns { lines: [checked], goodsPence, problems: [text] }.
 */
export function priceBasket(menu, lines) {
  const problems = [];
  if (!Array.isArray(lines) || lines.length === 0) return { lines: [], goodsPence: 0, problems: ['Your basket is empty.'] };
  if (lines.length > LIMITS.lines) return { lines: [], goodsPence: 0, problems: [`At most ${LIMITS.lines} items in one order.`] };
  const products = new Map(menu.products.map(p => [p.id, p]));
  const groups = new Map(menu.groups.map(g => [g.id, g]));
  const checked = [];
  let goods = 0;
  for (const line of lines) {
    const product = products.get(line?.productId);
    if (!product) { problems.push('An item in your basket is no longer on the menu.'); continue; }
    const variant = product.variants.find(v => v.id === line.variantId);
    if (!variant) { problems.push(`${product.name}: choose a size.`); continue; }
    if (product.soldOut || variant.soldOut) { problems.push(`${product.name} has sold out.`); continue; }
    const quantity = Number(line.quantity);
    if (!Number.isInteger(quantity) || quantity < 1 || quantity > LIMITS.quantity) { problems.push(`${product.name}: quantity must be 1 to ${LIMITS.quantity}.`); continue; }
    const optionIds = Array.isArray(line.optionIds) ? [...new Set(line.optionIds)] : [];
    let unit = variant.pricePence;
    let ok = true;
    for (const groupId of product.groupIds) {
      const group = groups.get(groupId);
      if (!group) continue;
      const picked = group.options.filter(o => optionIds.includes(o.id));
      if (picked.length < group.min || picked.length > group.max) {
        problems.push(group.min === group.max
          ? `${product.name}: choose ${group.min} ${group.name.toLowerCase()}.`
          : `${product.name}: choose ${group.min ? `${group.min} to ` : 'up to '}${group.max} ${group.name.toLowerCase()}.`);
        ok = false;
      }

      for (const option of picked) {
        if (option.soldOut) { problems.push(`${product.name}: ${option.name} has sold out.`); ok = false; }
        unit += option.pricePence;
      }
    }

    const known = new Set(product.groupIds.flatMap(id => groups.get(id)?.options.map(o => o.id) ?? []));
    if (optionIds.some(id => !known.has(id))) { problems.push(`${product.name}: an extra is no longer available.`); ok = false; }
    const note = typeof line.note === 'string' ? line.note.trim().slice(0, LIMITS.note) : '';
    if (!ok) continue;
    goods += unit * quantity;
    checked.push({ productId: product.id, variantId: variant.id, quantity, optionIds, note: note || null, unitPence: unit });
  }

  return { lines: checked, goodsPence: goods, problems };
}
