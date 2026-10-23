namespace MandalaMaker.Model;

/// <summary>A small shape drawn inside every copy of a motif.</summary>
public enum InnerMotif
{
    None,
    Circle,
    Bullseye,
    Star,
    Triangle,
    Diamond,
    Heart,
    Petal,
    Teardrop,
    TwoOvals,
    ThreeOvals,
}

/// <summary>How echo outlines are drawn.</summary>
public enum EchoStyle
{
    Lines,    // solid echo outlines
    Dots,     // a row of small dots following the shape
    Dashes,   // a dashed line
    Diamonds, // tiny diamonds strung along the path
    Leaves,   // small leaves alternating left and right, like a garland
    Scallops, // a lacy scalloped line
    Zigzag,   // a rickrack line
    Mixed,    // first echo solid, the others in the Echo accent style
    Fringe,   // first echo solid with short slanted ticks between it and the outline, like a fern or feather edge
}

/// <summary>The decoration used for the inner echoes when the echo style is Mixed.</summary>
public enum EchoAccent
{
    Dots,
    Dashes,
    Diamonds,
    Leaves,
    Scallops,
    Zigzag,
}

/// <summary>Line pattern drawn inside every copy of a motif, clipped to its outline.</summary>
public enum HatchStyle
{
    None,
    Across,   // stripes across the motif (perpendicular to the radius)
    Along,    // stripes running outward
    Cross,    // both
    Diagonal,
    Chevron,  // leaf veins: a center line with angled ribs
    Fan,      // rays fanning out from the base
}

/// <summary>How detailed a generated design is.</summary>
public enum Difficulty
{
    Simple,    // a few big layers, thick lines, no small decorations
    Medium,
    Intricate,
}
