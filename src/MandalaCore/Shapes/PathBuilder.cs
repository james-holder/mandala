using System;
using System.Globalization;
using System.Text;

namespace MandalaMaker.Shapes;

/// <summary>
/// Builds SVG path data, passing each point through a rotate+translate transform.
/// Curves are kept as cubics only, so a rigid transform never distorts them.
/// Closed outlines and open lines are collected separately. Open lines (veins, stripes, ribs) must only be stroked:
/// if they were filled, the fill would close them with a straight line and punch holes in the shape.
/// </summary>
public sealed class PathBuilder
{
    public const double Kappa = 0.5522847498; // cubic approximation of a quarter circle

    private readonly StringBuilder _closed = new();
    private readonly StringBuilder _open = new();
    private StringBuilder _sb = new(); // the subpath being built
    private double _cos = 1, _sin, _tx, _ty;

    public void SetTransform(double angleRad, double tx, double ty)
    {
        _cos = Math.Cos(angleRad);
        _sin = Math.Sin(angleRad);
        _tx = tx;
        _ty = ty;
    }

    public void ResetTransform() => SetTransform(0, 0, 0);

    /// <summary>
    /// Runs <paramref name="draw"/> in a sub-frame: moved to (dx, dy) and turned by <paramref name="angleRad"/>
    /// relative to the current frame. Used for things like the petals of a daisy.
    /// </summary>
    public void WithFrame(double dx, double dy, double angleRad, Action draw)
    {
        double cos = _cos, sin = _sin, tx = _tx, ty = _ty;
        _tx += dx * cos - dy * sin;
        _ty += dx * sin + dy * cos;
        double c = Math.Cos(angleRad), s = Math.Sin(angleRad);
        _cos = cos * c - sin * s;
        _sin = sin * c + cos * s;
        try { draw(); }
        finally { _cos = cos; _sin = sin; _tx = tx; _ty = ty; }
    }

    /// <summary>Runs <paramref name="draw"/> with the local origin moved by (dx, dy) in the current frame.</summary>
    public void WithOffset(double dx, double dy, Action draw)
    {
        double tx = _tx, ty = _ty;
        _tx += dx * _cos - dy * _sin;
        _ty += dx * _sin + dy * _cos;
        try { draw(); }
        finally { _tx = tx; _ty = ty; }
    }

    public void MoveTo(double x, double y)
    {
        FlushOpen();
        Command('M', x, y);
    }

    /// <summary>A subpath that was started but never closed is an open line.</summary>
    private void FlushOpen()
    {
        if (_sb.Length == 0) return;
        _open.Append(_sb);
        _sb.Clear();
    }

    public void LineTo(double x, double y) => Command('L', x, y);

    public void CubicTo(double x1, double y1, double x2, double y2, double x, double y)
    {
        _sb.Append('C');
        Point(x1, y1); _sb.Append(' ');
        Point(x2, y2); _sb.Append(' ');
        Point(x, y); _sb.Append(' ');
    }

    /// <summary>Quadratic curve expressed as the equivalent cubic.</summary>
    public void QuadTo(double fromX, double fromY, double cx, double cy, double x, double y) =>
        CubicTo(fromX + 2.0 / 3 * (cx - fromX), fromY + 2.0 / 3 * (cy - fromY),
                x + 2.0 / 3 * (cx - x), y + 2.0 / 3 * (cy - y), x, y);

    /// <summary>
    /// Circular arc. It stays valid under rotation and translation because the radius doesn't change and rotation keeps the sweep direction.
    /// </summary>
    public void ArcTo(double radius, bool largeArc, bool sweep, double x, double y)
    {
        _sb.Append('A').Append(Format(radius)).Append(',').Append(Format(radius))
           .Append(" 0 ").Append(largeArc ? '1' : '0').Append(' ').Append(sweep ? '1' : '0').Append(' ');
        Point(x, y);
        _sb.Append(' ');
    }

    public void Close()
    {
        _sb.Append("Z ");
        _closed.Append(_sb);
        _sb.Clear();
    }

    /// <summary>Closed ellipse. Set reverse to wind it the other way, which cuts a hole under the nonzero fill rule.</summary>
    public void Ellipse(double cx, double cy, double rx, double ry, bool reverse = false)
    {
        double kx = rx * Kappa, ky = ry * Kappa;
        double s = reverse ? -1 : 1;
        MoveTo(cx + rx, cy);
        CubicTo(cx + rx, cy + s * ky, cx + kx, cy + s * ry, cx, cy + s * ry);
        CubicTo(cx - kx, cy + s * ry, cx - rx, cy + s * ky, cx - rx, cy);
        CubicTo(cx - rx, cy - s * ky, cx - kx, cy - s * ry, cx, cy - s * ry);
        CubicTo(cx + kx, cy - s * ry, cx + rx, cy - s * ky, cx + rx, cy);
        Close();
    }

    private void Command(char c, double x, double y)
    {
        _sb.Append(c);
        Point(x, y);
        _sb.Append(' ');
    }

    private void Point(double x, double y)
    {
        double gx = x * _cos - y * _sin + _tx;
        double gy = x * _sin + y * _cos + _ty;
        _sb.Append(Format(gx)).Append(',').Append(Format(gy));
    }

    private static string Format(double v)
    {
        v = Math.Round(v, 3);
        if (v == 0) v = 0; // avoid "-0"
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Closed outlines only: the part that may be filled.</summary>
    public string ClosedData => _closed.ToString().TrimEnd();

    /// <summary>Open lines only: stroke them, never fill them.</summary>
    public string OpenData
    {
        get
        {
            FlushOpen();
            return _open.ToString().TrimEnd();
        }
    }

    /// <summary>Everything: closed outlines followed by open lines.</summary>
    public override string ToString()
    {
        string closed = ClosedData, open = OpenData;
        return open.Length == 0 ? closed : closed.Length == 0 ? open : closed + " " + open;
    }
}
