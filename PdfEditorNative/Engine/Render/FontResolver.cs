// Engine/Render/FontResolver.cs
// Builds a byte→char map for each PDF font by reading:
//   /Encoding /Differences  (Type1, TrueType with custom encoding)
//   /Encoding /BaseEncoding
//   /ToUnicode CMap stream
// Falls back to Latin-1 / MacRoman / WinAnsi standard encodings.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using PdfEditorNative.Engine;

namespace PdfEditorNative.Engine.Render;

public sealed class FontInfo
{
    /// <summary>byte (0-255) → Unicode char (for 1-byte encodings)</summary>
    public char[] ByteMap = BuildLatin1();
    /// <summary>code → Unicode char (for 2-byte CID fonts, covers all codes)</summary>
    public Dictionary<int, char> CodeMap = new();
    /// <summary>byte → glyph advance width in 1/1000 font units (1-byte fonts)</summary>
    public double[] Widths = BuildDefaultWidths();
    /// <summary>code → width (for CID fonts with /W array)</summary>
    public Dictionary<int, double> WidthMap = new();
    /// <summary>True if this is a Type0/CID font using 2-byte character codes</summary>
    public bool IsTwoByte = false;

    public System.Drawing.Font? GdiFont;

    /// <summary>Parsed CFF program (non-null → render via Type 2 interpreter).</summary>
    internal CffInfo? Cff;
    /// <summary>Cached Type 2 glyph outlines in CFF font units, keyed by GID.</summary>
    internal readonly Dictionary<int, GraphicsPath?> CffPathCache = new();

    private static char[] BuildLatin1()
    {
        var m = new char[256];
        for (int i = 0; i < 256; i++) m[i] = (char)i;
        return m;
    }
    private static double[] BuildDefaultWidths()
    {
        var w = new double[256];
        for (int i = 0; i < 256; i++) w[i] = 600;
        return w;
    }
}

public static class FontResolver
{
    // Standard encoding tables
    private static readonly char[] WinAnsi   = BuildWinAnsi();
    private static readonly char[] MacRoman  = BuildMacRoman();
    private static readonly char[] StdEncoding = BuildStd();

    // ── Embedded font loading (TrueType FontFile2) ────────────────
    private static readonly PrivateFontCollection _embeddedFonts = new();
    private static readonly Dictionary<string, FontFamily> _familyCache = new();
    private static readonly object _embedLock = new();

    private static FontFamily? TryLoadEmbeddedFont(PdfDict fontDict, PdfParser pdf, out CffInfo? cffInfo)
    {
        cffInfo = null;
        try
        {
            PdfDict? fd = null;
            PdfDict? descFont = null;
            string subtype = pdf.Resolve(fontDict.Get("Subtype")).AsName();
            if (subtype == "Type0")
            {
                var descArr = pdf.Resolve(fontDict.Get("DescendantFonts")) as PdfArray;
                if (descArr?.Items.Count > 0)
                {
                    descFont = pdf.Resolve(descArr.Items[0]) as PdfDict;
                    fd = pdf.Resolve(descFont?.Get("FontDescriptor")) as PdfDict;
                }
            }
            else
            {
                fd = pdf.Resolve(fontDict.Get("FontDescriptor")) as PdfDict;
            }
            if (fd == null) return null;

            var fontStm2 = pdf.Resolve(fd.Get("FontFile2")) as PdfStream;
            var fontStm3 = pdf.Resolve(fd.Get("FontFile3")) as PdfStream;
            string f3Subtype = fontStm3 != null ? pdf.Resolve(fontStm3.Dict.Get("Subtype")).AsName() : "";

            byte[]? fontBytes = null;
            if (fontStm2 != null)
                fontBytes = fontStm2.Decode();
            else if (fontStm3 != null && f3Subtype == "OpenType")
                fontBytes = fontStm3.Decode();
            else if (fontStm3 != null && (f3Subtype == "CIDFontType0C" || f3Subtype == "Type1C"))
            {
                // Pure CFF — parse it (for native Type 2 rendering) and also wrap in
                // OpenType so GDI+ can load it as fallback.
                byte[] cff = fontStm3.Decode();
                try { cffInfo = CffParser.Parse(cff); }
                catch { cffInfo = null; }
                var (u2c, cidWidths, dw) = BuildCidMappings(fontDict, descFont, pdf);
                string uniqueFam = "PDF_" + Guid.NewGuid().ToString("N").Substring(0, 10);
                fontBytes = OpenTypeBuilder.BuildOtf(cff, u2c, cidWidths, dw, uniqueFam);
            }
            if (fontBytes == null || fontBytes.Length < 100) return null;

            string fontName = pdf.Resolve(fd.Get("FontName")).AsName();
            if (string.IsNullOrEmpty(fontName)) fontName = "Emb_" + fontBytes.Length;

            lock (_embedLock)
            {
                if (_familyCache.TryGetValue(fontName, out var cached)) return cached;

                int beforeCount = _embeddedFonts.Families.Length;
                IntPtr ptr = Marshal.AllocHGlobal(fontBytes.Length);
                try
                {
                    Marshal.Copy(fontBytes, 0, ptr, fontBytes.Length);
                    _embeddedFonts.AddMemoryFont(ptr, fontBytes.Length);
                }
                finally { Marshal.FreeHGlobal(ptr); }

                var families = _embeddedFonts.Families;
                if (families.Length <= beforeCount) return null;

                var newFamily = families[families.Length - 1];
                _familyCache[fontName] = newFamily;
                return newFamily;
            }
        }
        catch { return null; }
    }

