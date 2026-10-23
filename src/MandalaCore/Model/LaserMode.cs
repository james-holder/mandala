namespace MandalaMaker.Model;

public enum LaserMode
{
    /// <summary>Every outline exactly as generated. Overlaps get cut twice.</summary>
    Raw,

    /// <summary>Overlapping shapes within each layer are unioned into one clean outline.</summary>
    MergeLayers,

    /// <summary>Only the lines you can actually see, so hidden lines under upper layers are removed.</summary>
    VisibleLines,

    /// <summary>A single outer outline of everything filled, unioned together.</summary>
    Silhouette,

    /// <summary>Silhouette as a red cut line, with the visible interior lines as a blue score line.</summary>
    SilhouetteWithDetail,
}
