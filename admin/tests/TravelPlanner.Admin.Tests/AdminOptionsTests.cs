using TravelPlanner.Admin.Services;
using Xunit;

namespace TravelPlanner.Admin.Tests;

public class AdminOptionsTests
{
    [Theory]
    [InlineData("admin@example.com", true)]
    [InlineData(" Admin@Example.com ", true)]
    [InlineData("other@example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_listed_addresses_are_admins(string? email, bool expected)
    {
        var options = new AdminOptions { Emails = ["admin@example.com", " "] };
        Assert.Equal(expected, options.IsAdmin(email));
    }

    [Fact]
    public void An_empty_list_lets_nobody_in() =>
        Assert.False(new AdminOptions().IsAdmin("admin@example.com"));
}