    // Build Unicode→GID + GID→width maps for a Type0 CID font.
    // For Identity CIDToGIDMap (default for CFF CID fonts), CID == GID.
    private static (Dictionary<int,int> u2g, Dictionary<int,int> widths, int dw)
        BuildCidMappings(PdfDict fontDict, PdfDict? descFont, PdfParser pdf)
    {
        var u2g = new Dictionary<int, int>();
        var widths = new Dictionary<int, int>();
        int dw = 1000;

        // ToUnicode CMap: code → Unicode. For CFF CID fonts, code IS the GID.
        var toUni = pdf.Resolve(fontDict.Get("ToUnicode")) as PdfStream;
        if (toUni != null)
        {
            var codeMap = new Dictionary<int, char>();
            ParseToUnicodeIntoCodeMap(toUni.Decode(), codeMap);
            foreach (var kv in codeMap)
            {
                int gid = kv.Key;   // CID == GID for Identity
                int uni = kv.Value;
                if (uni > 0 && !u2g.ContainsKey(uni)) u2g[uni] = gid;
            }
        }

        // Widths from DescendantFont /W array.
        if (descFont != null)
        {
            dw = (int)(pdf.Resolve(descFont.Get("DW"))?.AsDouble() ?? 1000.0);
            var wArr = pdf.Resolve(descFont.Get("W")) as PdfArray;
            if (wArr != null)
            {
                var wd = new Dictionary<int, double>();
                ParseCidWidths(wArr, wd);
                foreach (var kv in wd)
                    if (kv.Key >= 0) widths[kv.Key] = (int)kv.Value;
            }
        }
        return (u2g, widths, dw);
    }

    private static Font? BuildFontFromFamily(FontFamily family, float size, string pdfName)
    {
        bool bold = pdfName.Contains("Bold", StringComparison.OrdinalIgnoreCase);
        bool italic = pdfName.Contains("Italic", StringComparison.OrdinalIgnoreCase)
                   || pdfName.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        FontStyle style = FontStyle.Regular;
        if (bold && family.IsStyleAvailable(FontStyle.Bold)) style |= FontStyle.Bold;
        if (italic && family.IsStyleAvailable(FontStyle.Italic)) style |= FontStyle.Italic;
        // If requested style isn't available, fall back to any available style.
        if (!family.IsStyleAvailable(style))
        {
            foreach (var s in new[] { FontStyle.Regular, FontStyle.Bold, FontStyle.Italic, FontStyle.Bold|FontStyle.Italic })
                if (family.IsStyleAvailable(s)) { style = s; break; }
        }
        try { return new Font(family, size, style, GraphicsUnit.Point); }
        catch { return null; }
    }

