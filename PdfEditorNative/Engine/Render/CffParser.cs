// Minimal CFF (Compact Font Format) parser — extracts just enough metadata
// to wrap the CFF program in an OpenType/CFF container that GDI+ can load.
// Does NOT execute Type2 charstrings (that would be a full interpreter).
using System;

namespace PdfEditorNative.Engine.Render;

internal sealed class CffInfo
{
    public int NumGlyphs;
    public int[] FontBBox = { 0, -250, 1000, 750 };  // xMin, yMin, xMax, yMax (fallback)
    public int UnitsPerEm = 1000;
    public int ItalicAngle;
    public int UnderlinePosition = -100;
    public int UnderlineThickness = 50;
    public int StdHW = 50;
    public string FontName = "PdfEmbedded";
    public bool IsCid;
    public int Weight = 400;
    public bool IsItalic;
    /// <summary>GID → CID (for CID-keyed). For non-CID fonts, this stays empty.</summary>
    public Dictionary<int, int> GidToCid = new();
    /// <summary>CID → GID (inverted). For non-CID fonts this is identity.</summary>
    public Dictionary<int, int> CidToGid = new();
    /// <summary>GID → advance width extracted from Type2 charstrings.</summary>
    public Dictionary<int, int> GidWidths = new();

    // ── Fields needed by the Type 2 charstring interpreter ────────
    /// <summary>Raw Type 2 charstring bytes, indexed by GID.</summary>
    public byte[][] CharStrings = Array.Empty<byte[]>();
    /// <summary>Global subroutines (shared across all fonts in a CFF).</summary>
    public byte[][] GlobalSubrs = Array.Empty<byte[]>();
    /// <summary>Local subroutines for non-CID fonts. Empty for CID.</summary>
    public byte[][] LocalSubrs = Array.Empty<byte[]>();
    /// <summary>Local subroutines per FDArray entry (CID fonts only).</summary>
    public byte[][][] LocalSubrsPerFd = Array.Empty<byte[][]>();
    /// <summary>GID → FD index (CID fonts only). Null for non-CID.</summary>
    public int[]? FdSelect;
    /// <summary>Private DICT default/nominal width (non-CID).</summary>
    public double DefaultWidthX;
    public double NominalWidthX;
    /// <summary>Per-FD default/nominal widths (CID fonts only).</summary>
    public double[]? FdDefaultWidthX;
    public double[]? FdNominalWidthX;
    /// <summary>FontMatrix; default maps font units to em (1000 UPM → 1 em).</summary>
    public double[] FontMatrix = { 0.001, 0, 0, 0.001, 0, 0 };
}

