namespace MandalaWeb.Client;

/// <summary>Book page sizes and the margins pages are laid out in.</summary>
public static class PageSizes
{
    /// <summary>Common KDP trim sizes for coloring books (inches).</summary>
    public static readonly Dictionary<string, (double W, double H)> Trim = new()
    {
        ["8.5 × 11"] = (8.5, 11),
        ["8.25 × 11"] = (8.25, 11),
        ["8 × 10"] = (8, 10),
        ["8.5 × 8.5"] = (8.5, 8.5),
        ["8.25 × 8.25"] = (8.25, 8.25),
        ["7 × 10"] = (7, 10),
        ["6 × 9"] = (6, 9),
    };

    // A page is laid out inside KDP's margins for a right-hand page: 0.5 in at the top, bottom and outside,
    // and 0.75 in on the spine side (the gutter for books up to 150 pages plus the 0.375 in extra the
    // export adds). Export then fits this area into the real margins of whichever book it goes in.
    public const double OutsideIn = 0.5, InsideIn = 0.75;
}
