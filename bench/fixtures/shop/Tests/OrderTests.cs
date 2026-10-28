using Shop.Core;
using Xunit;

namespace Tests;

public class OrderTests
{
    private static readonly OrderLine Coffee = new("COF-1", "Coffee beans 1kg", 18.50m, 2);
    private static readonly OrderLine Filters = new("FLT-3", "Paper filters", 3.10m, 1);

    [Fact]
    public void Line_total_is_unit_price_times_quantity() =>
        Assert.Equal(14.50m, new OrderLine("MUG-2", "Mug", 7.25m, 2).LineTotal);

    [Fact]
    public void Total_adds_up_the_line_totals() =>
        Assert.Equal(40.10m, new Order(1, "Ali", [Coffee, Filters]).Total);

    [Fact]
    public void Empty_order_costs_nothing() => Assert.Equal(0m, new Order(2, "Bea", []).Total);

    [Fact]
    public void Item_count_adds_up_the_quantities() =>
        Assert.Equal(3, new Order(3, "Chen", [Coffee, Filters]).ItemCount);

    [Fact]
    public void With_line_returns_a_new_order()
    {
        var order = new Order(4, "Eva", [Filters]);

        var longer = order.WithLine(Filters with { Sku = "FLT-4" });

        Assert.Single(order.Lines);
        Assert.Equal(2, longer.Lines.Count);
        Assert.Equal(6.20m, longer.Total);
    }
}