internal static class CffParser
{
    public static CffInfo Parse(byte[] cff)
    {
        var info = new CffInfo();
        if (cff.Length < 4) return info;

        int pos = 0;
        byte hdrSize = cff[2];
        pos = hdrSize;

        // Name INDEX
        string[] names = ReadIndexStrings(cff, ref pos);
        if (names.Length > 0) info.FontName = names[0];

        // Top DICT INDEX
        byte[][] topDicts = ReadIndexData(cff, ref pos);
        if (topDicts.Length == 0) return info;

        // String INDEX
        int stringIdxStart = pos;
        byte[][] strings = ReadIndexData(cff, ref pos);

        // Global Subr INDEX
        info.GlobalSubrs = ReadIndexData(cff, ref pos);

        // Parse Top DICT
        var topDict = ParseDict(topDicts[0]);
        // Detect CID-keyed BEFORE parsing charset (charset format differs for CID vs name-keyed).
        if (topDict.ContainsKey(0x0C1E)) info.IsCid = true;

        int charStringsOffset = 0;
        if (topDict.TryGetValue(17, out var cs))
        {
            charStringsOffset = (int)cs[0];
            if (charStringsOffset < cff.Length - 2)
            {
                int csPos = charStringsOffset;
                info.NumGlyphs = ReadU16BE(cff, csPos);
            }
        }
        // Parse charset (operator 15, default 0 = ISOAdobe predefined)
        int charsetOffset = topDict.TryGetValue(15, out var chsOp) && chsOp.Length > 0 ? (int)chsOp[0] : 0;
        ParseCharset(cff, charsetOffset, info.NumGlyphs, info.IsCid, info.GidToCid);
        // Build inverse CID→GID map
        foreach (var kv in info.GidToCid)
            info.CidToGid[kv.Value] = kv.Key;
        if (topDict.TryGetValue(5, out var bbox) && bbox.Length == 4)
        {
            info.FontBBox[0] = (int)bbox[0];
            info.FontBBox[1] = (int)bbox[1];
            info.FontBBox[2] = (int)bbox[2];
            info.FontBBox[3] = (int)bbox[3];
        }
        if (topDict.TryGetValue(0x0C02, out var italic) && italic.Length > 0)
            info.ItalicAngle = (int)italic[0];
        // FontMatrix (op 0x0C07): typically [0.001 0 0 0.001 0 0]. UPM derives as 1/FontMatrix[0].
        if (topDict.TryGetValue(0x0C07, out var fm) && fm.Length == 6)
        {
            for (int i = 0; i < 6; i++) info.FontMatrix[i] = fm[i];
            if (Math.Abs(info.FontMatrix[0]) > 1e-9)
                info.UnitsPerEm = (int)Math.Round(1.0 / info.FontMatrix[0]);
        }
        if (topDict.TryGetValue(0x0C03, out var uPos) && uPos.Length > 0)
            info.UnderlinePosition = (int)uPos[0];
        if (topDict.TryGetValue(0x0C04, out var uThk) && uThk.Length > 0)
            info.UnderlineThickness = (int)uThk[0];

        // Read CharStrings INDEX (raw bytes per GID).
        if (charStringsOffset > 0 && charStringsOffset < cff.Length)
        {
            int csPos = charStringsOffset;
            try { info.CharStrings = ReadIndexData(cff, ref csPos); }
            catch { info.CharStrings = Array.Empty<byte[]>(); }
        }

        // Read Private DICT + local subrs (non-CID) or FDArray (CID).
        if (info.IsCid && topDict.TryGetValue(0x0C25, out var fdArrOp) && fdArrOp.Length > 0 &&
            topDict.TryGetValue(0x0C24, out var fdSelOp) && fdSelOp.Length > 0)
        {
            int fdArrayOff = (int)fdArrOp[0];
            int p = fdArrayOff;
            byte[][] fdDicts;
            try { fdDicts = ReadIndexData(cff, ref p); }
            catch { fdDicts = Array.Empty<byte[]>(); }
            info.FdDefaultWidthX = new double[fdDicts.Length];
            info.FdNominalWidthX = new double[fdDicts.Length];
            info.LocalSubrsPerFd = new byte[fdDicts.Length][][];
            for (int i = 0; i < fdDicts.Length; i++)
            {
                var fdDict = ParseDict(fdDicts[i]);
                info.LocalSubrsPerFd[i] = Array.Empty<byte[]>();
                if (fdDict.TryGetValue(18, out var fpriv) && fpriv.Length >= 2)
                {
                    int privOffset = (int)fpriv[1];
                    int privSize = (int)fpriv[0];
                    ReadPrivate(cff, privOffset, privSize,
                        out info.FdDefaultWidthX[i], out info.FdNominalWidthX[i],
                        out info.LocalSubrsPerFd[i]);
                }
            }
            info.FdSelect = ParseFdSelect(cff, (int)fdSelOp[0], info.NumGlyphs);
        }
        else if (topDict.TryGetValue(18, out var privOp) && privOp.Length >= 2)
        {
            ReadPrivate(cff, (int)privOp[1], (int)privOp[0],
                out info.DefaultWidthX, out info.NominalWidthX, out info.LocalSubrs);
        }

        // Extract per-GID advance widths directly from Type2 charstrings.
        ExtractGidWidths(info);

        return info;
    }