    // ── Build FontInfo from a PDF font dict ───────────────────────
    public static FontInfo Build(PdfDict fontDict, PdfParser pdf)
    {
        var info = new FontInfo();

        string subtype  = pdf.Resolve(fontDict.Get("Subtype")).AsName();
        string baseFont = pdf.Resolve(fontDict.Get("BaseFont")).AsName();

        // Try to load the actual embedded font program (TrueType); fall back to system.
        var embFamily = TryLoadEmbeddedFont(fontDict, pdf, out var cffInfo);
        // Only enable native CFF rendering for CID-keyed fonts (where the CFF
        // charset directly provides CID→GID). Simple CFF with name-keyed encoding
        // would need the encoding parsed, so we fall back to the OpenType wrapper.
        if (cffInfo != null && cffInfo.IsCid) info.Cff = cffInfo;

        // ── Type0 (composite / CID) font ─────────────────────────
        if (subtype == "Type0")
        {
            info.IsTwoByte = true;
            info.GdiFont = (embFamily != null ? BuildFontFromFamily(embFamily, 12, baseFont) : null)
                         ?? MapToGdi(baseFont, 12);

            // /ToUnicode CMap
            var toUni = pdf.Resolve(fontDict.Get("ToUnicode")) as PdfStream;
            if (toUni != null)
                ParseToUnicodeIntoCodeMap(toUni.Decode(), info.CodeMap);

            // /W array + /DW from DescendantFont
            var desc = pdf.Resolve(fontDict.Get("DescendantFonts")) as PdfArray;
            if (desc?.Items.Count > 0)
            {
                var df = pdf.Resolve(desc.Items[0]) as PdfDict;
                if (df != null)
                {
                    double dw = pdf.Resolve(df.Get("DW"))?.AsDouble() ?? 1000.0;
                    info.WidthMap[-1] = dw;
                    var wArr = pdf.Resolve(df.Get("W")) as PdfArray;
                    if (wArr != null) ParseCidWidths(wArr, info.WidthMap);
                }
            }
            return info;
        }

        // ── Simple font (Type1, TrueType, etc.) ───────────────────
        var encObj = pdf.Resolve(fontDict.Get("Encoding"));
        string baseEncName = "";
        PdfArray? differences = null;

        if (encObj is PdfName en)
            baseEncName = en.Value;
        else if (encObj is PdfDict ed)
        {
            baseEncName  = pdf.Resolve(ed.Get("BaseEncoding")).AsName();
            differences  = pdf.Resolve(ed.Get("Differences")) as PdfArray;
        }

        info.ByteMap = baseEncName switch
        {
            "WinAnsiEncoding"   => (char[])WinAnsi.Clone(),
            "MacRomanEncoding"  => (char[])MacRoman.Clone(),
            "StandardEncoding"  => (char[])StdEncoding.Clone(),
            _ => (char[])WinAnsi.Clone(),
        };

        if (differences != null)
            ApplyDifferences(info.ByteMap, differences, pdf);

        // /ToUnicode overrides
        var toUniSimple = pdf.Resolve(fontDict.Get("ToUnicode")) as PdfStream;
        if (toUniSimple != null)
        {
            var cmap = ParseToUnicode(toUniSimple.Decode());
            foreach (var kv in cmap)
                if (kv.Key < 256) info.ByteMap[kv.Key] = kv.Value;
        }

        // Widths
        int firstChar = fontDict.Get("FirstChar")?.AsInt() ?? 0;
        var widthsArr = pdf.Resolve(fontDict.Get("Widths")) as PdfArray;
        if (widthsArr != null)
            for (int i = 0; i < widthsArr.Items.Count && firstChar + i < 256; i++)
                info.Widths[firstChar + i] = widthsArr.Items[i].AsDouble();

        info.GdiFont = (embFamily != null ? BuildFontFromFamily(embFamily, 12, baseFont) : null)
                     ?? MapToGdi(baseFont, 12);
        return info;
    }

    // ── CID /W array parser: [c1 c2 [w...]] or [c1 c2 w] ────────
    private static void ParseCidWidths(PdfArray w, Dictionary<int, double> map)
    {
        int i = 0;
        while (i < w.Items.Count)
        {
            int c1 = w.Items[i].AsInt(); i++;
            if (i >= w.Items.Count) break;
            var next = w.Items[i]; i++;
            if (next is PdfArray wa)
            {
                for (int j = 0; j < wa.Items.Count; j++)
                    map[c1 + j] = wa.Items[j].AsDouble();
            }
            else
            {
                int c2 = next.AsInt();
                if (i < w.Items.Count)
                {
                    double ww = w.Items[i].AsDouble(); i++;
                    for (int c = c1; c <= c2; c++) map[c] = ww;
                }
            }
        }
    }

