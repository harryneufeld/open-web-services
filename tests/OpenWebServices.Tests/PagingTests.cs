using OpenWebServices.Web.Services;

namespace OpenWebServices.Tests;

public class PagingTests
{
    private static readonly IReadOnlyList<int> Items = [.. Enumerable.Range(1, 60)];

    [Fact]
    public void Pages_hold_25_items_and_the_last_page_the_rest()
    {
        Assert.Equal(3, Paging.PageCount(Items.Count));
        Assert.Equal(Enumerable.Range(1, 25), Paging.Slice(Items, 1));
        Assert.Equal(Enumerable.Range(51, 10), Paging.Slice(Items, 3));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(9, 3)]
    public void Out_of_range_pages_are_clamped(int requested, int expected) =>
        Assert.Equal(expected, Paging.Clamp(requested, Items.Count));

    [Fact]
    public void An_empty_list_has_one_empty_page()
    {
        Assert.Equal(1, Paging.PageCount(0));
        Assert.Empty(Paging.Slice(Array.Empty<int>(), 4));
    }
}
