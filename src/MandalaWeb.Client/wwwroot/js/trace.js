// Turns clip art and drawings into black-and-white vector coloring pages.
// Steps: load the image onto white, make a black/white mask (one of three modes), clean it up,
// crop to the drawing, then trace the black areas into SVG paths with imagetracer.js.
window.mandalaTrace = (() => {
    let source = null;       // the image as loaded, drawn on white: { canvas, name }

    // Load a picked or dropped file. Returns { width, height, dataUrl } of the working copy (long side capped).
    async function load(dataUrl, name, maxSide) {
        const img = await new Promise((resolve, reject) => {
            const i = new Image();
            i.onload = () => resolve(i);
            i.onerror = () => reject(new Error("That file isn't an image the browser can read."));
            i.src = dataUrl;
        });
        const scale = Math.min(1, maxSide / Math.max(img.naturalWidth, img.naturalHeight));
        const canvas = document.createElement("canvas");
        canvas.width = Math.max(1, Math.round(img.naturalWidth * scale));
        canvas.height = Math.max(1, Math.round(img.naturalHeight * scale));
        const ctx = canvas.getContext("2d");
        ctx.fillStyle = "#ffffff"; // transparent clip art goes on white
        ctx.fillRect(0, 0, canvas.width, canvas.height);
        ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
        source = { canvas, name };
        return { width: canvas.width, height: canvas.height, dataUrl: canvas.toDataURL("image/png") };
    }

    // Lets the user pick an image file; returns { name, dataUrl } or null
    function pick() {
        return new Promise(resolve => {
            const input = document.createElement("input");
            input.type = "file";
            input.accept = "image/*";
            input.onchange = () => {
                const f = input.files[0];
                if (!f) { resolve(null); return; }
                const r = new FileReader();
                r.onload = () => resolve({ name: f.name, dataUrl: r.result });
                r.readAsDataURL(f);
            };
            input.oncancel = () => resolve(null);
            input.click();
        });
    }

    // Drag-and-drop and paste onto an element; calls back into .NET with (name, dataUrl)
    function watchDrop(el, dotnet) {
        const take = f => {
            if (!f || !f.type.startsWith("image/")) return false;
            const r = new FileReader();
            r.onload = () => dotnet.invokeMethodAsync("ImageDropped", f.name || "pasted image", r.result);
            r.readAsDataURL(f);
            return true;
        };
        el.addEventListener("dragover", e => { e.preventDefault(); el.classList.add("drop"); });
        el.addEventListener("dragleave", () => el.classList.remove("drop"));
        el.addEventListener("drop", e => {
            e.preventDefault();
            el.classList.remove("drop");
            take(e.dataTransfer.files[0]);
        });
        document.addEventListener("paste", e => {
            for (const item of e.clipboardData ? e.clipboardData.items : [])
                if (item.kind === "file" && take(item.getAsFile())) { e.preventDefault(); return; }
        });
    }

    // ---------- Masks: 1 = ink, 0 = paper ----------

    function gray(data, i) { return 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2]; }

    // Otsu's method: the threshold that best splits the image into dark and light
    function autoThreshold() {
        const { canvas } = source;
        const d = canvas.getContext("2d").getImageData(0, 0, canvas.width, canvas.height).data;
        const hist = new Array(256).fill(0);
        for (let i = 0; i < d.length; i += 4) hist[Math.round(gray(d, i))]++;
        const total = d.length / 4;
        let sum = 0;
        for (let t = 0; t < 256; t++) sum += t * hist[t];
        let sumB = 0, wB = 0, best = 0, bestT = 128;
        for (let t = 0; t < 256; t++) {
            wB += hist[t];
            if (wB === 0) continue;
            const wF = total - wB;
            if (wF === 0) break;
            sumB += t * hist[t];
            const mB = sumB / wB, mF = (sum - sumB) / wF;
            const between = wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; bestT = t; }
        }
        return bestT;
    }

    function thresholdMask(d, w, h, threshold, invert) {
        const m = new Uint8Array(w * h);
        for (let p = 0, i = 0; p < m.length; p++, i += 4) {
            const dark = gray(d, i) < threshold;
            m[p] = dark !== invert ? 1 : 0;
        }
        return m;
    }

    // Grows (or with grow=false, shrinks) the ink by one pixel, using the 8 neighbors
    function step(m, w, h, grow) {
        const out = new Uint8Array(m.length);
        for (let y = 0; y < h; y++)
            for (let x = 0; x < w; x++) {
                const p = y * w + x;
                let v = m[p];
                if (grow ? !v : v) {
                    for (let dy = -1; dy <= 1 && (grow ? !v : v); dy++)
                        for (let dx = -1; dx <= 1; dx++) {
                            const xx = x + dx, yy = y + dy;
                            const n = xx < 0 || yy < 0 || xx >= w || yy >= h ? 0 : m[yy * w + xx];
                            if (grow && n) { v = 1; break; }
                            if (!grow && !n) { v = 0; break; }
                        }
                }
                out[p] = v;
            }
        return out;
    }

    function repeat(m, w, h, grow, times) {
        for (let i = 0; i < times; i++) m = step(m, w, h, grow);
        return m;
    }

    // Solid shapes become outlines: keep only ink within `weight` pixels of paper
    function outlineOf(m, w, h, weight) {
        const inner = repeat(m, w, h, false, weight);
        const out = new Uint8Array(m.length);
        for (let p = 0; p < m.length; p++) out[p] = m[p] && !inner[p] ? 1 : 0;
        return out;
    }

    // Color clip art: reduce to a few colors (k-means), then draw lines where the colors meet
    function colorEdges(d, w, h, colors, weight) {
        const n = w * h;
        // Pick starting colors spread through the image, then refine on a sample of pixels
        const centers = [];
        for (let k = 0; k < colors; k++) {
            const p = Math.floor((k + 0.5) / colors * n) * 4;
            centers.push([d[p], d[p + 1], d[p + 2]]);
        }
        const stride = Math.max(1, Math.floor(n / 40000));
        for (let iter = 0; iter < 8; iter++) {
            const acc = centers.map(() => [0, 0, 0, 0]);
            for (let p = 0; p < n; p += stride) {
                const i = p * 4, k = nearest(centers, d[i], d[i + 1], d[i + 2]);
                acc[k][0] += d[i]; acc[k][1] += d[i + 1]; acc[k][2] += d[i + 2]; acc[k][3]++;
            }
            acc.forEach((a, k) => { if (a[3]) centers[k] = [a[0] / a[3], a[1] / a[3], a[2] / a[3]]; });
        }
        const label = new Uint8Array(n);
        for (let p = 0; p < n; p++) { const i = p * 4; label[p] = nearest(centers, d[i], d[i + 1], d[i + 2]); }
        // Tidy the labels so anti-aliased edges don't leave crumbs: each pixel takes its 3×3 majority
        const tidy = new Uint8Array(n);
        const votes = new Uint16Array(colors);
        for (let y = 0; y < h; y++)
            for (let x = 0; x < w; x++) {
                votes.fill(0);
                for (let dy = -1; dy <= 1; dy++)
                    for (let dx = -1; dx <= 1; dx++) {
                        const xx = Math.min(w - 1, Math.max(0, x + dx)), yy = Math.min(h - 1, Math.max(0, y + dy));
                        votes[label[yy * w + xx]]++;
                    }
                let best = label[y * w + x];
                for (let k = 0; k < colors; k++) if (votes[k] > votes[best]) best = k;
                tidy[y * w + x] = best;
            }
        // A pixel is a line where its color differs from the one to the right or below
        let m = new Uint8Array(n);
        for (let y = 0; y < h; y++)
            for (let x = 0; x < w; x++) {
                const p = y * w + x, c = tidy[p];
                if ((x + 1 < w && tidy[p + 1] !== c) || (y + 1 < h && tidy[p + w] !== c)) m[p] = 1;
            }
        // Very dark colors (black outlines in the art) stay ink too
        for (let p = 0; p < n; p++) { const c = centers[tidy[p]]; if (0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2] < 60) m[p] = 1; }
        return repeat(m, w, h, true, Math.max(0, weight - 1));
    }

    function nearest(centers, r, g, b) {
        let best = 0, bestD = Infinity;
        for (let k = 0; k < centers.length; k++) {
            const c = centers[k], dd = (c[0] - r) ** 2 + (c[1] - g) ** 2 + (c[2] - b) ** 2;
            if (dd < bestD) { bestD = dd; best = k; }
        }
        return best;
    }

    // Removes ink blobs smaller than minArea pixels (specks, scanner dust)
    function despeckle(m, w, h, minArea) {
        if (minArea <= 1) return m;
        const seen = new Uint8Array(m.length), stack = [], blob = [];
        for (let s = 0; s < m.length; s++) {
            if (!m[s] || seen[s]) continue;
            blob.length = 0;
            stack.push(s);
            seen[s] = 1;
            while (stack.length) {
                const p = stack.pop();
                blob.push(p);
                const x = p % w, y = (p - x) / w;
                if (x > 0 && m[p - 1] && !seen[p - 1]) { seen[p - 1] = 1; stack.push(p - 1); }
                if (x < w - 1 && m[p + 1] && !seen[p + 1]) { seen[p + 1] = 1; stack.push(p + 1); }
                if (y > 0 && m[p - w] && !seen[p - w]) { seen[p - w] = 1; stack.push(p - w); }
                if (y < h - 1 && m[p + w] && !seen[p + w]) { seen[p + w] = 1; stack.push(p + w); }
            }
            if (blob.length < minArea) for (const p of blob) m[p] = 0;
        }
        return m;
    }

    // The drawing's silhouette: every pixel not reachable from the image border through paper.
    // It's drawn white behind the lines, so on a colored cover the picture shows as white with black lines (like mandalas do).
    function silhouette(m, w, h) {
        const outside = new Uint8Array(m.length), stack = [];
        const seed = p => { if (!m[p] && !outside[p]) { outside[p] = 1; stack.push(p); } };
        for (let x = 0; x < w; x++) { seed(x); seed((h - 1) * w + x); }
        for (let y = 0; y < h; y++) { seed(y * w); seed(y * w + w - 1); }
        while (stack.length) {
            const p = stack.pop(), x = p % w;
            if (x > 0) seed(p - 1);
            if (x < w - 1) seed(p + 1);
            if (p >= w) seed(p - w);
            if (p + w < m.length) seed(p + w);
        }
        const inside = new Uint8Array(m.length);
        for (let p = 0; p < m.length; p++) inside[p] = outside[p] ? 0 : 1;
        return inside;
    }

    // Traces the 1s of a mask (already cropped to cw × ch) into SVG path data
    function tracePaths(mask, cw, ch, smooth, speck) {
        const data = new Uint8ClampedArray(cw * ch * 4);
        for (let p = 0, i = 0; p < mask.length; p++, i += 4) {
            const v = mask[p] ? 0 : 255;
            data[i] = data[i + 1] = data[i + 2] = v;
            data[i + 3] = 255;
        }
        const td = ImageTracer.imagedataToTracedata({ width: cw, height: ch, data }, {
            pal: [{ r: 0, g: 0, b: 0, a: 255 }, { r: 255, g: 255, b: 255, a: 255 }],
            colorquantcycles: 1, ltres: smooth, qtres: smooth, pathomit: speck,
            rightangleenhance: false, blurradius: 0, roundcoords: 1, strokewidth: 0, viewbox: true,
        });
        // Keep only the black layer
        const black = { layers: [td.layers[0]], palette: [td.palette[0]], width: td.width, height: td.height };
        const raw = ImageTracer.getsvgstring(black, { strokewidth: 0, roundcoords: 1, viewbox: true });
        const paths = [];
        raw.replace(/\sd="([^"]+)"/g, (_, dd) => { paths.push(dd); return ""; });
        return paths.join(" ");
    }

    function bounds(m, w, h) {
        let x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (let y = 0; y < h; y++)
            for (let x = 0; x < w; x++)
                if (m[y * w + x]) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        return x1 < 0 ? null : { x0, y0, x1, y1 };
    }

    // ---------- Trace ----------

    // s: { mode: "lines" | "outline" | "color", threshold (-1 = auto), invert, weight, thicken, colors, speck, smooth }
    // Returns { svg, width, height, ink, threshold } or { error }
    function trace(s) {
        if (!source) return { error: "Open an image first." };
        const { canvas } = source;
        const w = canvas.width, h = canvas.height;
        const d = canvas.getContext("2d").getImageData(0, 0, w, h).data;
        const threshold = s.threshold >= 0 ? s.threshold : autoThreshold();

        let m;
        if (s.mode === "color") m = colorEdges(d, w, h, Math.max(2, Math.min(16, s.colors)), Math.max(1, s.weight));
        else {
            m = thresholdMask(d, w, h, threshold, !!s.invert);
            if (s.mode === "outline") m = outlineOf(m, w, h, Math.max(1, s.weight));
        }
        if (s.thicken > 0) m = repeat(m, w, h, true, s.thicken);
        m = despeckle(m, w, h, Math.round(s.speck * s.speck));

        const b = bounds(m, w, h);
        if (!b) return { error: "Nothing left to trace. Try a different threshold or mode.", threshold };
        // Crop to the drawing with a small margin, so the page fills nicely
        const pad = 4;
        const cx0 = Math.max(0, b.x0 - pad), cy0 = Math.max(0, b.y0 - pad);
        const cw = Math.min(w, b.x1 + pad + 1) - cx0, ch = Math.min(h, b.y1 + pad + 1) - cy0;
        // Lines thinner than KDP's 0.3 mm minimum (at a 200 mm drawing) vanish when the ink is opened
        // (shrunk then grown back) by half that width; report how much of the ink that is
        const mmPer = 200 / Math.max(cw, ch);
        const r = Math.round(0.3 / mmPer / 2);
        let thin = 0;
        if (r >= 1) {
            const opened = repeat(repeat(m, w, h, false, r), w, h, true, r);
            let lost = 0, total = 0;
            for (let p = 0; p < m.length; p++) if (m[p]) { total++; if (!opened[p]) lost++; }
            thin = total ? lost / total : 0;
        }
        const crop = new Uint8Array(cw * ch);
        let ink = 0;
        for (let y = 0; y < ch; y++)
            for (let x = 0; x < cw; x++) {
                const on = m[(y + cy0) * w + (x + cx0)];
                crop[y * cw + x] = on;
                ink += on;
            }

        const smooth = Math.max(0.1, s.smooth), speck = Math.max(0, Math.round(s.speck));
        const lines = tracePaths(crop, cw, ch, smooth, speck);
        const paper = tracePaths(silhouette(crop, cw, ch), cw, ch, smooth, speck);

        // Physical size: long side 200 mm, like a 200 mm mandala, so line checks and export treat them the same
        const wmm = (cw * mmPer).toFixed(3), hmm = (ch * mmPer).toFixed(3);
        const svg = `<?xml version="1.0" encoding="UTF-8"?>\n` +
            `<svg xmlns="http://www.w3.org/2000/svg" width="${wmm}mm" height="${hmm}mm" viewBox="0 0 ${cw} ${ch}">\n` +
            `  <rect x="0" y="0" width="${cw}" height="${ch}" fill="#FFFFFF"/>\n` +
            (paper ? `  <path fill="#FFFFFF" fill-rule="nonzero" d="${paper}"/>\n` : "") +
            `  <path fill="#000000" fill-rule="nonzero" d="${lines}"/>\n</svg>\n`;
        return { svg, width: cw, height: ch, ink: ink / (cw * ch), threshold, mmPerPixel: mmPer, thin };
    }

    function hasSource() { return !!source; }

    return { load, pick, watchDrop, trace, autoThreshold, hasSource };
})();
