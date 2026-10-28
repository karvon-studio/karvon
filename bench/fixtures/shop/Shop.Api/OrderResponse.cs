using Shop.Core;

namespace Shop.Api;

public sealed record LineResponse(string Sku, string Name, decimal UnitPrice, int Quantity, decimal LineTotal);

public sealed record OrderResponse(
    int Id,
    string Customer,
    string Currency,
    IReadOnlyList<LineResponse> Lines,
    decimal Total,
    string TotalText)
{
    public static OrderResponse From(Order order, decimal total) => new(
        order.Id,
        order.Customer,
        order.Currency,
        [.. order.Lines.Select(line => new LineResponse(line.Sku, line.Name, line.UnitPrice, line.Quantity, line.LineTotal))],
        total,
        PriceFormatter.Format(total));
}
