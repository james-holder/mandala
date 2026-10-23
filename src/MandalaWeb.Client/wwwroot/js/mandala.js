// Browser helpers for Mandala Maker: downloads, opening files, storage and printing.
window.mandala = {
    // Save text as a file the browser downloads
    download(fileName, mime, text) {
        const blob = new Blob([text], { type: mime });
        const url = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    },

    // Ask for a .mandala file and return its text (or null if cancelled)
    openText(accept) {
        return new Promise(resolve => {
            const input = document.createElement("input");
            input.type = "file";
            input.accept = accept;
            input.onchange = async () => {
                const file = input.files && input.files[0];
                resolve(file ? { name: file.name, text: await file.text() } : null);
            };
            input.oncancel = () => resolve(null);
            input.click();
        });
    },

    // Ask for several files and return their names and text
    openTexts(accept) {
        return new Promise(resolve => {
            const input = document.createElement("input");
            input.type = "file";
            input.accept = accept;
            input.multiple = true;
            input.onchange = async () => {
                const files = Array.from(input.files || []);
                resolve(await Promise.all(files.map(async f => ({ name: f.name, text: await f.text() }))));
            };
            input.oncancel = () => resolve([]);
            input.click();
        });
    },

    // Real PDF files from SVG coloring pages (vector, so lines stay crisp at any size).
    // pages: [{ name, svg }]. mode "single" = one PDF with every page; "separate" = one PDF per design.
    // preview = true returns a link to the finished PDF (to show on screen) instead of downloading it
    async exportPdf(pages, fileName, paper, mode, blankBacks, preview) {
        if (preview) mode = "single"; // preview everything in one document
        const { jsPDF } = window.jspdf;
        const sizes = { letter: [215.9, 279.4], legal: [215.9, 355.6], a4: [210, 297], a5: [148, 210] };
        const [pw, ph] = sizes[(paper || "letter").toLowerCase()] || sizes.letter;
        const margin = 12.7, side = Math.min(pw, ph) - 2 * margin;

        // svg2pdf reads styles from the live page, so render each SVG in an off-screen holder
        const holder = document.createElement("div");
        holder.style.cssText = "position:fixed;left:-10000px;top:0;width:800px;height:800px;overflow:hidden";
        document.body.appendChild(holder);

        const newDoc = () => new jsPDF({ unit: "mm", format: [pw, ph], orientation: "portrait", compress: true });
        async function drawPage(pdf, svgText) {
            holder.innerHTML = svgText.replace(/^<\?xml[^>]*>/, "");
            const el = holder.querySelector("svg");
            mandala.enforceMinLine(el, side, 0.35);
            await pdf.svg(el, { x: (pw - side) / 2, y: (ph - side) / 2, width: side, height: side });
        }

        try {
            if (mode === "separate") {
                for (let i = 0; i < pages.length; i++) {
                    const pdf = newDoc();
                    await drawPage(pdf, pages[i].svg);
                    const n = String(i + 1).padStart(3, "0");
                    pdf.save(`${fileName}_${n}.pdf`);
                    await new Promise(r => setTimeout(r, 400)); // give the browser a moment between downloads
                }
            } else {
                const pdf = newDoc();
                for (let i = 0; i < pages.length; i++) {
                    if (i > 0) pdf.addPage([pw, ph], "portrait");
                    await drawPage(pdf, pages[i].svg);
                    if (blankBacks) pdf.addPage([pw, ph], "portrait");
                }
                if (preview) return mandala.previewUrl(pdf);
                pdf.save(`${fileName}.pdf`);
            }
        } finally {
            holder.remove();
        }
        return null;
    },

    // Batch zoom view: the picture point (as 0–1 fractions) at the middle of the view, or under a click,
    // and scrolling so a given point sits in the middle after the picture changes size
    zoomCenter(view) {
        const v = view.getBoundingClientRect();
        return mandala.zoomPoint(view, v.left + view.clientWidth / 2, v.top + view.clientHeight / 2);
    },
    zoomPoint(view, clientX, clientY) {
        const r = view.querySelector("img").getBoundingClientRect();
        return [(clientX - r.left) / r.width, (clientY - r.top) / r.height];
    },
    zoomTo(view, fx, fy) {
        const img = view.querySelector("img");
        view.scrollLeft = img.offsetLeft + fx * img.offsetWidth - view.clientWidth / 2;
        view.scrollTop = img.offsetTop + fy * img.offsetHeight - view.clientHeight / 2;
    },

    // A link to the PDF for an on-screen preview. The previous preview's link is released first.
    previewUrl(pdf) {
        if (mandala._lastPreview) URL.revokeObjectURL(mandala._lastPreview);
        mandala._lastPreview = URL.createObjectURL(pdf.output("blob"));
        return mandala._lastPreview + "#view=Fit";
    },

    // Makes sure no line prints thinner than minLineMm when the SVG is drawn sideMm wide.
    // KDP's minimum is 0.3 mm; thinner lines can vanish in print.
    enforceMinLine(svgEl, sideMm, minLineMm) {
        const vb = svgEl.viewBox && svgEl.viewBox.baseVal;
        const designWidth = vb && vb.width ? vb.width : sideMm;
        const scale = sideMm / designWidth;
        const minUser = minLineMm / scale;
        svgEl.querySelectorAll("[stroke-width]").forEach(el => {
            const w = parseFloat(el.getAttribute("stroke-width"));
            if (!(w >= minUser)) el.setAttribute("stroke-width", String(minUser));
        });
    },

    // Text rendered as a 300 DPI image, so the PDF needs no fonts (KDP rejects fonts that aren't embedded)
    textPageImage(wIn, hIn, lines) {
        const dpi = 300, canvas = document.createElement("canvas");
        canvas.width = Math.round(wIn * dpi);
        canvas.height = Math.round(hIn * dpi);
        const ctx = canvas.getContext("2d");
        ctx.fillStyle = "#fff";
        ctx.fillRect(0, 0, canvas.width, canvas.height);
        ctx.fillStyle = "#000";
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        for (const l of lines) {
            ctx.font = `${l.bold ? "bold " : ""}${Math.round(l.pt * dpi / 72)}px ${l.font || "Georgia, 'Times New Roman', serif"}`;
            // Wrap long lines to the page width
            const maxW = canvas.width * 0.8, words = l.text.split(/\s+/), rows = [];
            let row = "";
            for (const w of words) {
                const test = row ? row + " " + w : w;
                if (ctx.measureText(test).width > maxW && row) { rows.push(row); row = w; } else row = test;
            }
            if (row) rows.push(row);
            const lineH = l.pt * dpi / 72 * 1.25;
            rows.forEach((r, i) => ctx.fillText(r, canvas.width / 2, l.y * canvas.height + (i - (rows.length - 1) / 2) * lineH));
            if (l.underline) {
                const y = l.y * canvas.height + lineH * 1.4;
                ctx.lineWidth = dpi * 0.012;
                ctx.beginPath(); ctx.moveTo(canvas.width * 0.2, y); ctx.lineTo(canvas.width * 0.8, y); ctx.stroke();
            }
        }
        return canvas.toDataURL("image/png");
    },

    // A KDP paperback interior: trim-size pages, no bleed, gutter margin from the page count,
    // designs on right-hand pages, optional blank backs, title and "belongs to" pages, minimum line weight.
    // pages: [{ name, svg }]. opts: { trimW, trimH (inches), title, subtitle, author, belongsTo, blankBacks, minLineMm }
    async exportKdp(pages, fileName, opts) {
        const { jsPDF } = window.jspdf;
        const mm = 25.4, W = opts.trimW * mm, H = opts.trimH * mm;

        // Lay out the page sequence first, because the gutter depends on the final page count
        const seq = [];
        if (opts.title) { seq.push({ kind: "title" }); seq.push({ kind: "blank" }); }
        if (opts.belongsTo) { seq.push({ kind: "belongs" }); seq.push({ kind: "blank" }); }
        pages.forEach((p, i) => {
            seq.push({ kind: "design", svg: p.svg });
            if (opts.blankBacks && i < pages.length - 1) seq.push({ kind: "blank" });
        });
        if (opts.blankBacks) seq.push({ kind: "blank" });
        if (seq.length % 2 === 1) seq.push({ kind: "blank" }); // KDP counts pages in pairs

        const n = seq.length;
        const gutterIn = n <= 150 ? 0.375 : n <= 300 ? 0.5 : n <= 500 ? 0.625 : n <= 700 ? 0.75 : 0.875;
        // KDP's gutter is a minimum; coloring near the binding is awkward, so keep art an extra 3/8" away from the spine
        const gutter = (gutterIn + 0.375) * mm, outside = 0.5 * mm, topBottom = 0.5 * mm;

        const holder = document.createElement("div");
        holder.style.cssText = "position:fixed;left:-10000px;top:0;width:800px;height:800px;overflow:hidden";
        document.body.appendChild(holder);
        const pdf = new jsPDF({ unit: "mm", format: [W, H], orientation: H >= W ? "portrait" : "landscape", compress: true });
        const textImage = lines => mandala.textPageImage(opts.trimW, opts.trimH, lines);

        try {
            for (let i = 0; i < n; i++) {
                if (i > 0) pdf.addPage([W, H], H >= W ? "portrait" : "landscape");
                const page = seq[i], rightHand = i % 2 === 0; // page 1 is a right-hand page
                const left = rightHand ? gutter : outside, right = rightHand ? outside : gutter;
                if (page.kind === "title") {
                    const lines = [{ text: opts.title, pt: 34, bold: true, y: 0.36 }];
                    if (opts.subtitle) lines.push({ text: opts.subtitle, pt: 16, y: 0.47 });
                    if (opts.author) lines.push({ text: opts.author, pt: 14, y: 0.72 });
                    pdf.addImage(textImage(lines), "PNG", 0, 0, W, H, undefined, "FAST");
                } else if (page.kind === "belongs") {
                    pdf.addImage(textImage([{ text: "This book belongs to", pt: 24, y: 0.42, underline: true }]), "PNG", 0, 0, W, H, undefined, "FAST");
                } else if (page.kind === "design") {
                    const areaW = W - left - right, areaH = H - 2 * topBottom, side = Math.min(areaW, areaH);
                    holder.innerHTML = page.svg.replace(/^<\?xml[^>]*>/, "");
                    const el = holder.querySelector("svg");
                    mandala.enforceMinLine(el, side, opts.minLineMm);
                    await pdf.svg(el, { x: left + (areaW - side) / 2, y: topBottom + (areaH - side) / 2, width: side, height: side });
                }
            }
            if (opts.preview) return { pages: n, gutterIn, url: mandala.previewUrl(pdf) };
            pdf.save(`${fileName}.pdf`);
        } finally {
            holder.remove();
        }
        return { pages: n, gutterIn, url: null };
    },

    confirm(message) {
        return window.confirm(message);
    },

    load(key) {
        try { return localStorage.getItem(key); } catch { return null; }
    },

    save(key, value) {
        try { localStorage.setItem(key, value); } catch { /* storage full or blocked: settings just aren't remembered */ }
    },

    // Print one or more SVG coloring pages, one per sheet, using the browser's print dialog ("Save as PDF" makes a PDF)
    printPages(pagesSvg, paper, blankBacks) {
        const frame = document.createElement("iframe");
        frame.style.position = "fixed";
        frame.style.right = "0";
        frame.style.bottom = "0";
        frame.style.width = "0";
        frame.style.height = "0";
        frame.style.border = "0";
        document.body.appendChild(frame);
        const pages = pagesSvg.map(svg =>
            `<section class="page"><div class="art">${svg.replace(/^<\?xml[^>]*>/, "")}</div></section>` +
            (blankBacks ? `<section class="page"></section>` : "")).join("");
        const doc = frame.contentDocument;
        doc.open();
        doc.write(`<!doctype html><html><head><meta charset="utf-8"><title>Mandala coloring pages</title><style>
            @page { size: ${paper}; margin: 0.5in; }
            html, body { margin: 0; background: white; }
            .page { height: calc(100vh - 1px); display: flex; align-items: center; justify-content: center; page-break-after: always; break-after: page; }
            .page:last-child { page-break-after: auto; break-after: auto; }
            .art { width: 100%; height: 100%; display: flex; align-items: center; justify-content: center; }
            .art svg { width: auto; height: auto; max-width: 100%; max-height: 100%; aspect-ratio: 1 / 1; }
        </style></head><body>${pages}</body></html>`);
        doc.close();
        frame.contentWindow.focus();
        setTimeout(() => {
            frame.contentWindow.print();
            setTimeout(() => frame.remove(), 2000);
        }, 300);
    },
};

// Firefox only starts a drag when some data is attached, which Blazor's drag events don't do
document.addEventListener("dragstart", e => {
    if (e.target && e.target.closest && e.target.closest(".card[draggable=true]") && e.dataTransfer) {
        e.dataTransfer.setData("text/plain", "");
        e.dataTransfer.effectAllowed = "move";
    }
});