    // ── ToUnicode → CodeMap (supports codes > 255) ───────────────
    // Parses line-by-line to avoid cross-line regex matches.
    private static void ParseToUnicodeIntoCodeMap(byte[] data, Dictionary<int, char> map)
    {
        string text = System.Text.Encoding.Latin1.GetString(data);
        var hexRx = new System.Text.RegularExpressions.Regex(@"<([0-9A-Fa-f]+)>");

        int pos = 0;
        bool inBfChar  = false;
        bool inBfRange = false;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Contains("beginbfchar",  StringComparison.Ordinal) && !line.StartsWith("end")) { inBfChar  = true;  continue; }
            if (line.Contains("endbfchar",    StringComparison.Ordinal))                             { inBfChar  = false; continue; }
            if (line.Contains("beginbfrange", StringComparison.Ordinal) && !line.StartsWith("end")) { inBfRange = true;  continue; }
            if (line.Contains("endbfrange",   StringComparison.Ordinal))                             { inBfRange = false; continue; }

            if (!inBfChar && !inBfRange) continue;

            // Collect all hex values on this line
            var hexMatches = hexRx.Matches(line);
            if (hexMatches.Count == 0) continue;

            var hexValues = new List<int>();
            foreach (System.Text.RegularExpressions.Match m in hexMatches)
                hexValues.Add(Convert.ToInt32(m.Groups[1].Value, 16));

