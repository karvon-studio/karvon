export function formatCents(cents: number): string {
  const sign = cents < 0 ? '-' : '';
  const abs = Math.abs(Math.round(cents));
  const dollars = Math.floor(abs / 100)
    .toString()
    .replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  return `${sign}$${dollars}.${String(abs % 100).padStart(2, '0')}`;
}

export function parsePrice(text: string): number {
  const match = /^\s*\$?(\d+)(?:\.(\d{1,2}))?\s*$/.exec(text);
  if (!match) {
    throw new Error(`not a price: "${text}"`);
  }
  return Number(match[1]) * 100 + Number((match[2] ?? '').padEnd(2, '0'));
}