    // Read a Private DICT and — if it references local subrs — read that INDEX too.
    private static void ReadPrivate(byte[] cff, int offset, int size,
                                     out double defaultW, out double nominalW,
                                     out byte[][] localSubrs)
    {
        defaultW = 0; nominalW = 0; localSubrs = Array.Empty<byte[]>();
        if (offset < 0 || size <= 0 || offset + size > cff.Length) return;
        byte[] priv = new byte[size];
        Array.Copy(cff, offset, priv, 0, size);
        var d = ParseDict(priv);
        if (d.TryGetValue(20, out var dw) && dw.Length > 0) defaultW = dw[0];
        if (d.TryGetValue(21, out var nw) && nw.Length > 0) nominalW = nw[0];
        // Local subrs offset is relative to the Private DICT start.
        if (d.TryGetValue(19, out var subrsOp) && subrsOp.Length > 0)
        {
            int subrsOff = offset + (int)subrsOp[0];
            if (subrsOff >= 0 && subrsOff < cff.Length)
            {
                int p = subrsOff;
                try { localSubrs = ReadIndexData(cff, ref p); }
                catch { localSubrs = Array.Empty<byte[]>(); }
            }
        }
    }

    private static void ExtractGidWidths(CffInfo info)
    {
        for (int gid = 0; gid < info.CharStrings.Length && gid < info.NumGlyphs; gid++)
        {
            double dfl = info.DefaultWidthX, nom = info.NominalWidthX;
            if (info.FdSelect != null && info.FdDefaultWidthX != null &&
                gid < info.FdSelect.Length && info.FdSelect[gid] < info.FdDefaultWidthX.Length)
            {
                dfl = info.FdDefaultWidthX[info.FdSelect[gid]];
                nom = info.FdNominalWidthX![info.FdSelect[gid]];
            }
            info.GidWidths[gid] = ExtractWidthFromCharstring(info.CharStrings[gid], dfl, nom);
        }
    }

    private static int[]? ParseFdSelect(byte[] cff, int offset, int numGlyphs)
    {
        if (offset < 0 || offset >= cff.Length) return null;
        var map = new int[numGlyphs];
        byte format = cff[offset];
        int p = offset + 1;
        if (format == 0)
        {
            for (int g = 0; g < numGlyphs && p < cff.Length; g++) map[g] = cff[p++];
        }
        else if (format == 3)
        {
            int nRanges = ReadU16BE(cff, p); p += 2;
            int prevFirst = 0, prevFd = 0;
            for (int r = 0; r < nRanges; r++)
            {
                int first = ReadU16BE(cff, p); p += 2;
                int fd = cff[p++];
                if (r > 0)
                    for (int g = prevFirst; g < first && g < numGlyphs; g++) map[g] = prevFd;
                prevFirst = first; prevFd = fd;
            }
            int sentinel = ReadU16BE(cff, p);
            for (int g = prevFirst; g < sentinel && g < numGlyphs; g++) map[g] = prevFd;
        }
        return map;
    }

