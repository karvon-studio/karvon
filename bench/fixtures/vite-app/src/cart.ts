export interface Product {
  sku: string;
  name: string;
  priceCents: number;
}

export interface CartLine extends Product {
  quantity: number;
}

export const FREE_SHIPPING_FROM_CENTS = 5000;
export const SHIPPING_CENTS = 499;

export function addItem(lines: readonly CartLine[], product: Product, quantity = 1): CartLine[] {
  if (!Number.isInteger(quantity) || quantity < 1) {
    throw new RangeError(`quantity must be a positive whole number, got ${quantity}`);
  }
  if (!lines.some((line) => line.sku === product.sku)) {
    return [...lines, { ...product, quantity }];
  }
  return lines.map((line) =>
    line.sku === product.sku ? { ...line, quantity: line.quantity + quantity } : line,
  );
}

export function removeItem(lines: readonly CartLine[], sku: string): CartLine[] {
  return lines.filter((line) => line.sku !== sku);
}

export function subtotalCents(lines: readonly CartLine[]): number {
  return lines.reduce((sum, line) => sum + line.priceCents * line.quantity, 0);
}

export function shippingCents(amountCents: number): number {
  if (amountCents === 0 || amountCents >= FREE_SHIPPING_FROM_CENTS) {
    return 0;
  }
  return SHIPPING_CENTS;
}

// Shipping is free from 50.00 after the discount.
export function totalCents(lines: readonly CartLine[], discountPercent = 0): number {
  if (discountPercent < 0 || discountPercent > 100) {
    throw new RangeError(`discount must be between 0 and 100, got ${discountPercent}`);
  }
  const subtotal = subtotalCents(lines);
  const discounted = Math.round((subtotal * (100 - discountPercent)) / 100);
  return discounted + shippingCents(discounted);
}
