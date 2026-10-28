namespace Shop.Core;

public sealed record Order(int Id, string Customer, IReadOnlyList<OrderLine> Lines)
{
    public string Currency { get; init; } = "USD";

    public decimal Total => Lines.Sum(line => line.LineTotal);

    public int ItemCount => Lines.Sum(line => line.Quantity);

    public Order WithLine(OrderLine line) => this with { Lines = [.. Lines, line] };
}
