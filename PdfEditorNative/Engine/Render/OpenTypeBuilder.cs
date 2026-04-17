// Wraps a raw CFF font program (as found in PDF /FontFile3 /Subtype /CIDFontType0C
// or /Type1C) into a valid OpenType/CFF font file that GDI+ can load.
//
// An OpenType/CFF file has sfnt version 'OTTO' and these tables:
//   'CFF ' — the CFF program (as-is)
//   'cmap' — Unicode → GID mapping
//   'head' — font header
//   'hhea' — horizontal header
//   'hmtx' — horizontal metrics
//   'maxp' — maximum profile
//   'name' — font names
//   'OS/2' — OS/2 and Windows metrics
//   'post' — PostScript info
using System;
using System.Collections.Generic;
using System.IO;

namespace PdfEditorNative.Engine.Render;

internal static class OpenTypeBuilder
{
    /// <summary>
    /// Build an OpenType/CFF font file from raw CFF bytes.
    /// </summary>
    public static byte[] BuildOtf(byte[] cff, Dictionary<int, int> unicodeToCid,
                                   Dictionary<int, int> cidWidths, int defaultWidth,
                                   string? uniqueFamily = null,
                                   int fontWeight = 400,
                                   bool isItalic = false)
    {
        var info = CffParser.Parse(cff);
        if (uniqueFamily != null) info.FontName = uniqueFamily;
        info.Weight = fontWeight;
        info.IsItalic = isItalic;
        int numGlyphs = Math.Max(1, info.NumGlyphs);

        // Build CID → GID inverse map from CFF charset (gidToCid)
        var cidToGid = new Dictionary<int, int>();
        foreach (var kv in info.GidToCid)
            if (!cidToGid.ContainsKey(kv.Value)) cidToGid[kv.Value] = kv.Key;

        // Convert unicode→CID to unicode→GID using charset.
        var unicodeToGid = new Dictionary<int, int>();
        foreach (var kv in unicodeToCid)
            if (cidToGid.TryGetValue(kv.Value, out int gid)) unicodeToGid[kv.Key] = gid;

        // Start with PDF /W widths (authoritative per PDF spec).
        var widths = new Dictionary<int, int>();
        foreach (var kv in cidWidths)
            if (cidToGid.TryGetValue(kv.Key, out int gid)) widths[gid] = kv.Value;

        // Override with CFF-internal widths only when they look sensible and /W is missing.
        foreach (var kv in info.GidWidths)
        {
            if (widths.ContainsKey(kv.Key)) continue;
            // Sanity: reject obviously bad values
            if (kv.Value > 0 && kv.Value < 3000) widths[kv.Key] = kv.Value;
        }

        // Build each table in memory.
        byte[] cmapTable = BuildCmap(unicodeToGid);
        byte[] headTable = BuildHead(info, numGlyphs);
        byte[] hheaTable;  // built after we know advanceWidthMax
        byte[] maxpTable = BuildMaxp(numGlyphs);
        byte[] nameTable = BuildName(info.FontName);
        byte[] os2Table  = BuildOS2(info);
        byte[] postTable = BuildPost(info);
        byte[] hmtxTable = BuildHmtx(numGlyphs, widths, defaultWidth, out int advanceWidthMax);
        hheaTable = BuildHhea(info, numGlyphs, advanceWidthMax);

        var tables = new (string tag, byte[] data)[]
        {
            ("CFF ", cff),
            ("OS/2", os2Table),
            ("cmap", cmapTable),
            ("head", headTable),
            ("hhea", hheaTable),
            ("hmtx", hmtxTable),
            ("maxp", maxpTable),
            ("name", nameTable),
            ("post", postTable),
        };
        // Tables must be in alphabetical-tag order in directory (but OpenType actually
        // allows any order; most tools use sorted). Sort by tag.
        Array.Sort(tables, (a, b) => string.CompareOrdinal(a.tag, b.tag));

        return Assemble(tables);
    }

    // ---- Assembly -----------------------------------------------------

