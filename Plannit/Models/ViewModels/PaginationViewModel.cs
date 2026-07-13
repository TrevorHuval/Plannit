namespace Plannit.Models.ViewModels;

// Shared windowed-pagination view model for _Pagination.cshtml. Any list page can
// populate one of these from its own filter route values to get the same
// first/prev/window/next/last controls and page-size selector.
public class PaginationViewModel
{
    public int Page { get; set; } = 1;
    public int TotalPages { get; set; } = 1;
    public int TotalCount { get; set; }
    public int PageSize { get; set; } = 50;
    public int[] PageSizeOptions { get; set; } = [25, 50, 100];

    public string Controller { get; set; } = null!;
    public string Action { get; set; } = "Index";

    // Every other active filter (accountId, searchText, ...) as strings, excluding
    // page/pageSize, so links and the page-size form can carry them forward.
    // Only set keys; omit filters that are unset rather than storing null/empty values.
    public Dictionary<string, string> RouteValues { get; set; } = new();

    public string ItemNounSingular { get; set; } = "item";
    public string ItemNounPlural { get; set; } = "items";
}
