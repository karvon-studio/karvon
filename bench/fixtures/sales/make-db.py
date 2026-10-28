#!/usr/bin/env python3
"""Builds shop.db: a small coffee-gear shop's sales from February to April 2026.

The data is the same on every run and every Python version: it comes from a fixed-seed LCG of its own, not from the
random module. Usage: python3 make-db.py [path], default shop.db next to this script.
"""
import os
import sqlite3
import sys
from datetime import datetime


class Lcg:
    def __init__(self, seed):
        self.state = seed

    def below(self, n):
        self.state = (self.state * 6364136223846793005 + 1442695040888963407) % (1 << 64)
        return (self.state >> 33) % n

    def pick(self, seq):
        return seq[self.below(len(seq))]

    def sample(self, seq, k):
        pool = list(seq)
        return [pool.pop(self.below(len(pool))) for _ in range(k)]


rng = Lcg(20260301)

# id, sku, name, category, price in cents
PRODUCTS = [
    (1, "CF-100", "Coffee Filters", "Brewing", 450),
    (2, "EC-090", "Espresso Cup", "Cups", 650),
    (3, "HB-1KG", "House Blend Beans", "Coffee", 2400),
    (4, "HG-200", "Hand Grinder", "Equipment", 8900),
    (5, "GK-600", "Gooseneck Kettle", "Equipment", 5400),
    (6, "PD-002", "Pour-Over Dripper", "Brewing", 2200),
    (7, "GC-600", "Glass Carafe", "Brewing", 2800),
    (8, "DS-500", "Digital Scale", "Equipment", 3600),
    (9, "TM-350", "Travel Mug", "Cups", 1900),
    (10, "CB-1L", "Cold Brew Bottle", "Brewing", 2600),
    (11, "MF-010", "Milk Frother", "Equipment", 3100),
    (12, "DT-012", "Descaling Tablets", "Care", 900),
    (13, "SO-250", "Single Origin Beans", "Coffee", 1800),
]
PRICE = {p[0]: p[4] for p in PRODUCTS}
APRIL_PRICE = {3: 2600, 13: 1950}  # beans got dearer in April

FIRST = ["Amelia", "Ben", "Chloe", "Daniel", "Elena", "Felix", "Grace", "Hugo", "Iris", "Jonas", "Kara", "Liam",
         "Maya", "Noah", "Olivia", "Paul", "Quinn", "Rosa", "Sam", "Tara", "Umar", "Vera", "Will", "Zoe"]
LAST = ["Adams", "Brooks", "Carter", "Diaz", "Evans", "Fischer", "Garcia", "Hughes", "Ito", "Jensen", "Kim", "Lopez",
        "Moreau", "Novak", "Okafor", "Patel", "Reyes", "Silva", "Turner", "Weber"]
CITIES = ["Austin", "Boston", "Chicago", "Denver", "Portland", "Seattle", "Toronto", "Dublin"]

MONTHS = {2: 28, 3: 31, 4: 30}
BASE_ORDERS = {2: 60, 3: 66, 4: 62}


def make_customers():
    seen, customers = set(), []
    while len(customers) < 48:
        first, last = rng.pick(FIRST), rng.pick(LAST)
        if (first, last) in seen:
            continue
        seen.add((first, last))
        cid = len(customers) + 1
        joined = f"2025-{1 + rng.below(12):02d}-{1 + rng.below(28):02d}"
        email = f"{first.lower()}.{last.lower()}@example.com"
        customers.append((cid, f"{first} {last}", email, rng.pick(CITIES), joined))
    return customers


