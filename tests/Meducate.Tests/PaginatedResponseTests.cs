using Meducate.API.Infrastructure;

namespace Meducate.Tests;

public class PaginatedResponseTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var items = new[] { "a", "b", "c" };

        var response = new PaginatedResponse<string>(items, 100);

        Assert.Equal(items, response.Items);
        Assert.Equal(100, response.TotalCount);
    }

    [Fact]
    public void EmptyItems_WorksCorrectly()
    {
        var response = new PaginatedResponse<int>([], 0);

        Assert.Empty(response.Items);
        Assert.Equal(0, response.TotalCount);
    }
}