    // Extract advance width from a Type2 charstring.
    // Width is present as first operand if operand count before first operator has specific
    // parity (per the operator's spec). Returns nominalW + width, or defaultW if no width.
    private static int ExtractWidthFromCharstring(byte[] cs, double defaultW, double nominalW)
    {
        if (cs == null || cs.Length == 0) return (int)defaultW;
        int i = 0;
        var operands = new List<double>();
        while (i < cs.Length)
        {
            byte b = cs[i];
            if (b <= 31)
            {
                int op = b; i++;
                if (b == 12 && i < cs.Length) { op = (12 << 8) | cs[i]; i++; }
                // Width present if operand count is: odd for stems (1,3,18,23);
                // (baseArgs+1) for moveto family (4=vmoveto=1, 22=hmoveto=1, 21=rmoveto=2)
                // and endchar (14=0).
                switch (op)
                {
                    case 1: case 3: case 18: case 23:  // h/v stems (pairs)
                        if ((operands.Count & 1) == 1) return (int)(nominalW + operands[0]);
                        break;
                    case 4: case 22:  // vmoveto, hmoveto (1 arg)
                        if (operands.Count == 2) return (int)(nominalW + operands[0]);
                        break;
                    case 21:  // rmoveto (2 args)
                        if (operands.Count == 3) return (int)(nominalW + operands[0]);
                        break;
                    case 14:  // endchar
                        if (operands.Count == 1) return (int)(nominalW + operands[0]);
                        break;
                }
                return (int)defaultW;
            }
            else
            {
                if (b >= 32 && b <= 246) { operands.Add(b - 139); i++; }
                else if (b >= 247 && b <= 250 && i + 1 < cs.Length)
                { operands.Add((b - 247) * 256 + cs[i + 1] + 108); i += 2; }
                else if (b >= 251 && b <= 254 && i + 1 < cs.Length)
                { operands.Add(-(b - 251) * 256 - cs[i + 1] - 108); i += 2; }
                else if (b == 28 && i + 2 < cs.Length)
                { operands.Add((short)((cs[i + 1] << 8) | cs[i + 2])); i += 3; }
                else if (b == 255 && i + 4 < cs.Length)
                {
                    int v = (cs[i+1]<<24)|(cs[i+2]<<16)|(cs[i+3]<<8)|cs[i+4];
                    operands.Add(v / 65536.0);
                    i += 5;
                }
                else i++;
            }
        }
        return (int)defaultW;
    }

    // Parse CFF charset table. Fills gidToCid for GID 1..numGlyphs-1 (GID 0 always = CID 0).
    // Predefined charsets (offset 0/1/2) only valid for name-keyed fonts; for CID-keyed
    // the charset offset always points into the CFF data.
    private static void ParseCharset(byte[] cff, int offset, int numGlyphs, bool isCid,
                                      Dictionary<int, int> gidToCid)
    {
        if (numGlyphs <= 0) return;
        gidToCid[0] = 0;  // always
        if (numGlyphs <= 1) return;

        if (offset < 3)
        {
            // Predefined charset — only for name-keyed. For CID-keyed, Identity is common;
            // fall back to GID == CID.
            for (int g = 1; g < numGlyphs; g++) gidToCid[g] = g;
            return;
        }
        if (offset >= cff.Length) return;

        byte format = cff[offset];
        int p = offset + 1;
        int gid = 1;
        try
        {
            if (format == 0)
            {
                // Array of numGlyphs-1 u16 CIDs
                while (gid < numGlyphs && p + 1 < cff.Length)
                {
                    gidToCid[gid++] = ReadU16BE(cff, p); p += 2;
                }
            }
            else if (format == 1)
            {
                // Ranges: {first u16, nLeft u8}
                while (gid < numGlyphs && p + 2 < cff.Length)
                {
                    int first = ReadU16BE(cff, p); p += 2;
                    int nLeft = cff[p]; p += 1;
                    for (int i = 0; i <= nLeft && gid < numGlyphs; i++)
                        gidToCid[gid++] = first + i;
                }
            }
            else if (format == 2)
            {
                // Ranges: {first u16, nLeft u16}
                while (gid < numGlyphs && p + 3 < cff.Length)
                {
                    int first = ReadU16BE(cff, p); p += 2;
                    int nLeft = ReadU16BE(cff, p); p += 2;
                    for (int i = 0; i <= nLeft && gid < numGlyphs; i++)
                        gidToCid[gid++] = first + i;
                }
            }
        }
        catch { }
        // Fill remaining as identity if parse was short
        while (gid < numGlyphs) { gidToCid[gid] = gid; gid++; }
    }

