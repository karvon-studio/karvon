import { describe, expect, it } from 'vitest';
import { formatCents, parsePrice } from './money.ts';

describe('formatCents', () => {
  it('formats dollars and cents', () => {
    expect(formatCents(5)).toBe('$0.05');
    expect(formatCents(1450)).toBe('$14.50');
  });

  it('groups thousands and keeps the sign', () => {
    expect(formatCents(123456789)).toBe('$1,234,567.89');
    expect(formatCents(-250)).toBe('-$2.50');
  });
});

describe('parsePrice', () => {
  it('reads prices with or without a dollar sign and cents', () => {
    expect(parsePrice('$3')).toBe(300);
    expect(parsePrice('12.5')).toBe(1250);
    expect(parsePrice(' 0.99 ')).toBe(99);
  });

  it('rejects text that is not a price', () => {
    expect(() => parsePrice('twelve')).toThrow('not a price');
    expect(() => parsePrice('1.234')).toThrow();
  });
});
