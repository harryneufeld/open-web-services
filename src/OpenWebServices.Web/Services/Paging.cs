namespace OpenWebServices.Web.Services;

/// <summary>
/// Page arithmetic for long lists, so only one page of rows is rendered and sent to the browser. Pages are 1-based
/// and clamped, so a list that shrinks under a live update never shows an empty page.
/// </summary>
public static class Paging
{
    public const int PageSize = 25;

    public static int PageCount(int total) => Math.Max(1, (total + PageSize - 1) / PageSize);

    public static int Clamp(int page, int total) => Math.Clamp(page, 1, PageCount(total));

    public static IEnumerable<T> Slice<T>(IReadOnlyList<T> items, int page) =>
        items.Skip((Clamp(page, items.Count) - 1) * PageSize).Take(PageSize);
}