    private static byte[] Assemble((string tag, byte[] data)[] tables)
    {
        int numTables = tables.Length;
        int headerSize = 12 + 16 * numTables;

        // Pad each table to 4 bytes and compute offsets.
        int[] offsets = new int[numTables];
        int[] lengths = new int[numTables];
        int pos = headerSize;
        for (int i = 0; i < numTables; i++)
        {
            offsets[i] = pos;
            lengths[i] = tables[i].data.Length;
            pos += Align4(tables[i].data.Length);
        }

        byte[] output = new byte[pos];

        // Write sfnt header (OTTO for CFF-based)
        uint sfntVersion = 0x4F54544F; // 'OTTO'
        ushort searchRange = (ushort)((1 << Log2((uint)numTables)) * 16);
        ushort entrySelector = (ushort)Log2((uint)numTables);
        ushort rangeShift = (ushort)(numTables * 16 - searchRange);

        WriteU32(output, 0, sfntVersion);
        WriteU16(output, 4, (ushort)numTables);
        WriteU16(output, 6, searchRange);
        WriteU16(output, 8, entrySelector);
        WriteU16(output, 10, rangeShift);

        // Write table directory and table data.
        int dirPos = 12;
        for (int i = 0; i < numTables; i++)
        {
            byte[] data = tables[i].data;
            int off = offsets[i];
            int len = lengths[i];
            // Copy data
            Array.Copy(data, 0, output, off, len);
            // Compute checksum on aligned data (zero-pad)
            uint checksum = Checksum(output, off, Align4(len));
            // Write directory entry
            WriteTag(output, dirPos, tables[i].tag);
            WriteU32(output, dirPos + 4, checksum);
            WriteU32(output, dirPos + 8, (uint)off);
            WriteU32(output, dirPos + 12, (uint)len);
            dirPos += 16;
        }

        // Compute checkSumAdjustment for head table:
        // 0xB1B0AFBA - sum(entire font as u32s)
        // Find 'head' offset:
        int headOff = -1;
        for (int i = 0; i < numTables; i++)
            if (tables[i].tag == "head") { headOff = offsets[i]; break; }
        if (headOff >= 0)
        {
            // Zero out the placeholder first (at head + 8)
            WriteU32(output, headOff + 8, 0);
            uint whole = Checksum(output, 0, output.Length);
            uint adjust = 0xB1B0AFBA - whole;
            WriteU32(output, headOff + 8, adjust);
        }
        return output;
    }

    private static int Align4(int n) => (n + 3) & ~3;

    private static int Log2(uint x)
    {
        int r = 0;
        while ((1u << (r + 1)) <= x) r++;
        return r;
    }

    private static uint Checksum(byte[] d, int start, int len)
    {
        uint sum = 0;
        for (int i = 0; i < len; i += 4)
        {
            uint v = 0;
            if (i + 0 < len) v |= (uint)d[start + i + 0] << 24;
            if (i + 1 < len) v |= (uint)d[start + i + 1] << 16;
            if (i + 2 < len) v |= (uint)d[start + i + 2] << 8;
            if (i + 3 < len) v |= (uint)d[start + i + 3];
            sum += v;
        }
        return sum;
    }

    // ---- Table builders ------------------------------------------------

