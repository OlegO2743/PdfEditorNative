// Engine/Render/GdiRenderer.cs
// PDF content stream interpreter → System.Drawing.Bitmap
// Now with full font encoding (Differences, ToUnicode, WinAnsi, MacRoman)

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text;
using PdfEditorNative.Engine;

namespace PdfEditorNative.Engine.Render;

public sealed class GdiRenderer
{
    private readonly PdfParser _pdf;

    public GdiRenderer(PdfParser pdf) => _pdf = pdf;

    // ── Public entry ──────────────────────────────────────────────
    public Bitmap RenderPage(int pageIndex, float zoom = 1.0f)
    {
        var pageDict = _pdf.GetPageDict(pageIndex);
        var mediaBox = GetBox(pageDict);
        float pageW = (float)(mediaBox[2] - mediaBox[0]);
        float pageH = (float)(mediaBox[3] - mediaBox[1]);

        int bmpW = Math.Max(1, (int)(pageW * zoom));
        int bmpH = Math.Max(1, (int)(pageH * zoom));

        var bmp = new Bitmap(bmpW, bmpH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        // Clip to bitmap bounds so paths outside page (e.g. y<0 in PDF) don't draw
        g.SetClip(new Rectangle(0, 0, bmpW, bmpH));
        g.SmoothingMode    = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // PDF→screen: flip Y, apply zoom
        var baseMatrix = PdfMatrix.From(zoom, 0, 0, -zoom,
            -mediaBox[0] * zoom, (pageH + mediaBox[1]) * zoom);

        var res = _pdf.GetResources(pageDict);
        var content = _pdf.GetContentBytes(pageDict);
        var ctx = new RenderContext(g, baseMatrix, zoom, res, _pdf);
        Interpret(content, ctx);
        return bmp;
    }

    // ═══════════════════════════════════════════════════════════════
    //  CONTENT STREAM INTERPRETER
    // ═══════════════════════════════════════════════════════════════
    private void Interpret(byte[] stream, RenderContext ctx)
    {
        var lex      = new PdfLexer(stream);
        var operands = new List<object>();
        while (!lex.AtEnd)
        {
            lex.SkipWS(); if (lex.AtEnd) break;
            var tok = lex.NextToken();
            if (tok == null) break;

            if (tok is string op)
            {
                if (op == "[")
                {
                    // Collect array items until "]" as a single PdfArray operand
                    var arr = new PdfArray();
                    while (!lex.AtEnd)
                    {
                        lex.SkipWS();
                        if (lex.PeekByte() == (byte)']') { lex.SkipByte(); break; }
                        var item = _pdf.ParseObject(lex);
                        if (item != null) arr.Items.Add(item);
                        else break;
                    }
                    operands.Add(arr);
                }
                else
                {
                    Execute(op, operands, ctx, lex);
                    operands.Clear();
                }
            }
            else
            {
                operands.Add(tok);
            }
        }
    }

    private void Execute(string op, List<object> ops, RenderContext ctx, PdfLexer lex)
    {
        var s = ctx.State;
        switch (op)
        {
            // ── Graphics state ────────────────────────────────────
            case "q":  ctx.Push(); break;
            case "Q":  ctx.Pop();  break;
            case "cm":
                var m = Mat(ops);
                ctx.State.CTM = m.Mul(ctx.State.CTM); break;
            case "w":  s.LineWidth = D(ops,0); break;
            case "J":  s.LineCap   = I(ops,0); break;
            case "j":  s.LineJoin  = I(ops,0); break;
            case "d":
                if (ops.Count >= 1 && ops[0] is PdfArray da)
                    s.DashArray = da.Items.Select(x => (float)((PdfObj)x).AsDouble()).ToArray();
                if (ops.Count >= 2) s.DashPhase = (float)D(ops,1);
                break;
            case "gs": ApplyExtGState(S(ops,0), ctx); break;

            // ── Color ─────────────────────────────────────────────
            case "G":  s.StrokeColor = Gray(D(ops,0)); break;
            case "g":  s.FillColor   = Gray(D(ops,0)); break;
            case "RG": s.StrokeColor = Rgb(D(ops,0),D(ops,1),D(ops,2)); break;
            case "rg": s.FillColor   = Rgb(D(ops,0),D(ops,1),D(ops,2)); break;
            case "K":  s.StrokeColor = Cmyk(D(ops,0),D(ops,1),D(ops,2),D(ops,3)); break;
            case "k":  s.FillColor   = Cmyk(D(ops,0),D(ops,1),D(ops,2),D(ops,3)); break;
            case "CS": case "cs": break;
            // Set color in current color space: use number of numeric operands
            case "SC": case "SCN": s.StrokeColor = ColorFromOps(ops); break;
            case "sc": case "scn": s.FillColor   = ColorFromOps(ops); break;

            // ── Paths ─────────────────────────────────────────────
            case "m":  ctx.NewFigure(); ctx.MoveTo(D(ops,0),D(ops,1)); break;
            case "l":  ctx.LineTo(D(ops,0),D(ops,1)); break;
            case "c":  ctx.CurveTo(D(ops,0),D(ops,1),D(ops,2),D(ops,3),D(ops,4),D(ops,5)); break;
            case "v":  ctx.CurveTo(ctx.CurX,ctx.CurY,D(ops,0),D(ops,1),D(ops,2),D(ops,3)); break;
            case "y":  ctx.CurveTo(D(ops,0),D(ops,1),D(ops,2),D(ops,3),D(ops,2),D(ops,3)); break;
            case "h":  ctx.Path.CloseFigure(); break;
            case "re": ctx.AddRect(D(ops,0),D(ops,1),D(ops,2),D(ops,3)); break;

            case "S":  ctx.Stroke(); break;
            case "s":  ctx.Path.CloseFigure(); ctx.Stroke(); break;
            case "f": case "F": ctx.Fill(FillMode.Winding); break;
            case "f*": ctx.Fill(FillMode.Alternate); break;
            case "B":  ctx.FillStroke(FillMode.Winding); break;
            case "B*": ctx.FillStroke(FillMode.Alternate); break;
            case "b":  ctx.Path.CloseFigure(); ctx.FillStroke(FillMode.Winding); break;
            case "b*": ctx.Path.CloseFigure(); ctx.FillStroke(FillMode.Alternate); break;
            case "n":  ctx.NewPath(); break;
            case "W":  ctx.G.SetClip(ctx.Path, CombineMode.Intersect); ctx.NewPath(); break;
            case "W*": ctx.G.SetClip(ctx.Path, CombineMode.Intersect); ctx.NewPath(); break;

            // ── Text ──────────────────────────────────────────────
            case "BT":
                s.TextMatrix = s.TextLineMatrix = PdfMatrix.Identity; break;
            case "ET": break;
            case "Tf":
                s.FontName = S(ops,0); s.FontSize = D(ops,1);
                ctx.CurrentFont = ResolveFont(s.FontName, ctx);
                break;
            case "Td":
                s.TextLineMatrix = PdfMatrix.From(1,0,0,1,D(ops,0),D(ops,1)).Mul(s.TextLineMatrix);
                s.TextMatrix = s.TextLineMatrix; break;
            case "TD":
                s.Leading = -D(ops,1);
                s.TextLineMatrix = PdfMatrix.From(1,0,0,1,D(ops,0),D(ops,1)).Mul(s.TextLineMatrix);
                s.TextMatrix = s.TextLineMatrix; break;
            case "Tm":
                s.TextMatrix = s.TextLineMatrix = PdfMatrix.From(D(ops,0),D(ops,1),D(ops,2),D(ops,3),D(ops,4),D(ops,5)); break;
            case "T*":
                s.TextLineMatrix = PdfMatrix.From(1,0,0,1,0,-s.Leading).Mul(s.TextLineMatrix);
                s.TextMatrix = s.TextLineMatrix; break;
            case "Tc": s.CharSpacing  = D(ops,0); break;
            case "Tw": s.WordSpacing  = D(ops,0); break;
            case "Tz": s.HorizScaling = D(ops,0); break;
            case "TL": s.Leading      = D(ops,0); break;
            case "Tr": s.TextRenderMode = I(ops,0); break;
            case "Ts": s.TextRise     = D(ops,0); break;
            case "Tj": if (ops.Count>0 && ops[0] is PdfStr tj) ShowText(tj.Bytes, ctx); break;
            case "TJ": if (ops.Count>0 && ops[0] is PdfArray ta) ShowTextArray(ta, ctx); break;
            case "'":
                s.TextLineMatrix = PdfMatrix.From(1,0,0,1,0,-s.Leading).Mul(s.TextLineMatrix);
                s.TextMatrix = s.TextLineMatrix;
                if (ops.Count>0 && ops[0] is PdfStr sq) ShowText(sq.Bytes, ctx); break;
            case "\"":
                s.WordSpacing = D(ops,0); s.CharSpacing = D(ops,1);
                s.TextLineMatrix = PdfMatrix.From(1,0,0,1,0,-s.Leading).Mul(s.TextLineMatrix);
                s.TextMatrix = s.TextLineMatrix;
                if (ops.Count>2 && ops[2] is PdfStr sdq) ShowText(sdq.Bytes, ctx); break;

            // ── XObjects / inline images ───────────────────────────
            case "Do": PaintXObject(S(ops,0), ctx); break;
            case "BI": ParseInlineImage(lex, ctx); break;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  TEXT
    // ═══════════════════════════════════════════════════════════════

    // GDI advance width cache: "Family:Style:CharCode" -> width in 1/1000 units
    private readonly Dictionary<string, double> _advanceCache = new();
    // Shared measurement bitmap (created once)
    private System.Drawing.Bitmap? _measureBmp;
    private System.Drawing.Graphics? _measureG;

    private System.Drawing.Graphics GetMeasureG()
    {
        if (_measureG != null) return _measureG;
        _measureBmp = new System.Drawing.Bitmap(1, 1);
        _measureG   = System.Drawing.Graphics.FromImage(_measureBmp);
        _measureG.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        return _measureG;
    }

    /// <summary>
    /// Returns advance width of <paramref name="ch"/> in 1/1000 of font-size units
    /// using the actual GDI substituted font. Cached per (family,style,char).
    /// </summary>
    private double MeasureGdiAdvance(char ch, Font font)
    {
        string key = $"{font.FontFamily.Name}:{(int)font.Style}:{(int)ch}";
        if (_advanceCache.TryGetValue(key, out double cached)) return cached;

        double result;
        try
        {
            var g  = GetMeasureG();
            // Use size 1000px -> result is already in 1/1000 units
            using var mf = new Font(font.FontFamily, 1000f, font.Style, GraphicsUnit.Pixel);
            var sf = System.Drawing.StringFormat.GenericTypographic;

            // Sandwich method: "X" + ch + "X" minus "XX"
            // This prevents trimming of leading/trailing spaces/thin chars.
            var szXcX = g.MeasureString("X" + ch + "X", mf, int.MaxValue, sf);
            var szXX  = g.MeasureString("XX",            mf, int.MaxValue, sf);
            result = Math.Max(0, szXcX.Width - szXX.Width);
        }
        catch
        {
            result = 500; // safe fallback
        }

        _advanceCache[key] = result;
        return result;
    }

    private void ShowText(byte[] bytes, RenderContext ctx)
    {
        var s  = ctx.State;
        var fi = ctx.CurrentFont;
        var font = BuildGdiFont(fi, s);

        bool isTwoByteFont = fi?.IsTwoByte ?? false;

        int i = 0;
        while (i < bytes.Length)
        {
            int code;
            if (isTwoByteFont && i + 1 < bytes.Length)
            {
                code = (bytes[i] << 8) | bytes[i + 1];
                i += 2;
            }
            else
            {
                code = bytes[i];
                i++;
            }

            // ── Native CFF path: bypass GDI DrawString, render outlines directly ──
            if (fi != null && fi.Cff != null)
            {
                int cid = code;
                int gid = fi.Cff.CidToGid.TryGetValue(cid, out int g) ? g : cid;

                double wCff = HasExplicitWidth(fi, code) ? GetCharWidth(fi, code)
                            : fi.Cff.GidWidths.TryGetValue(gid, out int gw) ? gw
                            : 500;

                if (s.TextRenderMode != 3)
                {
                    var glyphTmC = PdfMatrix.From(1,0,0,1,0,s.TextRise).Mul(s.TextMatrix);
                    var fullCtmC = glyphTmC.Mul(s.CTM);
                    DrawCffGlyph(fi, gid, fullCtmC, ctx);
                }

                double txC = (wCff * s.FontSize / 1000.0 + s.CharSpacing
                             + (code == 32 ? s.WordSpacing : 0)) * (s.HorizScaling / 100.0);
                s.TextMatrix = PdfMatrix.From(1,0,0,1,txC,0).Mul(s.TextMatrix);
                continue;
            }

            char ch = fi != null && fi.CodeMap.TryGetValue(code, out char mapped) ? mapped
                    : fi != null && !fi.IsTwoByte && code < 256 ? fi.ByteMap[code]
                    : fi != null && fi.IsTwoByte && code < 0x0100 && code >= 0x0020 ? (char)code
                    : fi != null && code < 256 ? fi.ByteMap[code]
                    : (char)code;

            bool drawable = ch >= ' ' || ch == '\t';

            // Compute PDF advance width (used for text matrix advance)
            double w;
            if (HasExplicitWidth(fi, code))
                w = GetCharWidth(fi, code);
            else if (drawable)
                w = MeasureGdiAdvance(ch, font);
            else
                w = GetCharWidth(fi, code);


            if (drawable && s.TextRenderMode != 3)
            {
                var glyphTm = PdfMatrix.From(1,0,0,1,0,s.TextRise).Mul(s.TextMatrix);
                var fullCtm = glyphTm.Mul(s.CTM);

                // Only shift glyph if substituted font is narrower than PDF advance
                // (centering). Never shift wider glyphs — that causes merging.
                double xOffset = 0;
                if (HasExplicitWidth(fi, code) && ch != ' ')
                {
                    double gdiW = MeasureGdiAdvance(ch, font);
                    double pdfW = w;
                    if (gdiW < pdfW) xOffset = (pdfW - gdiW) / 2.0;
                }

                DrawGlyph(ch, font, fullCtm, ctx, xOffset);
            }

            double tx = (w * s.FontSize / 1000.0 + s.CharSpacing
                        + (code == 32 ? s.WordSpacing : 0)) * (s.HorizScaling / 100.0);
            s.TextMatrix = PdfMatrix.From(1,0,0,1,tx,0).Mul(s.TextMatrix);
        }
    }

    private static double GetCharWidth(FontInfo? fi, int code)
    {
        if (fi == null) return 600;
        if (fi.WidthMap.TryGetValue(code, out double w)) return w;
        if (fi.IsTwoByte && fi.WidthMap.TryGetValue(-1, out double dw)) return dw;
        if (code < 256) return fi.Widths[code];
        return 1000;
    }

    /// <summary>True if the font has an EXPLICIT width for this code (not DW fallback).</summary>
    private static bool HasExplicitWidth(FontInfo? fi, int code)
    {
        if (fi == null) return false;
        if (fi.WidthMap.ContainsKey(code)) return true;
        // 1-byte fonts: explicit if Widths[] was set (differs from default 600)
        if (!fi.IsTwoByte && code < 256 && fi.Widths[code] != 600) return true;
        return false;
    }

    private void ShowTextArray(PdfArray arr, RenderContext ctx)
    {
        var s = ctx.State;
        foreach (var item in arr.Items)
        {
            if (item is PdfStr ps) ShowText(ps.Bytes, ctx);
            else
            {
                double shift = item.AsDouble() * s.FontSize / 1000.0 * (s.HorizScaling / 100.0);
                s.TextMatrix = PdfMatrix.From(1,0,0,1,-shift,0).Mul(s.TextMatrix);
            }
        }
    }

    private void DrawGlyph(char ch, Font font, PdfMatrix ctm, RenderContext ctx, double xOffset = 0, double glyphScaleX = 1.0)
    {
        var s = ctx.State;
        if (ch < ' ' && ch != '\t') return;
        if (ch == ' ') return;

        // Screen position = where CTM maps the origin (0,0)
        var (screenX, screenY) = (ctm.E, ctm.F);

        // Apply glyph centering offset.
        // xOffset is in 1/1000 font units. Convert to screen pixels:
        // screen_px = xOffset/1000 * fontSize * ctmScaleX
        if (Math.Abs(xOffset) > 0.01)
        {
            double ctmScaleX = Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B);
            double fontSize = ctx.State.FontSize;
            screenX += xOffset / 1000.0 * fontSize * ctmScaleX;
        }

        // Extract X scale and Y scale (with sign)
        double scaleX = Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B);
        double scaleY = Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D);
        double scale  = (scaleX + scaleY) * 0.5;

        // Determine if text is flipped (net Y-scale negative means upside down)
        // ctm.D carries the Y-column of the matrix (for unrotated text = net Y scale)
        bool flippedY = ctm.D > 0; // positive D in screen coords = text was flipped

        float screenFontSize = Math.Max(1f, (float)(font.Size * scale));
        float angleDeg = (float)(Math.Atan2(ctm.B, ctm.A) * 180.0 / Math.PI);

        var saved = ctx.G.Save();
        try
        {
            ctx.G.ResetTransform();
            ctx.G.TranslateTransform((float)screenX, (float)screenY);
            if (Math.Abs(angleDeg) > 0.1f)
                ctx.G.RotateTransform(-angleDeg);
            if (Math.Abs(glyphScaleX - 1.0) > 0.01)
                ctx.G.ScaleTransform((float)glyphScaleX, 1f);

            using var scaledFont = new Font(font.FontFamily, screenFontSize, font.Style, GraphicsUnit.Pixel);

            // Use font ascent so the PDF baseline (= screenY) aligns correctly.
            // PDF Y = baseline. GDI draws from top-left.
            // top = screenY - ascent  =>  baseline = screenY (correct)
            float yOff;
            if (flippedY)
            {
                yOff = 0f;
            }
            else
            {
                try
                {
                    int cellAscent = scaledFont.FontFamily.GetCellAscent(scaledFont.Style);
                    int emHeight   = scaledFont.FontFamily.GetEmHeight(scaledFont.Style);
                    yOff = -screenFontSize * cellAscent / emHeight;
                }
                catch { yOff = -screenFontSize * 0.8f; }
            }

            if (s.TextRenderMode == 0 || s.TextRenderMode == 2 || s.TextRenderMode == 4)
            {
                using var brush = new SolidBrush(s.EffectiveFillColor);
                ctx.G.DrawString(ch.ToString(), scaledFont, brush, 0f, yOff);
            }
            if (s.TextRenderMode == 1 || s.TextRenderMode == 2)
            {
                using var path = new GraphicsPath();
                using var pen  = new Pen(s.EffectiveStrokeColor, 0.5f);
                path.AddString(ch.ToString(), scaledFont.FontFamily, (int)scaledFont.Style,
                    screenFontSize, new PointF(0f, yOff), StringFormat.GenericDefault);
                ctx.G.DrawPath(pen, path);
            }
        }
        finally { ctx.G.Restore(saved); }
    }

