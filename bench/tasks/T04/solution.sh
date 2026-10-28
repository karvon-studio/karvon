#!/usr/bin/env bash
# Format takes the currency and shows it after the amount; every caller passes one.
set -euo pipefail
python3 - <<'PY'
import re

def edit(path, *pairs):
    with open(path, encoding="utf-8", newline="") as f:
        text = f.read()
    for old, new in pairs:
        if old not in text:
            raise SystemExit(f"{path}: {old!r} not found")
        text = text.replace(old, new)
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)

edit("Shop.Core/PriceFormatter.cs",
     ('// 1234.5 -> "1,234.50", the same on every machine.', '// 1234.5, "USD" -> "1,234.50 USD", the same on every machine.'),
     ("public static string Format(decimal amount) =>\n        amount.ToString(\"#,##0.00\", CultureInfo.InvariantCulture);",
      "public static string Format(decimal amount, string currency) =>\n        $\"{amount.ToString(\"#,##0.00\", CultureInfo.InvariantCulture)} {currency}\";"))
edit("Components/PriceTag.razor",
     ("@PriceFormatter.Format(Amount)", "@PriceFormatter.Format(Amount, Currency)"),
     ("    public decimal Amount { get; set; }\n", "    public decimal Amount { get; set; }\n\n    [Parameter]\n    public string Currency { get; set; } = \"USD\";\n"))
edit("Shop.Api/OrderResponse.cs", ("PriceFormatter.Format(total)", "PriceFormatter.Format(total, order.Currency)"))
for amount in ("line.LineTotal", "receipt.Subtotal", "receipt.Discount", "receipt.Total"):
    edit("Shop.Cli/Program.cs", (f"PriceFormatter.Format({amount})", f"PriceFormatter.Format({amount}, cart.Currency)"))
edit("Tests/PriceFormatterTests.cs",
     ('Assert.Equal("5.00", PriceFormatter.Format(5m))', 'Assert.Equal("5.00 USD", PriceFormatter.Format(5m, "USD"))'),
     ('Assert.Equal("1,234.50", PriceFormatter.Format(1234.5m))', 'Assert.Equal("1,234.50 EUR", PriceFormatter.Format(1234.5m, "EUR"))'),
     ('Assert.Equal("-3.20", PriceFormatter.Format(-3.2m))', 'Assert.Equal("-3.20 USD", PriceFormatter.Format(-3.2m, "USD"))'))
PY
