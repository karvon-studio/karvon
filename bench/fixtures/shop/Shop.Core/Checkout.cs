namespace Shop.Core;

public sealed record Receipt(decimal Subtotal, decimal Discount, decimal Total);

public static class Checkout
{
    // 10% off from 100.00, 15% off from 250.00.
    public static decimal RateFor(decimal subtotal) =>
        subtotal >= 250m ? 0.15m :
        subtotal >= 100m ? 0.10m :
        0m;

    public static Receipt Run(Order cart)
    {
        decimal subtotal = cart.Total;
        decimal rate = RateFor(subtotal);
        decimal discounted = Math.Round(subtotal * (1 - rate), 2);
        decimal discount = subtotal - discounted;
        decimal total = subtotal - discount;
        return new Receipt(subtotal, discount, total);
    }
}
