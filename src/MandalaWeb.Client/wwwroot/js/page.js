// Page layout helpers: measuring and drawing text, pointer math for the editor, and putting a finished
// page together as one SVG for the gallery and book export.
window.mandalaPage = (() => {
    const PT_MM = 25.4 / 72;   // one point in mm
    const LINE = 1.2;          // line height, in multiples of the font size
    const TEXT_DPI = 600;      // text is printed as a black-and-white image this sharp, so the PDF needs no fonts
    const MIN_LINE_MM = 0.4;   // a little over KDP's 0.3 mm, since export may shrink the page slightly

    function font(t, px) {
        return `${t.weight || 400} ${px}px "${t.font}", sans-serif`;
    }

    async function ready(t) {
        try { await document.fonts.load(font(t, 40), t.text || "A"); } catch { /* fall back to a system font */ }
    }

    // Size of a text block in mm: { w, h }. t: { text, font, weight, sizePt, style, outlineMm }
    async function measure(t) {
        await ready(t);
        const ctx = document.createElement("canvas").getContext("2d");
        const px = 100; // measure at a large size for accuracy, then scale
        ctx.font = font(t, px);
        const lines = String(t.text || " ").split("\n");
        let widest = 0;
        for (const l of lines) widest = Math.max(widest, ctx.measureText(l || " ").width);
        const sizeMm = t.sizePt * PT_MM;
        const pad = t.style === "outline" ? Math.max(MIN_LINE_MM, t.outlineMm) : 0;
        return { w: widest / px * sizeMm + 2 * pad, h: lines.length * sizeMm * LINE + 2 * pad };
    }

    // Draws a text block at 600 DPI. Returns { dataUrl, w, h } (mm).
    async function textImage(t) {
        const size = await measure(t);
        const pxPerMm = TEXT_DPI / 25.4;
        const canvas = document.createElement("canvas");
        canvas.width = Math.max(1, Math.ceil(size.w * pxPerMm));
        canvas.height = Math.max(1, Math.ceil(size.h * pxPerMm));
        const ctx = canvas.getContext("2d");
        const sizePx = t.sizePt * PT_MM * pxPerMm;
        const pad = t.style === "outline" ? Math.max(MIN_LINE_MM, t.outlineMm) * pxPerMm : 0;
        ctx.font = font(t, sizePx);
        ctx.textBaseline = "middle";
        ctx.textAlign = t.align || "center";
        const x = ctx.textAlign === "left" ? pad : ctx.textAlign === "right" ? canvas.width - pad : canvas.width / 2;
        const lines = String(t.text || "").split("\n");
        lines.forEach((l, i) => {
            const y = pad + (i + 0.5) * sizePx * LINE;
            if (t.style === "outline") {
                // Hollow letters to color in: a black outline drawn around white letters
                const outline = Math.max(MIN_LINE_MM, t.outlineMm) * pxPerMm;
                ctx.lineJoin = "round";
                ctx.lineWidth = outline * 2; // half of it ends up hidden under the white fill
                ctx.strokeStyle = "#000";
                ctx.strokeText(l, x, y);
                ctx.fillStyle = "#fff";
                ctx.fillText(l, x, y);
            } else {
                ctx.fillStyle = "#000";
                ctx.fillText(l, x, y);
            }
        });
        // Pure black and white, so print doesn't pick up gray fringes; paper stays transparent
        const img = ctx.getImageData(0, 0, canvas.width, canvas.height), d = img.data;
        for (let i = 0; i < d.length; i += 4) {
            if (d[i + 3] < 128) { d[i + 3] = 0; continue; }
            const v = d[i] + d[i + 1] + d[i + 2] < 384 ? 0 : 255;
            d[i] = d[i + 1] = d[i + 2] = v;
            d[i + 3] = 255;
        }
        ctx.putImageData(img, 0, 0);
        return { dataUrl: canvas.toDataURL("image/png"), w: size.w, h: size.h };
    }

    // Where the page drawing is on screen, so pointer positions can be turned into page mm.
    // Uses the SVG's own screen transform, which accounts for the page being letterboxed in its area.
    function metrics(svg) {
        const m = svg.getScreenCTM();
        return { left: m.e, top: m.f, mmPerPx: 1 / m.a, x0: 0, y0: 0 };
    }

    function capture(el, pointerId) { try { el.setPointerCapture(pointerId); } catch { } }

    const num = v => Math.round(v * 1000) / 1000;

    // One design placed on the page: its content moved into a group, minus its white background square,
    // with every line at least MIN_LINE_MM at the size it's placed.
    function designGroup(svgText, it) {
        const doc = new DOMParser().parseFromString(svgText.replace(/^<\?xml[^>]*>/, ""), "image/svg+xml");
        const root = doc.documentElement;
        const vb = (root.getAttribute("viewBox") || "0 0 100 100").trim().split(/[\s,]+/).map(Number);
        const first = root.firstElementChild;
        if (first && first.tagName === "rect" && /^#?f{3,6}$|white/i.test(first.getAttribute("fill") || "")) first.remove();
        const scale = it.w / vb[2];
        const minUser = MIN_LINE_MM / scale;
        root.querySelectorAll("[stroke-width]").forEach(el => {
            const w = parseFloat(el.getAttribute("stroke-width"));
            if (!(w >= minUser)) el.setAttribute("stroke-width", String(num(minUser)));
        });
        const inner = Array.from(root.childNodes).map(n => new XMLSerializer().serializeToString(n)).join("")
            .replace(/ xmlns="http:\/\/www\.w3\.org\/2000\/svg"/g, "");
        const sy = it.h / vb[3];
        return `<g transform="translate(${num(it.x)} ${num(it.y)}) rotate(${num(it.rot)}) translate(${num(-it.w / 2)} ${num(-it.h / 2)}) ` +
            `scale(${num(scale)} ${num(sy)}) translate(${num(-vb[0])} ${num(-vb[1])})">${inner}</g>`;
    }

    // The finished page: only the area inside the margins (box, in page mm), white background, items in order.
    // spec: { box: {x, y, w, h}, items: [{ type: "design", svg, x, y, w, h, rot } | { type: "text", text, font, ..., x, y, rot }] }
    async function compose(spec) {
        const b = spec.box;
        let body = "";
        for (const it of spec.items) {
            if (it.type === "design" && it.svg) body += designGroup(it.svg, it) + "\n";
            else if (it.type === "text" && it.text && it.text.trim()) {
                const img = await textImage(it);
                body += `<g transform="translate(${num(it.x)} ${num(it.y)}) rotate(${num(it.rot)})">` +
                    `<image x="${num(-img.w / 2)}" y="${num(-img.h / 2)}" width="${num(img.w)}" height="${num(img.h)}" ` +
                    `preserveAspectRatio="none" href="${img.dataUrl}" xlink:href="${img.dataUrl}"/></g>\n`;
            }
        }
        return `<?xml version="1.0" encoding="UTF-8"?>\n` +
            `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" ` +
            `width="${num(b.w)}mm" height="${num(b.h)}mm" viewBox="${num(b.x)} ${num(b.y)} ${num(b.w)} ${num(b.h)}">\n` +
            `<rect x="${num(b.x)}" y="${num(b.y)}" width="${num(b.w)}" height="${num(b.h)}" fill="#FFFFFF"/>\n` +
            `<clipPath id="page-area"><rect x="${num(b.x)}" y="${num(b.y)}" width="${num(b.w)}" height="${num(b.h)}"/></clipPath>\n` +
            `<g clip-path="url(#page-area)">\n${body}</g>\n</svg>\n`;
    }

    return { measure, textImage, metrics, capture, compose };
})();