def when(month, day=None):
    day = day or 1 + rng.below(MONTHS[month])
    return datetime(2026, month, day, 8 + rng.below(15), rng.below(60), rng.below(60))


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "shop.db")
    customers = make_customers()
    ids = [c[0] for c in customers]
    orders = []  # dicts: customer, at, lines {product: quantity}

    def new_order(month, customer=None, at=None):
        order = {"customer": customer or rng.pick(ids), "at": at or when(month), "lines": {}}
        orders.append(order)
        return order

    base = {m: [new_order(m) for _ in range(n)] for m, n in BASE_ORDERS.items()}

    def spread(month, product, quantities):
        free = [o for o in base[month] if product not in o["lines"]]
        for order, qty in zip(rng.sample(free, len(quantities)), quantities):
            order["lines"][product] = qty

    # Filters: steady, several packs at a time.
    spread(2, 1, [4, 6, 3, 5, 8, 4, 6, 2, 5, 4, 6, 3, 5, 4, 5])
    spread(3, 1, [6, 4, 5, 8, 3, 6, 4, 5, 6, 2, 5, 4, 8, 6, 3, 4, 5])
    spread(4, 1, [5, 6, 4, 8, 5, 3, 6, 4, 5, 6, 4, 5, 8, 3, 6, 6])
    # House blend: the most orders, one or two bags each.
    spread(2, 3, [1] * 14 + [2] * 4)
    spread(3, 3, [1] * 20 + [2] * 6)
    spread(4, 3, [1] * 16 + [2] * 4)
    # Everything else: a few orders a month, one or two at a time.
    for product in range(4, 14):
        for month in MONTHS:
            count = 4 + rng.below(9)
            spread(month, product, [1 + rng.below(2) if product in (9, 12, 13) else 1 for _ in range(count)])

    # Espresso cups: few orders but by the dozen (cafes), from a small set of buyers.
    cup_buyers = rng.sample(ids, 11)
    march_cups = [12, 6, 24, 6, 4, 12, 2, 6, 8, 2, 6, 4, 2, 4]
    repeat = rng.sample(cup_buyers, 3)
    for buyer, qty in zip(cup_buyers + repeat, march_cups):
        order = new_order(3, buyer)
        order["lines"][2] = qty
    orders[-1]["at"] = datetime(2026, 3, 31, 18, 42, 10)
    for month, quantities in ((2, [4, 2, 6]), (4, [6, 2, 4, 2])):
        for buyer, qty in zip(rng.sample(cup_buyers, len(quantities)), quantities):
            new_order(month, buyer)["lines"][2] = qty

    orders = [o for o in orders if o["lines"]]
    orders.sort(key=lambda o: o["at"])
    plain = [i for i, o in enumerate(orders) if not set(o["lines"]) & {1, 2, 3}]
    cancelled = set(rng.sample(plain, 6))

    if os.path.exists(path):
        os.remove(path)
    db = sqlite3.connect(path)
    db.executescript("""
        CREATE TABLE products (
            id INTEGER PRIMARY KEY,
            sku TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            category TEXT NOT NULL,
            price_cents INTEGER NOT NULL
        );
        CREATE TABLE customers (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            email TEXT NOT NULL UNIQUE,
            city TEXT NOT NULL,
            joined_on TEXT NOT NULL
        );
        CREATE TABLE orders (
            id INTEGER PRIMARY KEY,
            customer_id INTEGER NOT NULL REFERENCES customers(id),
            placed_at TEXT NOT NULL,
            status TEXT NOT NULL
        );
        CREATE TABLE order_items (
            id INTEGER PRIMARY KEY,
            order_id INTEGER NOT NULL REFERENCES orders(id),
            product_id INTEGER NOT NULL REFERENCES products(id),
            quantity INTEGER NOT NULL,
            unit_price_cents INTEGER NOT NULL
        );
        CREATE INDEX orders_customer ON orders(customer_id);
        CREATE INDEX orders_placed_at ON orders(placed_at);
        CREATE INDEX order_items_order ON order_items(order_id);
        CREATE INDEX order_items_product ON order_items(product_id);
    """)
    db.executemany("INSERT INTO products VALUES (?, ?, ?, ?, ?)",
                   [(p[0], p[1], p[2], p[3], APRIL_PRICE.get(p[0], p[4])) for p in PRODUCTS])
    db.executemany("INSERT INTO customers VALUES (?, ?, ?, ?, ?)", customers)
    item_id = 0
    for number, order in enumerate(orders, start=1):
        at = order["at"]
        if number - 1 in cancelled:
            status = "cancelled"
        elif at >= datetime(2026, 4, 28):
            status = "processing"
        elif at >= datetime(2026, 4, 23):
            status = "shipped"
        else:
            status = "delivered"
        db.execute("INSERT INTO orders VALUES (?, ?, ?, ?)",
                   (number, order["customer"], at.strftime("%Y-%m-%d %H:%M:%S"), status))
        for product in sorted(order["lines"]):
            item_id += 1
            price = APRIL_PRICE.get(product, PRICE[product]) if at.month == 4 else PRICE[product]
            db.execute("INSERT INTO order_items VALUES (?, ?, ?, ?, ?)",
                       (item_id, number, product, order["lines"][product], price))
    db.commit()
    check(db)
    db.execute("VACUUM")
    db.close()


def check(db):
    """The data must keep its shape: the March best seller by units is not the one with the most orders."""
    def ranked(sql, where):
        return db.execute(f"""
            SELECT p.name, {sql} AS v FROM order_items i
            JOIN orders o ON o.id = i.order_id JOIN products p ON p.id = i.product_id
            WHERE {where} GROUP BY p.id ORDER BY v DESC""").fetchall()

    march = "o.placed_at >= '2026-03-01' AND o.placed_at < '2026-04-01'"
    for where in (march, march + " AND o.status <> 'cancelled'"):
        units = ranked("SUM(i.quantity)", where)
        orders = ranked("COUNT(DISTINCT o.id)", where)
        revenue = ranked("SUM(i.quantity * i.unit_price_cents)", where)
        buyers = ranked("COUNT(DISTINCT o.customer_id)", where)
        top = units[0][0]
        assert units[0][1] - units[1][1] > 12, units[:3]
        assert orders[0][0] != top and revenue[0][0] != top, (orders[:2], revenue[:2])
        top_orders = dict(orders)[top]
        top_buyers = dict(buyers)[top]
        assert top_orders != top_buyers, (top_orders, top_buyers)
    everyone = ranked("COUNT(DISTINCT o.customer_id)", "1")
    assert dict(everyone)[top] == top_buyers
    assert ranked("SUM(i.quantity)", "1")[0][0] != top
    counts = {t: db.execute(f"SELECT COUNT(*) FROM {t}").fetchone()[0]
              for t in ("products", "customers", "orders", "order_items")}
    print(f"shop.db: {counts}")


if __name__ == "__main__":
    main()
