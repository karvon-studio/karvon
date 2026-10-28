using Shop.Core;
using Xunit;

namespace Tests;

public class PriceFormatterTests
{
    [Fact]
    public void Shows_two_decimals() => Assert.Equal("5.00", PriceFormatter.Format(5m));

    [Fact]
    public void Groups_thousands() => Assert.Equal("1,234.50", PriceFormatter.Format(1234.5m));

    [Fact]
    public void Keeps_the_sign() => Assert.Equal("-3.20", PriceFormatter.Format(-3.2m));
}
