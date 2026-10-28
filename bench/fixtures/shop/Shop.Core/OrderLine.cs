namespace Shop.Core;

public sealed record OrderLine(string Sku, string Name, decimal UnitPrice, int Quantity)
{
    public decimal LineTotal => UnitPrice * Quantity;
}