    private static byte[] BuildCmap(Dictionary<int, int> unicodeToGid)
    {
        // cmap with one subtable: Windows (platform 3, encoding 1) format 4.
        // Format 4 segments must be sorted by endCode and end with 0xFFFF segment.
        var pairs = new List<(int uni, int gid)>();
        foreach (var kv in unicodeToGid)
            if (kv.Key > 0 && kv.Key <= 0xFFFF && kv.Value >= 0)
                pairs.Add((kv.Key, kv.Value));
        pairs.Sort((a, b) => a.uni.CompareTo(b.uni));

        // Build segments (contiguous or discontinuous ranges).
        var segStarts = new List<int>();
        var segEnds = new List<int>();
        var segDeltas = new List<int>();
        int i = 0;
        while (i < pairs.Count)
        {
            int start = pairs[i].uni;
            int startGid = pairs[i].gid;
            int end = start;
            int expectedGid = startGid + 1;
            i++;
            while (i < pairs.Count && pairs[i].uni == end + 1 && pairs[i].gid == expectedGid)
            {
                end = pairs[i].uni;
                expectedGid = pairs[i].gid + 1;
                i++;
            }
            segStarts.Add(start);
            segEnds.Add(end);
            segDeltas.Add((startGid - start) & 0xFFFF);
        }
        // Sentinel segment 0xFFFF → 0xFFFF mapped to missing glyph (idDelta=1 so → 0)
        segStarts.Add(0xFFFF);
        segEnds.Add(0xFFFF);
        segDeltas.Add(1);

        int segCount = segStarts.Count;
        int searchRange = 2 * (1 << Log2((uint)segCount));
        int entrySelector = Log2((uint)segCount);
        int rangeShift = 2 * segCount - searchRange;

        // Format 4 size: 14 (header) + 2*segCount*4 + 2 (reservedPad) + 0 (no idRangeOffset use)
        int subtableLen = 16 + 8 * segCount;

        // cmap table: 4 (header) + 8 (encoding record) + subtable
        int totalLen = 4 + 8 + subtableLen;
        byte[] buf = new byte[totalLen];

        // cmap header
        WriteU16(buf, 0, 0); // version
        WriteU16(buf, 2, 1); // numTables
        // Encoding record
        WriteU16(buf, 4, 3); // platformID = Windows
        WriteU16(buf, 6, 1); // encodingID = Unicode BMP
        WriteU32(buf, 8, 12); // offset to subtable

        int p = 12;
        WriteU16(buf, p, 4); p += 2;                  // format 4
        WriteU16(buf, p, (ushort)subtableLen); p += 2; // length
        WriteU16(buf, p, 0); p += 2;                  // language
        WriteU16(buf, p, (ushort)(segCount * 2)); p += 2; // segCountX2
        WriteU16(buf, p, (ushort)searchRange); p += 2;
        WriteU16(buf, p, (ushort)entrySelector); p += 2;
        WriteU16(buf, p, (ushort)rangeShift); p += 2;
        for (int s = 0; s < segCount; s++) { WriteU16(buf, p, (ushort)segEnds[s]); p += 2; }
        WriteU16(buf, p, 0); p += 2; // reservedPad
        for (int s = 0; s < segCount; s++) { WriteU16(buf, p, (ushort)segStarts[s]); p += 2; }
        for (int s = 0; s < segCount; s++) { WriteU16(buf, p, (ushort)segDeltas[s]); p += 2; }
        for (int s = 0; s < segCount; s++) { WriteU16(buf, p, 0); p += 2; } // all idRangeOffsets = 0
        return buf;
    }

    private static byte[] BuildHead(CffInfo info, int numGlyphs)
    {
        byte[] buf = new byte[54];
        // macStyle bit 0 = bold, bit 1 = italic
        ushort macStyle = 0;
        if (info.Weight >= 600) macStyle |= 1;
        if (info.IsItalic)      macStyle |= 2;
        WriteU32(buf, 0, 0x00010000);     // version
        WriteU32(buf, 4, 0x00010000);     // fontRevision
        WriteU32(buf, 8, 0);              // checkSumAdjustment (patched in Assemble)
        WriteU32(buf, 12, 0x5F0F3CF5);    // magicNumber
        WriteU16(buf, 16, 0);             // flags
        WriteU16(buf, 18, (ushort)info.UnitsPerEm); // unitsPerEm
        // created / modified: long date (int64 seconds since 1904). Use 0.
        WriteU64(buf, 20, 0);
        WriteU64(buf, 28, 0);
        WriteI16(buf, 36, (short)info.FontBBox[0]);  // xMin
        WriteI16(buf, 38, (short)info.FontBBox[1]);  // yMin
        WriteI16(buf, 40, (short)info.FontBBox[2]);  // xMax
        WriteI16(buf, 42, (short)info.FontBBox[3]);  // yMax
        WriteU16(buf, 44, macStyle);      // macStyle
        WriteU16(buf, 46, 7);             // lowestRecPPEM
        WriteI16(buf, 48, 2);             // fontDirectionHint (deprecated, 2 = mixed)
        WriteI16(buf, 50, 0);             // indexToLocFormat (short; not used for CFF)
        WriteI16(buf, 52, 0);             // glyphDataFormat
        return buf;
    }

    private static byte[] BuildHhea(CffInfo info, int numGlyphs, int advanceWidthMax)
    {
        byte[] buf = new byte[36];
        WriteU32(buf, 0, 0x00010000);                   // version
        WriteI16(buf, 4, (short)info.FontBBox[3]);      // ascender
        WriteI16(buf, 6, (short)info.FontBBox[1]);      // descender
        WriteI16(buf, 8, 0);                            // lineGap
        WriteU16(buf, 10, (ushort)advanceWidthMax);
        WriteI16(buf, 12, (short)info.FontBBox[0]);     // minLeftSideBearing
        WriteI16(buf, 14, (short)info.FontBBox[0]);     // minRightSideBearing
        WriteI16(buf, 16, (short)info.FontBBox[2]);     // xMaxExtent
        WriteI16(buf, 18, 1);                           // caretSlopeRise
        WriteI16(buf, 20, 0);                           // caretSlopeRun
        WriteI16(buf, 22, 0);                           // caretOffset
        WriteI16(buf, 24, 0); WriteI16(buf, 26, 0);
        WriteI16(buf, 28, 0); WriteI16(buf, 30, 0);
        WriteI16(buf, 32, 0);                           // metricDataFormat
        WriteU16(buf, 34, (ushort)numGlyphs);           // numberOfHMetrics
        return buf;
    }

