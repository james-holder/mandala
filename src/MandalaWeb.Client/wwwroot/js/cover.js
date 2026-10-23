// KDP paperback cover: back + spine + front with 0.125" bleed, drawn on a canvas.
// The same drawing is used for the on-screen preview (with guides) and the 300 DPI export.
window.mandalaCover = (() => {
    const BLEED = 0.125;          // inches, on every outside edge
    const SAFE = 0.25;            // keep text this far inside the trim (and away from the spine)
    const SPINE_TEXT_MIN_PAGES = 80; // KDP prints spine text only on books with more than 79 pages
    const SPINE_TEXT_CLEAR = 0.0625; // clearance on each side of spine text
    const BARCODE_W = 2, BARCODE_H = 1.2;
    const PER_PAGE = { white: 0.002252, cream: 0.0025 };
    const FONTS = {
        serif: "Georgia, 'Times New Roman', serif",
        elegant: "'Palatino Linotype', 'Book Antiqua', Palatino, Georgia, serif",
        clean: "'Segoe UI', 'Helvetica Neue', Arial, sans-serif",
        script: "'Segoe Script', 'Brush Script MT', 'Lucida Handwriting', cursive",
    };

    const imageCache = new Map();

    function geometry(spec) {
        const spine = spec.pages * (PER_PAGE[spec.paper] || PER_PAGE.white);
        const width = BLEED + spec.trimW + spine + spec.trimW + BLEED;
        const height = BLEED + spec.trimH + BLEED;
        const back = { x: BLEED, y: BLEED, w: spec.trimW, h: spec.trimH };
        const spineBox = { x: BLEED + spec.trimW, y: BLEED, w: spine, h: spec.trimH };
        const front = { x: BLEED + spec.trimW + spine, y: BLEED, w: spec.trimW, h: spec.trimH };
        // KDP's barcode sits at the lower right of the back cover; keep it clear
        const barcode = { x: back.x + back.w - SAFE - BARCODE_W, y: back.y + back.h - SAFE - BARCODE_H, w: BARCODE_W, h: BARCODE_H };
        return { spine, width, height, back, spineBox, front, barcode };
    }

    // The coloring-page SVG without its white square background, so the mandala sits on the cover color
    function prepareSvg(svgText, displayIn, minLineIn) {
        const doc = new DOMParser().parseFromString(svgText.replace(/^<\?xml[^>]*>/, ""), "image/svg+xml");
        const svg = doc.documentElement;
        const bg = svg.querySelector(":scope > rect");
        if (bg) bg.remove();
        const vb = svg.viewBox.baseVal;
        const perUnit = displayIn / (vb && vb.width ? vb.width : 1);
        svg.querySelectorAll("[stroke-width]").forEach(el => {
            const w = parseFloat(el.getAttribute("stroke-width"));
            if (!(w * perUnit >= minLineIn)) el.setAttribute("stroke-width", String(minLineIn / perUnit));
        });
        return new XMLSerializer().serializeToString(svg);
    }

    function loadImage(key, svgText) {
        if (imageCache.has(key)) return imageCache.get(key);
        const promise = new Promise((resolve, reject) => {
            const img = new Image();
            img.onload = () => resolve(img);
            img.onerror = reject;
            img.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svgText);
        });
        imageCache.set(key, promise);
        if (imageCache.size > 40) imageCache.delete(imageCache.keys().next().value);
        return promise;
    }

    async function drawMandala(ctx, dpi, svgText, cx, cy, diameterIn) {
        const prepared = prepareSvg(svgText, diameterIn, 0.35 / 25.4);
        // Rasterize at the final size so lines stay crisp (the browser renders the vector SVG at this size)
        const px = Math.max(1, Math.round(diameterIn * dpi));
        const img = await loadImage(px + ":" + prepared.length + ":" + prepared.slice(-64), prepared);
        ctx.drawImage(img, (cx - diameterIn / 2) * dpi, (cy - diameterIn / 2) * dpi, px, px);
    }

    function wrap(ctx, text, maxW) {
        const out = [];
        for (const para of String(text || "").split(/\n/)) {
            const words = para.split(/\s+/).filter(Boolean);
            let row = "";
            for (const w of words) {
                const test = row ? row + " " + w : w;
                if (ctx.measureText(test).width > maxW && row) { out.push(row); row = w; } else row = test;
            }
            out.push(row);
        }
        return out;
    }

    // Text centered at (cx, y) in inches, wrapped to maxWIn; returns the bottom y
    function text(ctx, dpi, str, cx, y, maxWIn, pt, font, color, bold) {
        if (!str) return y;
        ctx.fillStyle = color;
        ctx.textAlign = "center";
        ctx.textBaseline = "top";
        ctx.font = `${bold ? "bold " : ""}${pt * dpi / 72}px ${font}`;
        const lineH = pt / 72 * 1.2;
        const rows = wrap(ctx, str, maxWIn * dpi);
        rows.forEach((r, i) => ctx.fillText(r, cx * dpi, (y + i * lineH) * dpi));
        return y + rows.length * lineH;
    }

    // Largest font size (pt) at which the text fits on one line within maxWIn
    function fitPt(ctx, dpi, str, maxWIn, startPt, font, bold) {
        let pt = startPt;
        while (pt > 8) {
            ctx.font = `${bold ? "bold " : ""}${pt * dpi / 72}px ${font}`;
            if (ctx.measureText(str).width <= maxWIn * dpi) break;
            pt -= 1;
        }
        return pt;
    }

    async function draw(canvas, spec, dpi, guides) {
        const g = geometry(spec);
        canvas.width = Math.round(g.width * dpi);
        canvas.height = Math.round(g.height * dpi);
        const ctx = canvas.getContext("2d");
        const font = FONTS[spec.font] || FONTS.serif;
        const ink = spec.ink || "#222222";

        ctx.fillStyle = spec.bg || "#ffffff";
        ctx.fillRect(0, 0, canvas.width, canvas.height);

        // ---- Front: title, mandala, subtitle, author ----
        const f = g.front, fcx = f.x + f.w / 2, inner = f.w - 2 * SAFE - 0.25;
        let y = f.y + SAFE + 0.35;
        const titlePt = fitPt(ctx, dpi, spec.title || "", inner, 54, font, true);
        y = text(ctx, dpi, spec.title, fcx, y, inner, titlePt, font, ink, true) + 0.1;
        y = text(ctx, dpi, spec.subtitle, fcx, y, inner, 20, font, ink, false);
        const authorPt = 18;
        const authorTop = f.y + f.h - SAFE - 0.45;
        const room = authorTop - 0.2 - (y + 0.25);
        const diameter = Math.max(1, Math.min(f.w - 2 * SAFE - 0.3, room));
        if (spec.frontSvg) await drawMandala(ctx, dpi, spec.frontSvg, fcx, y + 0.25 + room / 2, diameter);
        text(ctx, dpi, spec.author, fcx, authorTop, inner, authorPt, font, ink, false);

        // ---- Back: blurb, preview grid, clear barcode area ----
        const b = g.back, bcx = b.x + b.w / 2, binner = b.w - 2 * SAFE - 0.5;
        let by = b.y + SAFE + 0.4;
        by = text(ctx, dpi, spec.blurb, bcx, by, binner, 14, font, ink, false) + 0.3;
        const thumbs = (spec.backSvgs || []).slice(0, 4);
        if (thumbs.length) {
            const bottom = g.barcode.y - 0.3;
            const cols = thumbs.length === 1 ? 1 : 2, rows = Math.ceil(thumbs.length / cols);
            const cell = Math.min((binner) / cols, (bottom - by) / rows);
            const startX = bcx - cols * cell / 2;
            for (let i = 0; i < thumbs.length; i++) {
                const c = i % cols, r = Math.floor(i / cols);
                if (cell > 0.6) await drawMandala(ctx, dpi, thumbs[i], startX + (c + 0.5) * cell, by + (r + 0.5) * cell, cell * 0.88);
            }
        }

        // ---- Spine text (only allowed on books with more than 79 pages) ----
        const s = g.spineBox;
        const spineText = [spec.title, spec.author].filter(Boolean).join("   •   ");
        if (spec.pages >= SPINE_TEXT_MIN_PAGES && spineText && s.w - 2 * SPINE_TEXT_CLEAR > 0.08) {
            const maxH = s.w - 2 * SPINE_TEXT_CLEAR;
            let pt = Math.min(maxH * 72 * 0.72, 24);
            ctx.font = `bold ${pt * dpi / 72}px ${font}`;
            const avail = (s.h - 2 * SAFE) * dpi;
            while (ctx.measureText(spineText).width > avail && pt > 5) { pt -= 0.5; ctx.font = `bold ${pt * dpi / 72}px ${font}`; }
            ctx.save();
            ctx.translate((s.x + s.w / 2) * dpi, (s.y + s.h / 2) * dpi);
            ctx.rotate(Math.PI / 2); // reads top to bottom, the US convention
            ctx.fillStyle = ink;
            ctx.textAlign = "center";
            ctx.textBaseline = "middle";
            ctx.fillText(spineText, 0, 0);
            ctx.restore();
        }

        if (guides) drawGuides(ctx, dpi, g, spec);
        return g;
    }

    function drawGuides(ctx, dpi, g, spec) {
        const rect = (r, color, dash, label) => {
            ctx.save();
            ctx.strokeStyle = color;
            ctx.lineWidth = Math.max(1, dpi / 70);
            ctx.setLineDash(dash.map(d => d * dpi / 70));
            ctx.strokeRect(r.x * dpi, r.y * dpi, r.w * dpi, r.h * dpi);
            if (label) {
                ctx.setLineDash([]);
                ctx.fillStyle = color;
                ctx.font = `${Math.max(10, dpi * 0.13)}px sans-serif`;
                ctx.textAlign = "left";
                ctx.textBaseline = "top";
                ctx.fillText(label, r.x * dpi + 4, r.y * dpi + 4);
            }
            ctx.restore();
        };
        // Trim line (where the cover is cut); everything outside it is bleed
        rect({ x: BLEED, y: BLEED, w: g.width - 2 * BLEED, h: g.height - 2 * BLEED }, "#d32f2f", [6, 4], "");
        // Spine edges
        rect(g.spineBox, "#1565c0", [3, 3], "");
        // Text-safe areas
        for (const p of [g.back, g.front])
            rect({ x: p.x + SAFE, y: p.y + SAFE, w: p.w - 2 * SAFE, h: p.h - 2 * SAFE }, "#2e7d32", [2, 4], "");
        // Barcode area
        ctx.save();
        ctx.fillStyle = "rgba(255,255,255,0.85)";
        ctx.fillRect(g.barcode.x * dpi, g.barcode.y * dpi, g.barcode.w * dpi, g.barcode.h * dpi);
        ctx.restore();
        rect(g.barcode, "#6a1b9a", [4, 3], "Barcode (KDP adds it)");
    }

    return {
        geometry,
        // Live preview with guide lines, fitted to the canvas's display width
        async preview(canvas, spec) {
            const g = geometry(spec);
            const cssWidth = canvas.parentElement ? canvas.parentElement.clientWidth : 900;
            const dpi = Math.max(20, Math.min(110, cssWidth / g.width));
            await draw(canvas, spec, dpi, true);
            return { widthIn: g.width, heightIn: g.height, spineIn: g.spine };
        },
        // 300 DPI PDF at the exact full-cover size, no guides
        async exportPdf(spec, fileName) {
            const g = geometry(spec);
            const canvas = document.createElement("canvas");
            await draw(canvas, spec, 300, false);
            const { jsPDF } = window.jspdf;
            const pdf = new jsPDF({ unit: "in", format: [g.width, g.height], orientation: "landscape", compress: true });
            pdf.addImage(canvas.toDataURL("image/jpeg", 0.95), "JPEG", 0, 0, g.width, g.height, undefined, "FAST");
            pdf.save(`${fileName}.pdf`);
            return { widthIn: g.width, heightIn: g.height, spineIn: g.spine };
        },
    };
})();
