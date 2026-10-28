using Shop.Core;
using Xunit;

namespace Tests;

public class CheckoutTests
{
    [Fact]
    public void No_discount_under_100() => Assert.Equal(0m, Checkout.RateFor(99.99m));

    [Fact]
    public void Ten_percent_from_100() => Assert.Equal(0.10m, Checkout.RateFor(100m));

    [Fact]
    public void Fifteen_percent_from_250() => Assert.Equal(0.15m, Checkout.RateFor(250m));

    [Fact]
    public void Small_cart_pays_its_subtotal()
    {
        var cart = new Order(5, "Femi",
        [
            new OrderLine("COF-1", "Coffee beans 1kg", 18.50m, 2),
            new OrderLine("FLT-3", "Paper filters", 3.10m, 1),
        ]);

        var receipt = Checkout.Run(cart);

        Assert.Equal(new Receipt(40.10m, 0m, 40.10m), receipt);
    }
}
