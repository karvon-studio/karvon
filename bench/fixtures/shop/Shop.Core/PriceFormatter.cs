using System.Globalization;

namespace Shop.Core;

public static class PriceFormatter
{
    // 1234.5 -> "1,234.50", the same on every machine.
    public static string Format(decimal amount) =>
        amount.ToString("#,##0.00", CultureInfo.InvariantCulture);
}