    private static byte[] BuildMaxp(int numGlyphs)
    {
        byte[] buf = new byte[6];
        WriteU32(buf, 0, 0x00005000);             // version 0.5 (CFF)
        WriteU16(buf, 4, (ushort)numGlyphs);
        return buf;
    }

    private static byte[] BuildName(string fontName)
    {
        // Strip PDF subset prefix ("ABCDEF+")
        string clean = fontName;
        int plus = clean.IndexOf('+');
        if (plus > 0 && plus < clean.Length - 1) clean = clean.Substring(plus + 1);

        // Two names: family (nameID 1) and subfamily (nameID 2).
        // Platform 3 (Windows), encoding 1 (Unicode BMP), language 0x0409 (en-US).
        string family = clean;
        string subfamily = "Regular";

        byte[] familyBytes = Utf16Be(family);
        byte[] subfamilyBytes = Utf16Be(subfamily);

        int recordCount = 4; // family, subfamily, unique, full
        int headerSize = 6 + recordCount * 12;
        int stringsStart = headerSize;

        byte[] uniqueBytes = Utf16Be(family + " " + subfamily);
        byte[] fullBytes = Utf16Be(family);

        int totalStringLen = familyBytes.Length + subfamilyBytes.Length + uniqueBytes.Length + fullBytes.Length;
        byte[] buf = new byte[headerSize + totalStringLen];

        WriteU16(buf, 0, 0);                      // format
        WriteU16(buf, 2, (ushort)recordCount);
        WriteU16(buf, 4, (ushort)stringsStart);

        int offset = 0;
        int p = 6;
        void AddRecord(ushort nameId, byte[] data)
        {
            WriteU16(buf, p, 3);        // platformID Windows
            WriteU16(buf, p + 2, 1);    // encodingID Unicode BMP
            WriteU16(buf, p + 4, 0x0409); // language en-US
            WriteU16(buf, p + 6, nameId);
            WriteU16(buf, p + 8, (ushort)data.Length);
            WriteU16(buf, p + 10, (ushort)offset);
            p += 12;
            Array.Copy(data, 0, buf, stringsStart + offset, data.Length);
            offset += data.Length;
        }
        AddRecord(1, familyBytes);
        AddRecord(2, subfamilyBytes);
        AddRecord(3, uniqueBytes);
        AddRecord(4, fullBytes);
        return buf;
    }

    private static byte[] BuildOS2(CffInfo info)
    {
        // Version 4 OS/2 = 96 bytes
        byte[] buf = new byte[96];
        WriteU16(buf, 0, 4);                                   // version
        WriteI16(buf, 2, 500);                                 // xAvgCharWidth (approx)
        WriteU16(buf, 4, (ushort)Math.Clamp(info.Weight, 100, 900)); // usWeightClass
        WriteU16(buf, 6, 5);                                   // usWidthClass (medium)
        WriteU16(buf, 8, 0);                                   // fsType (installable)
        WriteI16(buf, 10, 650); WriteI16(buf, 12, 1300);       // subscriptX,YSize
        WriteI16(buf, 14, 0); WriteI16(buf, 16, 140);          // subscriptX,YOffset
        WriteI16(buf, 18, 650); WriteI16(buf, 20, 1300);       // superscriptX,YSize
        WriteI16(buf, 22, 0); WriteI16(buf, 24, 477);          // superscriptX,YOffset
        WriteI16(buf, 26, 50); WriteI16(buf, 28, 350);         // strikeoutSize, Position
        WriteI16(buf, 30, 0);                                  // sFamilyClass
        // panose (10 bytes of zeros — acceptable)
        for (int k = 0; k < 10; k++) buf[32 + k] = 0;
        // ulUnicodeRange1..4 (16 bytes) — set BMP basic Latin
        WriteU32(buf, 42, 0x00000001);                         // ulUnicodeRange1: bit 0 = Basic Latin
        WriteU32(buf, 46, 0);
        WriteU32(buf, 50, 0);
        WriteU32(buf, 54, 0);
        // achVendID (4 bytes)
        buf[58] = (byte)'P'; buf[59] = (byte)'D'; buf[60] = (byte)'F'; buf[61] = (byte)' ';
        // fsSelection: bit 0=italic, bit 5=bold, bit 6=regular (if neither italic nor bold)
        ushort fsSel = 0;
        if (info.IsItalic) fsSel |= 0x01;
        if (info.Weight >= 600) fsSel |= 0x20;
        if (fsSel == 0) fsSel = 0x40; // Regular
        WriteU16(buf, 62, fsSel);                              // fsSelection
        WriteU16(buf, 64, 0);                                  // usFirstCharIndex
        WriteU16(buf, 66, 0xFFFF);                             // usLastCharIndex
        WriteI16(buf, 68, (short)info.FontBBox[3]);            // sTypoAscender
        WriteI16(buf, 70, (short)info.FontBBox[1]);            // sTypoDescender
        WriteI16(buf, 72, 0);                                  // sTypoLineGap
        WriteU16(buf, 74, (ushort)info.FontBBox[3]);           // usWinAscent
        WriteU16(buf, 76, (ushort)Math.Abs(info.FontBBox[1])); // usWinDescent
        WriteU32(buf, 78, 1); WriteU32(buf, 82, 0);            // ulCodePageRange1,2 (Latin 1)
        WriteI16(buf, 86, (short)(info.FontBBox[3] / 2));      // sxHeight
        WriteI16(buf, 88, (short)(info.FontBBox[3] * 7 / 10)); // sCapHeight
        WriteU16(buf, 90, 0);                                  // usDefaultChar
        WriteU16(buf, 92, 0x20);                               // usBreakChar (space)
        WriteU16(buf, 94, 1);                                  // usMaxContext
        return buf;
    }