            if (inBfChar && hexValues.Count >= 2)
            {
                // <srcCode> <dstCode>
                map[hexValues[0]] = (char)hexValues[1];
            }
            else if (inBfRange && hexValues.Count >= 3)
            {
                // <lo> <hi> <dstStart>
                int lo = hexValues[0], hi = hexValues[1], dst = hexValues[2];
                for (int c = lo; c <= hi; c++) map[c] = (char)(dst + c - lo);
            }
        }
    }

    // ── Apply /Differences array ──────────────────────────────────
    private static void ApplyDifferences(char[] map, PdfArray diffs, PdfParser pdf)
    {
        int code = 0;
        foreach (var item in diffs.Items)
        {
            if (item is PdfInt pi)  { code = pi.Value; continue; }
            if (item is PdfName pn)
            {
                string gname = pn.Value;
                int cp = GlyphNames.ToUnicode(gname);
                if (cp == 0)
                {
                    // Try single-char name fallback: /A → 'A', /a → 'a'
                    if (gname.Length == 1) cp = gname[0];
                    else cp = (int)Encoding.Latin1.GetBytes(gname)[0];
                }
                if (code < 256) map[code] = cp != 0 ? (char)cp : (char)code;
                code++;
            }
        }
    }

    // ── Parse ToUnicode CMap → byte map (for 1-byte fonts) ───────
    private static Dictionary<int, char> ParseToUnicode(byte[] data)
    {
        var result = new Dictionary<int, char>();
        ParseToUnicodeIntoCodeMap(data, result);
        return result;
    }


    // ── GDI font name mapping ─────────────────────────────────────
    public static System.Drawing.Font MapToGdi(string pdfName, float size)
    {
        size = Math.Max(0.5f, size);
        bool bold   = pdfName.Contains("Bold",    StringComparison.OrdinalIgnoreCase)
                   || pdfName.Contains("Heavy",   StringComparison.OrdinalIgnoreCase)
                   || pdfName.Contains("Black",   StringComparison.OrdinalIgnoreCase);
        bool italic = pdfName.Contains("Italic",  StringComparison.OrdinalIgnoreCase)
                   || pdfName.Contains("Oblique", StringComparison.OrdinalIgnoreCase)
                   || pdfName.Contains("Slanted", StringComparison.OrdinalIgnoreCase);
        var style = (bold   ? System.Drawing.FontStyle.Bold   : System.Drawing.FontStyle.Regular)
                  | (italic ? System.Drawing.FontStyle.Italic : System.Drawing.FontStyle.Regular);

        // Strip subset prefix like "MUFUZY+" or "ABCDEF+"
        string name = pdfName.Contains('+') ? pdfName[(pdfName.IndexOf('+')+1)..] : pdfName;
        // Strip style suffixes for family lookup
        string nameLower = name.ToLowerInvariant();

        // "Condensed"/"Narrow"/"Extended" style hints → use narrow/wide variants when available.
        bool condensed = nameLower.Contains("condensed") || nameLower.Contains("narrow")
                      || nameLower.Contains("compressed");
        bool extended  = nameLower.Contains("extended") || nameLower.Contains("expanded")
                      || nameLower.Contains("extexte")  || nameLower.Contains("wide")
                      || nameLower.Contains("extexte");

        string family = nameLower switch
        {
            // Serif
            var n when n.Contains("timesnewroman") || n.Contains("times new roman") => "Times New Roman",
            var n when n.StartsWith("times")         => "Times New Roman",
            var n when n.Contains("georgia")         => "Georgia",
            var n when n.Contains("garamond")        => "Garamond",
            var n when n.Contains("palatino")        => "Palatino Linotype",

            // Condensed sans-serif (Helvetica Condensed, Switzerland Condensed, etc.)
            var n when n.Contains("switzerland") || n.Contains("helvcondensed")
                    || n.Contains("helveticacondensed") || n.Contains("arialnarrow") => "Arial Narrow",

            // Monospace
            var n when n.Contains("couriernew") || n.Contains("courier new") => "Courier New",
            var n when n.StartsWith("courier")       => "Courier New",
            var n when n.Contains("inconsolata")     => "Courier New",
            var n when n.Contains("sourcecodepro")   => "Courier New",
            var n when n.Contains("robotomono")      => "Courier New",
            var n when n.Contains("ibmplexmono")     => "Courier New",
            var n when n.Contains("spacemono")       => "Courier New",

            // Symbol / Decorative
            var n when n.Contains("symbol")          => "Symbol",
            var n when n.Contains("zapfdingbat") || n.Contains("wingding") => "Wingdings",

            // Sans-serif — map to closest Windows equivalent
            var n when n.Contains("arial")           => condensed ? "Arial Narrow" : "Arial",
            var n when n.Contains("helvetica")       => condensed ? "Arial Narrow" : "Arial",
            // Microgramma — display font, Arial Narrow is closest narrow sans
            var n when n.Contains("microgramma")     => "Arial Narrow",
            var n when n.Contains("calibri")         => "Calibri",
            var n when n.Contains("tahoma")          => "Tahoma",
            var n when n.Contains("trebuchet")       => "Trebuchet MS",
            var n when n.Contains("verdana")         => "Verdana",
            var n when n.Contains("franklin")        => "Franklin Gothic Medium",
            var n when n.Contains("gill")            => "Gill Sans MT",
            var n when n.Contains("futura")          => "Century Gothic",
            var n when n.Contains("centurygothic")   => "Century Gothic",

            // Google Fonts commonly embedded in PDFs
            var n when n.Contains("roboto")          => "Arial",
            var n when n.Contains("opensans")        => "Arial",
            var n when n.Contains("lato")            => "Arial",
            var n when n.Contains("montserrat")      => "Arial",
            var n when n.Contains("raleway")         => "Arial",
            var n when n.Contains("poppins")         => "Arial",
            var n when n.Contains("nunito")          => "Arial",
            var n when n.Contains("ubuntu")          => "Arial",
            var n when n.Contains("oswald")          => "Arial Narrow",
            var n when n.Contains("notosans")        => "Arial",
            var n when n.Contains("notoserif")       => "Times New Roman",
            var n when n.Contains("ibmplexsans")     => "Arial",
            var n when n.Contains("ibmplexserif")    => "Times New Roman",
            var n when n.Contains("sourceserif")     => "Times New Roman",
            var n when n.Contains("sourcesans")      => "Arial",
            var n when n.Contains("merriweather")    => "Times New Roman",
            var n when n.Contains("playfair")        => "Times New Roman",
            var n when n.Contains("cambo")           => "Times New Roman",
            var n when n.Contains("cabin")           => "Arial",
            var n when n.Contains("droid")           => "Arial",
            var n when n.Contains("ptserif")         => "Times New Roman",
            var n when n.Contains("ptsans")          => "Arial",
            var n when n.Contains("fira")            => "Arial",
            var n when n.Contains("worksans")        => "Arial",
            var n when n.Contains("inter")           => "Arial",

            // CJK / other scripts
            var n when n.Contains("gothic") || n.Contains("mincho") => "Arial",

            // Default
            _ => condensed ? "Arial Narrow" : "Arial",
        };

        try { return new System.Drawing.Font(family, size, style, System.Drawing.GraphicsUnit.Point); }
        catch
        {
            try { return new System.Drawing.Font("Arial", size, style, System.Drawing.GraphicsUnit.Point); }
            catch { return new System.Drawing.Font("Arial", size, System.Drawing.GraphicsUnit.Point); }
        }
    }

    // ── Standard encoding tables ──────────────────────────────────
    private static char[] BuildWinAnsi()
    {
        var m = new char[256];
        for (int i = 0; i < 256; i++) m[i] = (char)i;
        // WinAnsiEncoding overrides for 0x80-0x9F (cp1252)
        var overrides = new Dictionary<int,char>{
            {0x80,'\u20AC'},{0x82,'\u201A'},{0x83,'\u0192'},{0x84,'\u201E'},
            {0x85,'\u2026'},{0x86,'\u2020'},{0x87,'\u2021'},{0x88,'\u02C6'},
            {0x89,'\u2030'},{0x8A,'\u0160'},{0x8B,'\u2039'},{0x8C,'\u0152'},
            {0x8E,'\u017D'},{0x91,'\u2018'},{0x92,'\u2019'},{0x93,'\u201C'},
            {0x94,'\u201D'},{0x95,'\u2022'},{0x96,'\u2013'},{0x97,'\u2014'},
            {0x98,'\u02DC'},{0x99,'\u2122'},{0x9A,'\u0161'},{0x9B,'\u203A'},
            {0x9C,'\u0153'},{0x9E,'\u017E'},{0x9F,'\u0178'},
        };
        foreach (var kv in overrides) m[kv.Key] = kv.Value;
        return m;
    }

    private static char[] BuildMacRoman()
    {
        var m = new char[256];
        for (int i = 0; i < 128; i++) m[i] = (char)i;
        // Mac Roman upper half
        int[] upper = {
            0xC4,0xC5,0xC7,0xC9,0xD1,0xD6,0xDC,0xE1,0xE0,0xE2,0xE4,0xE5,0xE7,0xE9,0xE8,0xEA,
            0xEB,0xED,0xEC,0xEE,0xEF,0xF1,0xF3,0xF2,0xF4,0xF6,0xFA,0xF9,0xFB,0xFC,0x2020,0xB0,
            0xA2,0xA3,0xA7,0x2022,0xB6,0xDF,0xAE,0xA9,0x2122,0xB4,0xA8,0x2260,0xC6,0xD8,0x221E,
            0xB1,0x2264,0x2265,0xA5,0xB5,0x2202,0x2211,0x220F,0x3C0,0x222B,0xAA,0xBA,0x3A9,0xE6,0xF8,
            0xBF,0xA1,0xAC,0x221A,0x192,0x2248,0x2206,0xAB,0xBB,0x2026,0xA0,0xC0,0xC3,0xD5,0x152,0x153,
            0x2013,0x2014,0x201C,0x201D,0x2018,0x2019,0xF7,0x25CA,0xFF,0x178,0x2044,0x20AC,0x2039,0x203A,0xFB01,0xFB02,
            0x2021,0xB7,0x201A,0x201E,0x2030,0xC2,0xCA,0xC1,0xCB,0xC8,0xCD,0xCE,0xCF,0xCC,0xD3,0xD4,
            0xF8FF,0xD2,0xDA,0xDB,0xD9,0x131,0x2C6,0x2DC,0xAF,0x2D8,0x2D9,0x2DA,0xB8,0x2DD,0x2DB,0x2C7
        };
        for (int i = 0; i < upper.Length && i+128 < 256; i++)
            m[i + 128] = (char)upper[i];
        return m;
    }

    private static char[] BuildStd()
    {
        // StandardEncoding — mostly ASCII + some specials
        var m = BuildWinAnsi();
        m[0x60] = '\u2018'; m[0x27] = '\u2019';
        m[0x22] = '\u201C';
        return m;
    }
}