    // Render a CFF glyph by interpreting its Type 2 charstring directly.
    // The outline is cached in FontInfo.CffPathCache (font-unit space, Y-up).
    // Drawing: set Graphics.Transform = scale(fontSize/UPM) × fullCtm, then FillPath.
    // The PDF CTM already carries the page Y-flip, so the Y-up path maps correctly.
    private void DrawCffGlyph(FontInfo fi, int gid, PdfMatrix fullCtm, RenderContext ctx)
    {
        if (fi.Cff == null) return;
        if (!fi.CffPathCache.TryGetValue(gid, out var path))
        {
            path = Type2Interpreter.BuildGlyphPath(fi.Cff, gid);
            fi.CffPathCache[gid] = path;
        }
        if (path == null) return;

        var s = ctx.State;
        double scale = s.FontSize / (double)fi.Cff.UnitsPerEm;

        var m = new Matrix(
            (float)(scale * fullCtm.A),
            (float)(scale * fullCtm.B),
            (float)(scale * fullCtm.C),
            (float)(scale * fullCtm.D),
            (float)fullCtm.E,
            (float)fullCtm.F);

        var saved = ctx.G.Save();
        try
        {
            ctx.G.Transform = m;
            if (s.TextRenderMode == 0 || s.TextRenderMode == 2 || s.TextRenderMode == 4 || s.TextRenderMode == 6)
            {
                using var brush = new SolidBrush(s.EffectiveFillColor);
                ctx.G.FillPath(brush, path);
            }
            if (s.TextRenderMode == 1 || s.TextRenderMode == 2 || s.TextRenderMode == 5 || s.TextRenderMode == 6)
            {
                // Pen width is in font units; 20 ≈ 0.02 em which gives a visually
                // reasonable outline at any zoom under our scaled transform.
                using var pen = new Pen(s.EffectiveStrokeColor, 20f);
                ctx.G.DrawPath(pen, path);
            }
        }
        finally { ctx.G.Restore(saved); }
    }

