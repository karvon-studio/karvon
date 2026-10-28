import { describe, expect, it } from 'vitest';
import { addItem, removeItem, subtotalCents, totalCents, type CartLine, type Product } from './cart.ts';

const mug: Product = { sku: 'MUG-01', name: 'Stoneware mug', priceCents: 1450 };
const pot: Product = { sku: 'POT-02', name: 'Glass teapot', priceCents: 3200 };

describe('addItem', () => {
  it('adds a new line and merges the same product into it', () => {
    let lines: CartLine[] = addItem([], mug);
    lines = addItem(lines, pot);
    lines = addItem(lines, mug, 2);
    expect(lines).toEqual([
      { ...mug, quantity: 3 },
      { ...pot, quantity: 1 },
    ]);
  });

  it('rejects a quantity that is not a positive whole number', () => {
    expect(() => addItem([], mug, 0)).toThrow(RangeError);
    expect(() => addItem([], mug, 1.5)).toThrow(RangeError);
  });
});

describe('removeItem', () => {
  it('removes the line of a product and keeps the others', () => {
    const lines = addItem(addItem([], mug), pot);
    expect(removeItem(lines, 'MUG-01')).toEqual([{ ...pot, quantity: 1 }]);
  });
});

describe('totals', () => {
  it('adds up price times quantity', () => {
    expect(subtotalCents(addItem(addItem([], mug, 2), pot))).toBe(6100);
  });

  it('charges shipping under 50.00', () => {
    expect(totalCents(addItem([], pot))).toBe(3200 + 499);
  });

  it('ships for free from 50.00', () => {
    expect(totalCents(addItem([], mug, 4))).toBe(5800);
  });

  it('charges shipping when a discount takes the order under 50.00', () => {
    expect(totalCents(addItem([], mug, 4), 20)).toBe(4640 + 499);
  });

  it('costs nothing when the cart is empty', () => {
    expect(totalCents([], 10)).toBe(0);
  });
});
