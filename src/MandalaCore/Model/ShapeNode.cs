namespace MandalaMaker.Model;

/// <summary>
/// One outline point of a custom motif, in normalized motif space.
/// X runs from -1 (toward the center) to +1 (outward) and is scaled by Length/2.
/// Y runs from -1 to +1 across the ring and is scaled by Width/2.
/// A sharp point is a corner: smoothing doesn't round it off.
/// </summary>
public sealed record ShapeNode(double X, double Y, bool Sharp = false);
