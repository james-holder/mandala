// Laser designs: number outlines read straight from a font file (opentype.js), so numbers can be cut as shapes.
window.mandalaLaser = (() => {
    const files = {
        "Playfair Display": "fonts/playfair-display-latin-700-normal.woff",
        "Fredoka": "fonts/fredoka-latin-600-normal.woff",
        "Nunito": "fonts/nunito-latin-800-normal.woff",
        "Bangers": "fonts/bangers-latin-400-normal.woff",
    };
    const loaded = new Map();

    async function font(name) {
        if (!loaded.has(name)) {
            loaded.set(name, (async () => {
                const res = await fetch(files[name] || files["Playfair Display"]);
                if (!res.ok) throw new Error("Couldn't load the font for the numbers.");
                return opentype.parse(await res.arrayBuffer());
            })());
        }
        return loaded.get(name);
    }

    // Outlines for each text, as SVG path data centered on the text's box (y down), in font units at size 100.
    // Returns { "12": { pathData, width, height }, ... }
    async function glyphs(fontName, texts) {
        const f = await font(fontName);
        const out = {};
        for (const t of texts) {
            const path = f.getPath(t, 0, 0, 100);
            const b = path.getBoundingBox();
            const cx = (b.x1 + b.x2) / 2, cy = (b.y1 + b.y2) / 2;
            out[t] = { pathData: toData(path.commands, cx, cy), width: b.x2 - b.x1, height: b.y2 - b.y1 };
        }
        return out;
    }

    // Path data using only M, L, C and Z (what the app's path reader understands), moved so the box center is 0,0.
    // Fonts often use quadratic curves (Q); each becomes the cubic curve with the same shape.
    function toData(commands, cx, cy) {
        const n = v => (Math.round(v * 1000) / 1000).toString();
        let s = "", px = 0, py = 0, sx = 0, sy = 0;
        for (const c of commands) {
            if (c.type === "M") { px = sx = c.x - cx; py = sy = c.y - cy; s += `M${n(px)},${n(py)}`; }
            else if (c.type === "L") { px = c.x - cx; py = c.y - cy; s += `L${n(px)},${n(py)}`; }
            else if (c.type === "C") {
                s += `C${n(c.x1 - cx)},${n(c.y1 - cy)} ${n(c.x2 - cx)},${n(c.y2 - cy)} ${n(c.x - cx)},${n(c.y - cy)}`;
                px = c.x - cx; py = c.y - cy;
            } else if (c.type === "Q") {
                const qx = c.x1 - cx, qy = c.y1 - cy, x = c.x - cx, y = c.y - cy;
                s += `C${n(px + 2 / 3 * (qx - px))},${n(py + 2 / 3 * (qy - py))} ${n(x + 2 / 3 * (qx - x))},${n(y + 2 / 3 * (qy - y))} ${n(x)},${n(y)}`;
                px = x; py = y;
            } else if (c.type === "Z") { s += "Z"; px = sx; py = sy; }
        }
        return s;
    }

    return { glyphs };
})();
