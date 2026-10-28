using Shop.Core;

namespace Shop.Api;

// The orders, in memory. Each order's total is kept beside it so listing orders does not add up every line.
public sealed class OrderStore
{
    private readonly object gate = new();
    private readonly Dictionary<int, Order> orders = [];
    private readonly Dictionary<int, decimal> totals = [];

    public IReadOnlyList<Order> All
    {
        get { lock (gate) return [.. orders.Values.OrderBy(order => order.Id)]; }
    }

    public Order? Find(int id)
    {
        lock (gate) return orders.GetValueOrDefault(id);
    }

    public decimal TotalOf(int id)
    {
        lock (gate) return totals[id];
    }

    public void Add(Order order)
    {
        lock (gate)
        {
            orders[order.Id] = order;
            totals[order.Id] = order.Total;
        }
    }

    public Order AddLine(int id, OrderLine line)
    {
        lock (gate)
        {
            var order = orders[id].WithLine(line);
            orders[id] = order;
            totals[id] += line.LineTotal;
            return order;
        }
    }

    public static OrderStore Seeded()
    {
        var products = new Dictionary<string, (string Name, decimal Price)>
        {
            ["COF-1"] = ("Coffee beans 1kg", 18.50m),
            ["MUG-2"] = ("Mug", 7.25m),
            ["FLT-3"] = ("Paper filters", 3.10m),
            ["KTL-4"] = ("Kettle", 42.00m),
            ["GRN-5"] = ("Grinder", 64.90m),
            ["WRP-6"] = ("Gift wrap", 2.50m),
        };
        OrderLine Line(string sku, int quantity) => new(sku, products[sku].Name, products[sku].Price, quantity);

        var store = new OrderStore();
        store.Add(new Order(1, "Ali", [Line("COF-1", 1), Line("FLT-3", 2)]));
        store.Add(new Order(2, "Bea", [Line("MUG-2", 4)]));
        store.Add(new Order(3, "Chen", [Line("KTL-4", 1)]));
        store.Add(new Order(4, "Ravi", [Line("GRN-5", 1), Line("COF-1", 2)]));
        store.Add(new Order(5, "Eva", [Line("FLT-3", 5)]));
        store.Add(new Order(6, "Femi", [Line("COF-1", 3), Line("MUG-2", 2)]));
        store.Add(new Order(7, "Dana", [Line("KTL-4", 1), Line("MUG-2", 2), Line("COF-1", 1)]));
        store.Add(new Order(8, "Hugo", [Line("GRN-5", 1)]));

        // Lines added after the order was placed.
        store.AddLine(3, Line("WRP-6", 1));
        store.AddLine(7, Line("WRP-6", 3));
        return store;
    }
}