    private static byte[] BuildPost(CffInfo info)
    {
        byte[] buf = new byte[32];
        WriteU32(buf, 0, 0x00030000);                          // version 3.0 (no glyph names)
        WriteU32(buf, 4, (uint)(info.ItalicAngle << 16));      // italicAngle (Fixed)
        WriteI16(buf, 8, (short)info.UnderlinePosition);
        WriteI16(buf, 10, (short)info.UnderlineThickness);
        WriteU32(buf, 12, 0);                                  // isFixedPitch
        WriteU32(buf, 16, 0);                                  // minMemType42
        WriteU32(buf, 20, 0);                                  // maxMemType42
        WriteU32(buf, 24, 0);                                  // minMemType1
        WriteU32(buf, 28, 0);                                  // maxMemType1
        return buf;
    }

    private static byte[] BuildHmtx(int numGlyphs, Dictionary<int, int> widths,
                                     int defaultWidth, out int advanceWidthMax)
    {
        byte[] buf = new byte[numGlyphs * 4];
        advanceWidthMax = defaultWidth;
        for (int gid = 0; gid < numGlyphs; gid++)
        {
            int w = widths.TryGetValue(gid, out int v) ? v : defaultWidth;
            if (w > advanceWidthMax) advanceWidthMax = w;
            WriteU16(buf, gid * 4, (ushort)Math.Clamp(w, 0, 0xFFFF));
            WriteI16(buf, gid * 4 + 2, 0); // lsb
        }
        return buf;
    }

    // ---- Writers -------------------------------------------------------

    private static void WriteU16(byte[] d, int p, ushort v)
    {
        d[p] = (byte)(v >> 8); d[p + 1] = (byte)v;
    }
    private static void WriteI16(byte[] d, int p, short v) => WriteU16(d, p, (ushort)v);
    private static void WriteU32(byte[] d, int p, uint v)
    {
        d[p] = (byte)(v >> 24); d[p + 1] = (byte)(v >> 16);
        d[p + 2] = (byte)(v >> 8); d[p + 3] = (byte)v;
    }
    private static void WriteU64(byte[] d, int p, ulong v)
    {
        WriteU32(d, p, (uint)(v >> 32));
        WriteU32(d, p + 4, (uint)v);
    }
    private static void WriteTag(byte[] d, int p, string tag)
    {
        for (int i = 0; i < 4; i++) d[p + i] = (byte)(i < tag.Length ? tag[i] : ' ');
    }
    private static byte[] Utf16Be(string s)
    {
        byte[] buf = new byte[s.Length * 2];
        for (int i = 0; i < s.Length; i++)
        {
            buf[i * 2] = (byte)(s[i] >> 8);
            buf[i * 2 + 1] = (byte)s[i];
        }
        return buf;
    }
}
