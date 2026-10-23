using System;
using System.Globalization;
using System.Security;
using System.Text;
using MandalaMaker.Model;
using MandalaMaker.Shapes;

namespace MandalaMaker.Rendering;

/// <summary>
/// Writes a design as SVG at true millimeter size. The web app uses the same output for the live preview
/// and for downloads, so what you see is exactly what you get. (Ported from the desktop app's SvgExporter.)
/// </summary>
public static class SvgWriter
{
    /// <param name="coloringPage">Black outlines on white with every shape filled white, as printed in a coloring book.</param>
    public static string Export(MandalaDocument doc, bool coloringPage = false)
    {
        double d = doc.DiameterMm, r = d / 2;
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(d)}mm\" height=\"{F(d)}mm\" viewBox=\"{F(-r)} {F(-r)} {F(d)} {F(d)}\">");

        var (bg, bgA) = ColorHex.ToSvg(coloringPage ? "#FFFFFF" : doc.BackgroundColor);
        if (bgA > 0)
            sb.AppendLine($"  <rect x=\"{F(-r)}\" y=\"{F(-r)}\" width=\"{F(d)}\" height=\"{F(d)}\" fill=\"{bg}\"{Opacity("fill-opacity", bgA)}/>");

        for (int i = doc.Layers.Count - 1; i >= 0; i--)
        {
            var layer = doc.Layers[i];
            if (!layer.Visible) continue;
            var (data, openData) = ShapeGenerator.BuildPathParts(layer);
            if (string.IsNullOrWhiteSpace(data) && string.IsNullOrWhiteSpace(openData)) continue;

            // On a coloring page every shape is filled white, so layers in front still hide what's behind them
            bool showsFill = coloringPage ? layer.Filled : doc.ShowsFill(layer);
            string fillColor = coloringPage ? "#FFFFFF" : layer.FillColor;
            var (strokeWidth, strokeColor) = doc.EffectiveStroke(layer);
            if (coloringPage) { strokeColor = "#000000"; if (layer.Filled && strokeWidth <= 0) strokeWidth = 0.4; }

            var attrs = new StringBuilder();
            if (showsFill)
            {
                var (fill, fa) = ColorHex.ToSvg(fillColor);
                attrs.Append($" fill=\"{fill}\"").Append(Opacity("fill-opacity", fa));
            }
            else attrs.Append(" fill=\"none\"");

            if (strokeWidth > 0)
            {
                var (stroke, sa) = ColorHex.ToSvg(strokeColor);
                attrs.Append($" stroke=\"{stroke}\" stroke-width=\"{F(strokeWidth)}\" stroke-linejoin=\"round\" stroke-linecap=\"round\"")
                     .Append(Opacity("stroke-opacity", sa));
            }

            sb.AppendLine($"  <g id=\"layer-{doc.Layers.Count - i}\">");
            sb.AppendLine($"    <title>{SecurityElement.Escape(layer.Name)}</title>");
            if (!string.IsNullOrWhiteSpace(data))
                sb.AppendLine($"    <path d=\"{data}\"{attrs}/>");

            var (dw, dcol) = doc.DetailStroke(layer);
            if (coloringPage) dcol = "#000000";
            var (dstroke, da) = ColorHex.ToSvg(dcol);
            string detailStyle = $" fill=\"none\" stroke=\"{dstroke}\" stroke-width=\"{F(dw)}\" stroke-linejoin=\"round\" stroke-linecap=\"round\"{Opacity("stroke-opacity", da)}";

            // The shape's own open lines (stripes, ribs, veins): stroked like the outline, never filled
            if (!string.IsNullOrWhiteSpace(openData))
            {
                string openStyle = strokeWidth > 0
                    ? $" fill=\"none\" stroke=\"{ColorHex.ToSvg(strokeColor).Rgb}\" stroke-width=\"{F(strokeWidth)}\" stroke-linejoin=\"round\" stroke-linecap=\"round\""
                    : detailStyle;
                sb.AppendLine($"    <path d=\"{openData}\"{openStyle}/>");
            }

            string detail = ShapeGenerator.BuildDetailData(layer);
            if (!string.IsNullOrWhiteSpace(detail))
                sb.AppendLine($"    <path d=\"{detail}\"{detailStyle}/>");
            sb.AppendLine("  </g>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string Opacity(string attr, double a) => a < 0.999 ? $" {attr}=\"{F(a)}\"" : "";

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Parses #RRGGBB and #AARRGGBB colors (the formats the designs use) for SVG output.</summary>
public static class ColorHex
{
    public static (string Rgb, double Opacity) ToSvg(string? hex)
    {
        string h = (hex ?? "").Trim().TrimStart('#');
        try
        {
            if (h.Length == 8)
                return ("#" + h.Substring(2).ToUpperInvariant(), Convert.ToInt32(h.Substring(0, 2), 16) / 255.0);
            if (h.Length == 6)
                return ("#" + h.ToUpperInvariant(), 1);
            if (h.Length == 3)
                return ("#" + string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]).ToUpperInvariant(), 1);
        }
        catch (FormatException) { }
        return ("#000000", 1);
    }
}
