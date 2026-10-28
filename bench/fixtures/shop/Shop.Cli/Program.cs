using System.Globalization;
using Shop.Core;

var cart = new Order(1001, "Walk-in customer",
[
    new OrderLine("GRN-5", "Grinder", 64.90m, 1),
    new OrderLine("COF-1", "Coffee beans 1kg", 18.50m, 3),
    new OrderLine("MUG-2", "Mug", 7.25m, 2),
]);

Receipt receipt = Checkout.Run(cart);

Console.WriteLine($"Cart {cart.Id} for {cart.Customer}, {cart.ItemCount} items");
foreach (var line in cart.Lines)
    Console.WriteLine($"  {line.Quantity} x {line.Name,-20}{PriceFormatter.Format(line.LineTotal),10}");
Console.WriteLine($"  {"Subtotal",-24}{PriceFormatter.Format(receipt.Subtotal),10}");
Console.WriteLine($"  {"Discount",-24}{PriceFormatter.Format(receipt.Discount),10}");
Console.WriteLine($"  {"Total",-24}{PriceFormatter.Format(receipt.Total),10}");

// The same in one line, for scripts.
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"subtotal={receipt.Subtotal:0.00} discount={receipt.Discount:0.00} total={receipt.Total:0.00}"));
