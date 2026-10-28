import { addItem, removeItem, shippingCents, subtotalCents, totalCents, type CartLine } from './cart.ts';
import { catalog } from './catalog.ts';
import { formatCents } from './money.ts';

const app = document.querySelector<HTMLDivElement>('#app')!;
let lines: CartLine[] = [];
let discount = 0;

function render(): void {
  const subtotal = subtotalCents(lines);
  const discounted = Math.round((subtotal * (100 - discount)) / 100);
  app.innerHTML = `
    <h1>Shop</h1>
    <ul class="catalog">
      ${catalog.map((p) => `<li>${p.name} ${formatCents(p.priceCents)} <button data-add="${p.sku}">Add</button></li>`).join('')}
    </ul>
    <h2>Cart</h2>
    <ul class="cart">
      ${lines.map((l) => `<li>${l.quantity} x ${l.name} <button data-remove="${l.sku}">Remove</button></li>`).join('')}
    </ul>
    <label>Discount % <input id="discount" type="number" min="0" max="100" value="${discount}" /></label>
    <p>Subtotal ${formatCents(subtotal)}, shipping ${formatCents(shippingCents(discounted))}</p>
    <p><strong>Total ${formatCents(totalCents(lines, discount))}</strong></p>
  `;
}

app.addEventListener('click', (event) => {
  const button = (event.target as HTMLElement).closest('button');
  if (!button) return;
  const add = catalog.find((p) => p.sku === button.dataset.add);
  if (add) lines = addItem(lines, add);
  if (button.dataset.remove) lines = removeItem(lines, button.dataset.remove);
  render();
});

app.addEventListener('change', (event) => {
  const input = event.target as HTMLInputElement;
  if (input.id === 'discount') {
    discount = Math.min(100, Math.max(0, Number(input.value) || 0));
    render();
  }
});

render();