    private Font BuildGdiFont(FontInfo? fi, GfxState s)
    {
        // We return a font at the PDF font size (in points).
        // DrawGlyph will convert to actual screen pixels using the CTM scale.
        float sz = Math.Max(0.5f, (float)s.FontSize);
        if (fi?.GdiFont != null)
        {
            try { return new Font(fi.GdiFont.FontFamily, sz, fi.GdiFont.Style, GraphicsUnit.Point); }
            catch { }
        }
        return FontResolver.MapToGdi(s.FontName, sz);
    }

    // ── Font resolution with caching ──────────────────────────────
    private readonly Dictionary<string, FontInfo?> _fontCache = new();

    private FontInfo? ResolveFont(string resName, RenderContext ctx)
    {
        if (_fontCache.TryGetValue(resName, out var cached)) return cached;

        try
        {
            var fonts = _pdf.Resolve(ctx.Res.Get("Font")) as PdfDict;
            if (fonts == null) { _fontCache[resName] = null; return null; }

            var fontObj = _pdf.Resolve(fonts.Get(resName));
            if (fontObj is not PdfDict fd) { _fontCache[resName] = null; return null; }

            var fi = FontResolver.Build(fd, _pdf);
            _fontCache[resName] = fi;
            return fi;
        }
        catch
        {
            _fontCache[resName] = null;
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  XOBJECTS & IMAGES
    // ═══════════════════════════════════════════════════════════════
    private void PaintXObject(string name, RenderContext ctx)
    {
        var xobjs = _pdf.Resolve(ctx.Res.Get("XObject")) as PdfDict;
        if (xobjs == null) return;
        var xobj = _pdf.Resolve(xobjs.Get(name)) as PdfStream;
        if (xobj == null) return;

        string sub = _pdf.Resolve(xobj.Dict.Get("Subtype")).AsName();
        if (sub == "Image") PaintImage(xobj, ctx);
        else if (sub == "Form") PaintForm(xobj, ctx);
    }

    private void PaintImage(PdfStream stm, RenderContext ctx)
    {
        int w = _pdf.Resolve(stm.Dict.Get("Width")).AsInt();
        int h = _pdf.Resolve(stm.Dict.Get("Height")).AsInt();
        Image? img = null;
        try
        {
            string filter = stm.FilterName();
            if (filter == "JPXDecode")
            {
                img = Jpeg2000Decoder.Decode(stm.RawData);
                if (img == null)
                {
                    try { img = Image.FromStream(new MemoryStream(stm.RawData)); }
                    catch { img = WicDecoder.TryDecode(stm.RawData); }
                }
            }
            else if (filter == "DCTDecode")
            {
                try { img = Image.FromStream(new MemoryStream(stm.RawData)); }
                catch { img = WicDecoder.TryDecode(stm.RawData); }
            }
            else
            {
                byte[] dec  = stm.Decode();
                string cs   = GetCS(stm.Dict);
                bool   mask = _pdf.Resolve(stm.Dict.Get("ImageMask")) is PdfBool {Value:true};
                Color[]? palette = cs == "Indexed" ? GetIndexedPalette(stm.Dict) : null;
                img = RawToImage(dec, w, h, cs, mask, ctx.State.FillColor, palette);
            }
            if (img == null) return;

            // Apply SMask (soft mask) as alpha channel if present.
            var smaskObj = _pdf.Resolve(stm.Dict.Get("SMask")) as PdfStream;
            if (smaskObj != null) img = ApplySMask(img, smaskObj);

            var sv = ctx.G.Save();
            try
            {
                ctx.G.Transform = ctx.State.CTM.ToGdi();
                // PDF image: row 0 is top, unit square (0,0)-(1,1) where y=1 is top
                ctx.G.DrawImage(img, new PointF[]{new(0,1),new(1,1),new(0,0)});
            }
            finally { ctx.G.Restore(sv); img.Dispose(); }
        }
        catch { }
    }

    private void PaintForm(PdfStream stm, RenderContext ctx)
    {
        ctx.Push();
        try
        {
            if (_pdf.Resolve(stm.Dict.Get("Matrix")) is PdfArray ma && ma.Items.Count == 6)
            {
                var fm = PdfMatrix.From(ma.Items[0].AsDouble(), ma.Items[1].AsDouble(),
                    ma.Items[2].AsDouble(), ma.Items[3].AsDouble(),
                    ma.Items[4].AsDouble(), ma.Items[5].AsDouble());
                ctx.State.CTM = fm.Mul(ctx.State.CTM);
            }
            var savedRes = ctx.Res;
            ctx.Res = _pdf.Resolve(stm.Dict.Get("Resources")) as PdfDict ?? ctx.Res;
            Interpret(stm.Decode(), ctx);
            ctx.Res = savedRes;
        }
        finally { ctx.Pop(); }
    }

    private void ParseInlineImage(PdfLexer lex, RenderContext ctx)
    {
        var dict = new PdfDict();
        while (true)
        {
            lex.SkipWS();
            var tok = lex.NextToken();
            if (tok == null || (tok is string s2 && s2 == "ID")) break;
            if (tok is not PdfName key) continue;
            string full = key.Value switch {
                "W"=>"Width","H"=>"Height","CS"=>"ColorSpace",
                "BPC"=>"BitsPerComponent","F"=>"Filter",
                "IM"=>"ImageMask","D"=>"Decode","I"=>"Interpolate", _=>key.Value };
            var val = _pdf.ParseObject(lex);
            if (val != null) dict.Set(full, val);
        }
        if (lex.PeekByte()==13) lex.SkipByte();
        if (lex.PeekByte()==10) lex.SkipByte();
        int start = lex.Position;
        while (lex.Position < lex.Buf.Length-1)
            { if (lex.Buf[lex.Position]==(byte)'E'&&lex.Buf[lex.Position+1]==(byte)'I') break; lex.Position++; }
        var imgData = new byte[lex.Position-start];
        Array.Copy(lex.Buf, start, imgData, 0, imgData.Length);
        lex.Position += 2;
        var stm = new PdfStream{Dict=dict,RawData=imgData};
        PaintImage(stm, ctx);
    }

    private void ApplyExtGState(string name, RenderContext ctx)
    {
        var extGs = _pdf.Resolve(ctx.Res.Get("ExtGState")) as PdfDict;
        if (extGs == null) return;
        var gs = _pdf.Resolve(extGs.Get(name)) as PdfDict;
        if (gs == null) return;
        if (_pdf.Resolve(gs.Get("LW")) is { } lw) ctx.State.LineWidth = lw.AsDouble();
        if (_pdf.Resolve(gs.Get("CA")) is { } ca) ctx.State.StrokeAlpha = ca.AsDouble();
        if (_pdf.Resolve(gs.Get("ca")) is { } cai) ctx.State.FillAlpha = cai.AsDouble();
    }

    // ── Image decode helpers ──────────────────────────────────────
    private string GetCS(PdfDict dict)
    {
        var cs = _pdf.Resolve(dict.Get("ColorSpace"));
        return cs is PdfName n ? n.Value : (cs is PdfArray a && a.Items.Count>0 ? _pdf.Resolve(a.Items[0]).AsName() : "DeviceRGB");
    }

    // Apply a soft mask (SMask) as alpha channel to an image.
    // The SMask is a grayscale image where 0 = fully transparent, 255 = fully opaque.
    private Image ApplySMask(Image img, PdfStream smask)
    {
        try
        {
            int sw = _pdf.Resolve(smask.Dict.Get("Width")).AsInt();
            int sh = _pdf.Resolve(smask.Dict.Get("Height")).AsInt();
            if (sw <= 0 || sh <= 0) return img;
            byte[] data = smask.Decode();
            if (data.Length < sw * sh) return img;

            // Resample SMask to image size if dimensions differ.
            int iw = img.Width, ih = img.Height;
            var dst = new Bitmap(iw, ih, PixelFormat.Format32bppArgb);
            using (var src = new Bitmap(img))
            {
                for (int y = 0; y < ih; y++)
                for (int x = 0; x < iw; x++)
                {
                    int sx = (int)((long)x * sw / iw);
                    int sy = (int)((long)y * sh / ih);
                    byte alpha = data[sy * sw + sx];
                    var c = src.GetPixel(x, y);
                    dst.SetPixel(x, y, Color.FromArgb(alpha, c));
                }
            }
            img.Dispose();
            return dst;
        }
        catch { return img; }
    }

    // Resolve Indexed colorspace palette as RGB Colors.
    private Color[]? GetIndexedPalette(PdfDict dict)
    {
        var cs = _pdf.Resolve(dict.Get("ColorSpace"));
        if (cs is not PdfArray a || a.Items.Count < 4) return null;
        if (_pdf.Resolve(a.Items[0]).AsName() != "Indexed") return null;

        var baseCs = _pdf.Resolve(a.Items[1]);
        string baseName = baseCs is PdfName bn ? bn.Value
            : (baseCs is PdfArray ba && ba.Items.Count > 0 ? _pdf.Resolve(ba.Items[0]).AsName() : "DeviceRGB");
        int baseComps = baseName == "DeviceGray" ? 1 : baseName == "DeviceCMYK" ? 4 : 3;
        int hival = _pdf.Resolve(a.Items[2]).AsInt();

        byte[]? lut = null;
        var lo = _pdf.Resolve(a.Items[3]);
        if (lo is PdfStream ls) lut = ls.Decode();
        else if (lo is PdfStr lstr) lut = lstr.Bytes;
        if (lut == null) return null;

        var pal = new Color[hival + 1];
        for (int i = 0; i <= hival; i++)
        {
            int o = i * baseComps;
            if (baseComps == 1)
            {
                byte v = o < lut.Length ? lut[o] : (byte)0;
                pal[i] = Color.FromArgb(v, v, v);
            }
            else if (baseComps == 4)
            {
                double cy = o<lut.Length?lut[o]/255.0:0, mm=o+1<lut.Length?lut[o+1]/255.0:0;
                double yy = o+2<lut.Length?lut[o+2]/255.0:0, kk=o+3<lut.Length?lut[o+3]/255.0:0;
                pal[i] = Color.FromArgb((int)((1-cy)*(1-kk)*255),(int)((1-mm)*(1-kk)*255),(int)((1-yy)*(1-kk)*255));
            }
            else
            {
                byte r = o<lut.Length?lut[o]:(byte)0;
                byte g = o+1<lut.Length?lut[o+1]:(byte)0;
                byte b = o+2<lut.Length?lut[o+2]:(byte)0;
                pal[i] = Color.FromArgb(r, g, b);
            }
        }
        return pal;
    }

    private static Image? RawToImage(byte[] data, int w, int h, string cs, bool mask, Color maskColor, Color[]? palette = null)
    {
        if (w<=0||h<=0) return null;
        bool indexed = cs == "Indexed" && palette != null;
        int comps = indexed ? 1 : cs == "DeviceGray" ? 1 : cs == "DeviceCMYK" ? 4 : 3;
        try
        {
            var bmp    = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            int stride = w * comps;
            for (int row=0; row<h; row++)
            for (int col=0; col<w; col++)
            {
                int idx = row*stride + col*comps;
                if (idx+comps-1 >= data.Length) goto done;
                Color c;
                if (mask)
                {
                    // ImageMask: 1 bit (byte here) – 1=paint, 0=transparent
                    c = data[idx] != 0 ? maskColor : Color.Transparent;
                }
                else if (indexed)
                {
                    int p = data[idx];
                    c = p < palette!.Length ? palette[p] : Color.Black;
                }
                else if (comps==1) { byte v=data[idx]; c=Color.FromArgb(v,v,v); }
                else if (comps==4)
                {
                    double cy=data[idx]/255.0,m=data[idx+1]/255.0,y=data[idx+2]/255.0,k=data[idx+3]/255.0;
                    c=Color.FromArgb((int)((1-cy)*(1-k)*255),(int)((1-m)*(1-k)*255),(int)((1-y)*(1-k)*255));
                }
                else c=Color.FromArgb(data[idx],data[idx+1],idx+2<data.Length?data[idx+2]:(byte)0);
                bmp.SetPixel(col,row,c);
            }
            done: return bmp;
        }
        catch { return null; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════
    private static double D(List<object> ops, int i) => i < ops.Count ? ((PdfObj)ops[i]).AsDouble() : 0;
    private static int    I(List<object> ops, int i) => i < ops.Count ? ((PdfObj)ops[i]).AsInt()    : 0;
    private static string S(List<object> ops, int i)
    {
        if (i >= ops.Count) return "";
        return ops[i] switch { PdfName n=>n.Value, PdfStr s=>s.Latin1, _=>"" };
    }
    private static PdfMatrix Mat(List<object> ops) =>
        PdfMatrix.From(D(ops,0),D(ops,1),D(ops,2),D(ops,3),D(ops,4),D(ops,5));
    private static Color Gray(double v) { int g=(int)(v*255); return Color.FromArgb(g,g,g); }
    private static Color Rgb(double r,double g,double b)
        => Color.FromArgb((int)(r*255),(int)(g*255),(int)(b*255));
    private static Color Cmyk(double c,double m,double y,double k)
        => Color.FromArgb((int)((1-c)*(1-k)*255),(int)((1-m)*(1-k)*255),(int)((1-y)*(1-k)*255));

    // SC/SCN/sc/scn: interpret numeric operands by count; trailing Name (Pattern) is skipped.
    private static Color ColorFromOps(List<object> ops)
    {
        int n = ops.Count;
        while (n > 0 && ops[n-1] is PdfName) n--;
        if (n >= 4) return Cmyk(D(ops,0),D(ops,1),D(ops,2),D(ops,3));
        if (n == 3) return Rgb(D(ops,0),D(ops,1),D(ops,2));
        if (n == 1) return Gray(D(ops,0));
        return Color.Black;
    }

    private static Pen MakePen(GfxState s, float zoom = 1f)
    {
        double ctmScale = Math.Sqrt(s.CTM.A * s.CTM.A + s.CTM.B * s.CTM.B);
        float screenWidth = (float)(s.LineWidth * ctmScale * zoom);

        // Match Chrome/PDFium hairline look: thin PDF lines (≤ ~1pt) render at 0.5 device
        // pixels so antialiasing produces a light-grey hairline instead of a solid bar.
        float penW = screenWidth < 1.5f ? 0.5f : screenWidth;
        var pen = new Pen(s.StrokeColor, penW);
        pen.StartCap = pen.EndCap = s.GdiLineCap;
        pen.LineJoin = s.GdiLineJoin;
        if (s.DashArray is {Length:>0})
        {
            pen.DashStyle   = DashStyle.Custom;
            float lw = Math.Max(0.01f, (float)s.LineWidth);
            pen.DashPattern = s.DashArray.Select(x => Math.Max(0.01f, x/lw)).ToArray();
            pen.DashOffset  = s.DashPhase / lw;
            // Zero-length dashes render as dots iff dash cap is round.
            if (s.DashArray.Any(x => x == 0f)) pen.DashCap = DashCap.Round;
        }
        return pen;
    }

    private static double[] GetBox(PdfDict page)
    {
        var arr = page.Get("MediaBox") as PdfArray;
        return arr != null
            ? arr.Items.Select(x => x.AsDouble()).ToArray()
            : new double[]{0,0,612,792};
    }

    public void Dispose()
    {
        _measureG?.Dispose();
        _measureBmp?.Dispose();
    }
}

// ── Render context ────────────────────────────────────────────────
public sealed class RenderContext
{
    public Graphics G;
    public PdfMatrix BaseMatrix;
    public float Zoom;
    public PdfDict Res;
    public PdfParser Pdf;
    public GfxState State = new();
    public Stack<GfxState> StateStack = new();
    public GraphicsPath Path = new();
    public double CurX, CurY;
    public FontInfo? CurrentFont;

    public RenderContext(Graphics g, PdfMatrix bm, float zoom, PdfDict res, PdfParser pdf)
    { G=g; BaseMatrix=bm; Zoom=zoom; Res=res; Pdf=pdf; State.CTM=bm; }

    public void Push() => StateStack.Push(State.Clone());
    public void Pop()  { if(StateStack.Count>0){State=StateStack.Pop();G.ResetTransform();} }

    public void NewFigure() => Path.StartFigure();
    public void MoveTo(double x,double y) { CurX=x; CurY=y; }
    public void LineTo(double x,double y)
    {
        var (x1,y1)=State.CTM.Transform(CurX,CurY);
        var (x2,y2)=State.CTM.Transform(x,y);
        // No pixel snapping: let antialiasing work naturally.
        // Lines at adjacent sub-pixel positions (0.5px apart in PDF)
        // will overlap and appear darker than single isolated lines.
        Path.AddLine((float)x1,(float)y1,(float)x2,(float)y2);
        CurX=x; CurY=y;
    }
    public void CurveTo(double x1,double y1,double x2,double y2,double x3,double y3)
    {
        var (ax,ay)=State.CTM.Transform(CurX,CurY);
        var (bx,by)=State.CTM.Transform(x1,y1);
        var (cx,cy)=State.CTM.Transform(x2,y2);
        var (dx,dy)=State.CTM.Transform(x3,y3);
        Path.AddBezier((float)ax,(float)ay,(float)bx,(float)by,(float)cx,(float)cy,(float)dx,(float)dy);
        CurX=x3; CurY=y3;
    }
    public void AddRect(double x,double y,double w,double h)
    {
        var (ax,ay)=State.CTM.Transform(x,y);
        var (bx,by)=State.CTM.Transform(x+w,y+h);
        float left  =(float)Math.Min(ax,bx);
        float top   =(float)Math.Min(ay,by);
        float right =(float)Math.Max(ax,bx);
        float bottom=(float)Math.Max(ay,by);
        Path.AddRectangle(new RectangleF(left,top,right-left,bottom-top));
    }
    public void Stroke()
    {
        using var pen = MakePen(State, Zoom);
        if (Path.PointCount > 0) try { G.DrawPath(pen, Path); } catch { }
        NewPath();
    }
    public void Fill(FillMode mode)
    {
        Path.FillMode = mode;
        using var brush = new SolidBrush(State.EffectiveFillColor);
        if (Path.PointCount>0) try{G.FillPath(brush,Path);}catch{}
        NewPath();
    }
    public void FillStroke(FillMode mode)
    {
        Path.FillMode = mode;
        using var brush = new SolidBrush(State.EffectiveFillColor);
        if (Path.PointCount>0){try{G.FillPath(brush,Path);}catch{}}
        using var pen = MakePen(State, Zoom);
        if (Path.PointCount>0){try{G.DrawPath(pen,Path);}catch{}}
        NewPath();
    }
    public void NewPath() { Path.Dispose(); Path=new GraphicsPath(); }

    private static Pen MakePen(GfxState s, float zoom = 1f)
    {
        // CTM scale = PDF content transforms only (no viewport zoom).
        double ctmScale = Math.Sqrt(s.CTM.A * s.CTM.A + s.CTM.B * s.CTM.B);
        float screenWidth = (float)(s.LineWidth * ctmScale * zoom);

        // Match Chrome/PDFium: thin PDF lines (<= 1pt) are always drawn as a single
        // device pixel hairline regardless of viewport zoom — this gives a consistent
        // thickness at any zoom level, like Chrome does for table rules.
        float penW = s.LineWidth <= 1.0 ? 1f : screenWidth;
        var pen = new Pen(s.EffectiveStrokeColor, penW);
        pen.StartCap = pen.EndCap = s.GdiLineCap;
        pen.LineJoin = s.GdiLineJoin;
        if (s.DashArray is {Length:>0})
        {
            pen.DashStyle   = DashStyle.Custom;
            float lw = Math.Max(0.01f, (float)s.LineWidth);
            pen.DashPattern = s.DashArray.Select(x => Math.Max(0.01f, x/lw)).ToArray();
            pen.DashOffset  = s.DashPhase / lw;
            if (s.DashArray.Any(x => x == 0f)) pen.DashCap = DashCap.Round;
        }
        return pen;
    }
}