    private static byte[][] ReadIndexData(byte[] d, ref int pos)
    {
        if (pos + 2 > d.Length) return Array.Empty<byte[]>();
        int count = ReadU16BE(d, pos); pos += 2;
        if (count == 0) return Array.Empty<byte[]>();
        byte offSize = d[pos++];
        int[] offsets = new int[count + 1];
        for (int i = 0; i <= count; i++)
        {
            offsets[i] = ReadOffset(d, pos, offSize);
            pos += offSize;
        }
        int dataStart = pos - 1; // offsets are 1-based from here
        var result = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            int len = offsets[i + 1] - offsets[i];
            result[i] = new byte[len];
            Array.Copy(d, dataStart + offsets[i], result[i], 0, len);
        }
        pos = dataStart + offsets[count];
        return result;
    }

    private static string[] ReadIndexStrings(byte[] d, ref int pos)
    {
        var data = ReadIndexData(d, ref pos);
        var result = new string[data.Length];
        for (int i = 0; i < data.Length; i++)
            result[i] = System.Text.Encoding.ASCII.GetString(data[i]);
        return result;
    }

    private static void SkipIndex(byte[] d, ref int pos)
    {
        if (pos + 2 > d.Length) return;
        int count = ReadU16BE(d, pos); pos += 2;
        if (count == 0) return;
        byte offSize = d[pos++];
        int lastOff = ReadOffset(d, pos + offSize * count, offSize);
        pos += offSize * (count + 1);
        pos += lastOff - 1;
    }

    private static int ReadU16BE(byte[] d, int p) => (d[p] << 8) | d[p + 1];
    private static int ReadU32BE(byte[] d, int p) => (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];

    private static int ReadOffset(byte[] d, int p, int size) => size switch
    {
        1 => d[p],
        2 => (d[p] << 8) | d[p + 1],
        3 => (d[p] << 16) | (d[p + 1] << 8) | d[p + 2],
        4 => (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3],
        _ => 0
    };

    // Parse a CFF DICT: returns operator → array of operands.
    private static Dictionary<int, double[]> ParseDict(byte[] d)
    {
        var result = new Dictionary<int, double[]>();
        var operands = new List<double>();
        int i = 0;
        while (i < d.Length)
        {
            byte b0 = d[i];
            if (b0 <= 21)
            {
                // Operator
                int op = b0;
                i++;
                if (b0 == 12 && i < d.Length)
                {
                    op = (12 << 8) | d[i];
                    i++;
                }
                result[op] = operands.ToArray();
                operands.Clear();
            }
            else
            {
                // Operand (number)
                double val = 0;
                if (b0 >= 32 && b0 <= 246) { val = b0 - 139; i++; }
                else if (b0 >= 247 && b0 <= 250 && i + 1 < d.Length)
                { val = (b0 - 247) * 256 + d[i + 1] + 108; i += 2; }
                else if (b0 >= 251 && b0 <= 254 && i + 1 < d.Length)
                { val = -(b0 - 251) * 256 - d[i + 1] - 108; i += 2; }
                else if (b0 == 28 && i + 2 < d.Length)
                { val = (short)((d[i + 1] << 8) | d[i + 2]); i += 3; }
                else if (b0 == 29 && i + 4 < d.Length)
                { val = (int)((d[i + 1] << 24) | (d[i + 2] << 16) | (d[i + 3] << 8) | d[i + 4]); i += 5; }
                else if (b0 == 30)
                {
                    // Real number — skip nibbles until terminator nibble 0xF
                    i++;
                    while (i < d.Length)
                    {
                        byte bb = d[i++];
                        if ((bb & 0x0F) == 0x0F || (bb & 0xF0) == 0xF0) break;
                    }
                    val = 0;
                }
                else { i++; }
                operands.Add(val);
            }
        }
        return result;
    }
}
