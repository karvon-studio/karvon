using Components;
using Xunit;

namespace Tests;

public class GreetingTests
{
    [Fact]
    public void Default_name_is_world() => Assert.Equal("world", new Greeting().Name);
}
