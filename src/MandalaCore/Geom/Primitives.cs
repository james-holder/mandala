using System;

namespace MandalaMaker.Geom;

/// <summary>A 2D point (millimeters). Mirrors the parts of WPF's Point the shape code uses, so it runs anywhere.</summary>
public struct Point
{
    public double X;
    public double Y;

    public Point(double x, double y) { X = x; Y = y; }

    public static Vector operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
    public static Point operator +(Point p, Vector v) => new(p.X + v.X, p.Y + v.Y);
    public static Point operator -(Point p, Vector v) => new(p.X - v.X, p.Y - v.Y);

    public override string ToString() => $"{X},{Y}";
}

/// <summary>A 2D vector. Mirrors the parts of WPF's Vector the shape code uses.</summary>
public struct Vector
{
    public double X;
    public double Y;

    public Vector(double x, double y) { X = x; Y = y; }

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSquared => X * X + Y * Y;

    public void Normalize()
    {
        double len = Length;
        if (len <= 0) return;
        X /= len;
        Y /= len;
    }

    public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector operator -(Vector a, Vector b) => new(a.X - b.X, a.Y - b.Y);
    public static Vector operator -(Vector v) => new(-v.X, -v.Y);
    public static Vector operator *(Vector v, double s) => new(v.X * s, v.Y * s);
    public static Vector operator *(double s, Vector v) => new(v.X * s, v.Y * s);
    public static Vector operator /(Vector v, double s) => new(v.X / s, v.Y / s);

    /// <summary>Dot product.</summary>
    public static double Multiply(Vector a, Vector b) => a.X * b.X + a.Y * b.Y;

    public static double CrossProduct(Vector a, Vector b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Signed angle from a to b, in degrees.</summary>
    public static double AngleBetween(Vector a, Vector b) =>
        Math.Atan2(CrossProduct(a, b), Multiply(a, b)) * 180 / Math.PI;
}

/// <summary>An axis-aligned rectangle. Mirrors the parts of WPF's Rect the shape code uses (including Empty and Union).</summary>
public struct Rect
{
    public double X, Y, Width, Height;
    private bool _set;

    public Rect(double x, double y, double width, double height)
    {
        X = x; Y = y; Width = width; Height = height; _set = true;
    }

    public static Rect Empty => default;
    public readonly bool IsEmpty => !_set;

    public readonly double Left => X;
    public readonly double Top => Y;
    public readonly double Right => X + Width;
    public readonly double Bottom => Y + Height;
    public readonly Point TopLeft => new(Left, Top);
    public readonly Point TopRight => new(Right, Top);
    public readonly Point BottomLeft => new(Left, Bottom);
    public readonly Point BottomRight => new(Right, Bottom);

    public void Union(Point p)
    {
        if (!_set) { X = p.X; Y = p.Y; Width = 0; Height = 0; _set = true; return; }
        double l = Math.Min(Left, p.X), t = Math.Min(Top, p.Y), r = Math.Max(Right, p.X), b = Math.Max(Bottom, p.Y);
        X = l; Y = t; Width = r - l; Height = b - t;
    }

    public static Rect Union(Rect rect, Point p)
    {
        rect.Union(p);
        return rect;
    }
}
