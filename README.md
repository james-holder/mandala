# Mandala Maker (web)

The web version of Mandala Maker, served at **mandala.candaceholder.com**. It's a Blazor WebAssembly app, so it runs
entirely in the browser: designs are generated on your own computer, and nothing is stored on the server.

## Projects

| Project | What it is |
|---|---|
| `src/MandalaCore` | The shapes, generators and SVG output, ported from the desktop app (`../MandalaMaker`). WPF's geometry is replaced by `Geom/` (a path reader plus Clipper2 for merging shapes). |
| `src/MandalaWeb.Client` | The web page (Blazor WebAssembly). |
| `src/MandalaWeb.Server` | A tiny ASP.NET Core host that serves the files on Fly.io. |

## Where things are saved

- **Shape preferences** and the file-name counter are kept in the browser (local storage), per browser.
- **Save** downloads `Mandala_NNN.mandala` and `Mandala_NNN.svg` to your Downloads folder. The `.mandala` files open in
  both the web and desktop apps.
- **Print book** lets you pick several `.mandala` files and prints them one per page; choose "Save as PDF" in the print
  dialog for a PDF.

## Making pages

- **Detail** (Simple, Medium, Intricate) sets how busy new designs are, for both ✦ Mandala and ✏ Line art and on the
  Batch page. Simple pages have fewer, bigger spaces and bolder lines.
- Coloring pages are trimmed to the drawing, so a design fills the page inside the margins. Exports from the gallery
  redraw each design from its saved data, so older designs get this too.
- **👁 Preview** (design page, gallery export and KDP cover) shows the finished PDF in a pop-up before you download it.

## Tracing images

- **🖍 Trace image** turns clip art or a drawing into a black-and-white coloring page (vector), using the public-domain
  imagetracer.js (`wwwroot/lib`). Modes: line drawing, solid shapes → outlines, color clip art → outlines.
- Traced designs are saved with kind `traced`: the stored SVG is the page, and the JSON keeps the working image and
  settings so it can be re-traced. They go in folders and books like mandalas; export fits any shape inside the margins.

## Page layout

- **📄 Page** lays out designs (mandalas and traced images) and text on a book page: drag to move, the square handle
  resizes, the round one rotates, and things snap to the page center. Pages are saved with kind `page`.
- The saved SVG is only the area inside the margins (PageSizes: 0.5 in, 0.75 in on the spine side), so book export
  fits it into each book's real margins. Text is drawn as 600 DPI black-and-white images, so PDFs need no fonts.
- Fonts are bundled in `wwwroot/fonts` (SIL Open Font License, see LICENSE.txt there).

## Running locally

```bash
dotnet run --project src/MandalaWeb.Server
```

## Deploying

```bash
fly deploy
```

The app is `candaceholder-mandala` on Fly.io. The subdomain needs a DNS record at GoDaddy (where candaceholder.com's
DNS lives): a CNAME from `mandala` to `candaceholder-mandala.fly.dev`, plus the certificate from
`fly certs add mandala.candaceholder.com -a candaceholder-mandala`.

## Keeping in step with the desktop app

`MandalaCore` started as a copy of the desktop app's `Model/` and `Shapes/` code. New shapes added to the desktop app
need copying here too (or both apps can later share this library).
