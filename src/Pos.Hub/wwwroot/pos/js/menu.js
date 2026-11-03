// Reading the menu: categories, buttons, sizes, extras and meal-deal choices.
import { text } from './dom.js';

export class MenuView {
  constructor(state) {
    this.state = state;
    this.products = new Map(state.products.map(p => [p.id, p]));
    this.groups = new Map(state.modifierGroups.map(g => [g.id, g]));
    this.variantOwner = new Map();
    for (const p of state.products) for (const v of p.variants) this.variantOwner.set(v.id, p);
  }

  get isEmpty() {
    return this.state.products.length === 0;
  }

  /** Categories shown on the till, in order. */
  categories() {
    return this.state.categories.filter(c => !c.isHidden).sort((a, b) => a.sortOrder - b.sortOrder);
  }

  /** Items in a category, in button order. */
  productsIn(categoryId, includeHidden = false) {
    return this.state.products
      .filter(p => p.categoryId === categoryId && (includeHidden || !p.isHidden))
      .sort((a, b) => a.sortOrder - b.sortOrder);
  }

  /** Name on the button. */
  buttonName(product) {
    return text(product.buttonName ?? product.name);
  }

  /** Lowest price, for "from £7.99" on items with sizes. */
  fromPrice(product) {
    return Math.min(...product.variants.map(v => v.price.minorUnits));
  }

  isSoldOut(product) {
    return product.isSoldOut || product.variants.every(v => v.isSoldOut);
  }

  modifierGroups(product) {
    return (product.modifierGroupIds ?? []).map(id => this.groups.get(id)).filter(Boolean);
  }

  /** True when picking the item needs a choice (size or extras) before it goes on the order. */
  needsOptions(product) {
    return product.variants.length > 1 || this.modifierGroups(product).length > 0;
  }

  isDeal(product) {
    return product.type === 'MealDeal';
  }

  /** The items (and fixed size, if any) a meal-deal slot allows, with any extra charge. */
  slotChoices(slot) {
    const choices = [];
    const seen = new Set();
    const add = (product, variantId, upcharge) => {
      const key = `${product.id}:${variantId ?? ''}`;
      if (!product || product.isHidden || seen.has(key) || this.isDeal(product)) return;
      seen.add(key);
      choices.push({ product, variantId, upcharge: upcharge?.minorUnits ?? 0 });
    };

    for (const option of slot.options) {
      if (option.kind === 'Product') add(this.products.get(option.targetId), null, option.upcharge);
      else if (option.kind === 'Variant') add(this.variantOwner.get(option.targetId), option.targetId, option.upcharge);
      else for (const p of this.productsIn(option.targetId)) add(p, null, option.upcharge);
    }

    return choices;
  }

  variant(product, variantId) {
    return product.variants.find(v => v.id === variantId);
  }
}
