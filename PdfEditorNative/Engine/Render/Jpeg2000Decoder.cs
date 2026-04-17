using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PdfEditorNative.Engine.Render;

internal static class Jpeg2000Decoder
{
    /// <summary>Decode JPEG 2000 data (JP2 container or raw codestream) to a Bitmap.</summary>
    internal static Bitmap? Decode(byte[] data)
    {
        try
        {
            var cs = ExtractCodestream(data);
            if (cs == null) return null;
            return DecodeCodestream(cs);
        }
        catch { return null; }
    }

    // --- JP2 container / raw codestream extraction ---

    static byte[]? ExtractCodestream(byte[] data)
    {
        if (data.Length < 2) return null;
        if (data[0] == 0xFF && data[1] == 0x4F) return data;
        if (data.Length >= 12 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 0x0C
            && data[4] == 0x6A && data[5] == 0x50 && data[6] == 0x20 && data[7] == 0x20)
        {
            int pos = 0;
            while (pos + 8 <= data.Length)
            {
                long boxLen = Ru32(data, pos);
                uint boxType = Ru32(data, pos + 4);
                int hdrLen = 8;
                if (boxLen == 1 && pos + 16 <= data.Length)
                {
                    boxLen = ((long)Ru32(data, pos + 8) << 32) | Ru32(data, pos + 12);
                    hdrLen = 16;
                }
                if (boxLen == 0) boxLen = data.Length - pos;
                if (boxLen < hdrLen || pos + boxLen > data.Length) break;
                if (boxType == 0x6A703263) // "jp2c"
                {
                    int cStart = pos + hdrLen;
                    int cLen = (int)(boxLen - hdrLen);
                    var cs = new byte[cLen];
                    Buffer.BlockCopy(data, cStart, cs, 0, cLen);
                    return cs;
                }
                pos += (int)boxLen;
            }
        }
        return null;
    }

    static int R16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    static uint Ru32(byte[] d, int o) => ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
    static int R32(byte[] d, int o) => (int)Ru32(d, o);
    static int CeilDiv(int a, int b) => b <= 0 ? a : (a + b - 1) / b;

    // --- Marker data structures ---

    sealed class SizInfo
    {
        public int W, H, XO, YO, TW, TH, TXO, TYO, Csiz;
        public int[] Depth = [], XR = [], YR = [];
        public bool[] Signed = [];
    }

    sealed class CodInfo
    {
        public int Scod, ProgOrder, NumLayers, Mct, NL, CbW, CbH, CbStyle, Wavelet;
    }

    sealed class QcdInfo
    {
        public int Style, Guard;
        public (int exp, int man)[] Steps = [];
    }

    // --- Main codestream decoder ---

    static Bitmap? DecodeCodestream(byte[] d)
    {
        var siz = new SizInfo();
        var cod = new CodInfo();
        var qcd = new QcdInfo();
        var tileParts = new List<(int idx, byte[] data)>();
        int p = 0;
        if (p + 2 > d.Length || R16(d, p) != 0xFF4F) return null;
        p += 2; // SOC

        while (p + 2 <= d.Length)
        {
            int mk = R16(d, p); p += 2;
            if (mk == 0xFFD9) break; // EOC

            if (mk == 0xFF90) // SOT
            {
                if (p + 2 > d.Length) return null;
                int mlen = R16(d, p);
                if (p + mlen > d.Length) return null;
                int tileIdx = R16(d, p + 2);
                int psot = R32(d, p + 4);

                // Find SOD
                int sodSearch = p + mlen;
                while (sodSearch + 2 <= d.Length)
                {
                    int smk = R16(d, sodSearch);
                    if (smk == 0xFF93) { sodSearch += 2; break; }
                    if (smk >= 0xFF00 && smk != 0xFF00 && smk < 0xFF90)
                    {
                        // marker segment before SOD — skip it
                        if (sodSearch + 4 <= d.Length) { int sl = R16(d, sodSearch + 2); sodSearch += 2 + sl; }
                        else sodSearch++;
                    }
                    else sodSearch++;
                }

                int dataEnd;
                if (psot > 0) dataEnd = (p - 2) + psot;
                else
                {
                    dataEnd = d.Length;
                    if (dataEnd >= 2 && R16(d, dataEnd - 2) == 0xFFD9) dataEnd -= 2;
                }
                if (dataEnd > d.Length) dataEnd = d.Length;

                int tlen = dataEnd - sodSearch;
                if (tlen < 0) tlen = 0;
                var td = new byte[tlen];
                if (tlen > 0) Buffer.BlockCopy(d, sodSearch, td, 0, tlen);
                tileParts.Add((tileIdx, td));
                p = dataEnd;
                continue;
            }

            if (mk >= 0xFF30 && mk <= 0xFF3F) continue; // no-body markers

            if (p + 2 > d.Length) return null;
            int len = R16(d, p);
            int end = p + len;
            if (end > d.Length) return null;

            switch (mk)
            {
                case 0xFF51: ParseSiz(d, p + 2, siz); break;
                case 0xFF52: ParseCod(d, p + 2, end, cod); break;
                case 0xFF5C: ParseQcd(d, p + 2, end, qcd, cod.NL); break;
            }
            p = end;
        }

        if (siz.Csiz == 0 || siz.W <= siz.XO || siz.H <= siz.YO) return null;
        int imgW = siz.W - siz.XO, imgH = siz.H - siz.YO;

        var result = new float[siz.Csiz][];
        for (int c = 0; c < siz.Csiz; c++) result[c] = new float[imgW * imgH];

        // Group tile-parts by tile index
        var tileMap = new Dictionary<int, List<byte[]>>();
        foreach (var (idx, td) in tileParts)
        {
            if (!tileMap.TryGetValue(idx, out var list)) { list = new List<byte[]>(); tileMap[idx] = list; }
            list.Add(td);
        }

        int ntx = CeilDiv(siz.W - siz.TXO, siz.TW);
        int nty = CeilDiv(siz.H - siz.TYO, siz.TH);

        for (int ty = 0; ty < nty; ty++)
        for (int tx = 0; tx < ntx; tx++)
        {
            int ti = ty * ntx + tx;
            int tx0 = Math.Max(siz.TXO + tx * siz.TW, siz.XO);
            int ty0 = Math.Max(siz.TYO + ty * siz.TH, siz.YO);
            int tx1 = Math.Min(siz.TXO + (tx + 1) * siz.TW, siz.W);
            int ty1 = Math.Min(siz.TYO + (ty + 1) * siz.TH, siz.H);
            if (tx1 <= tx0 || ty1 <= ty0) continue;
            if (!tileMap.TryGetValue(ti, out var parts)) continue;

            int total = 0;
            foreach (var pp in parts) total += pp.Length;
            var tileData = new byte[total];
            int off = 0;
            foreach (var pp in parts) { Buffer.BlockCopy(pp, 0, tileData, off, pp.Length); off += pp.Length; }

            DecodeTile(tileData, tx0, ty0, tx1, ty1, siz, cod, qcd, result, imgW);
        }

        ApplyMCT(result, imgW, imgH, cod.Mct, cod.Wavelet);
        // DC level shift applied after inverse MCT (T.800 G.1.1)
        for (int c = 0; c < siz.Csiz; c++)
        {
            if (!siz.Signed[c])
            {
                float dc = 1 << (siz.Depth[c] - 1);
                for (int i = 0; i < result[c].Length; i++) result[c][i] += dc;
            }
        }
        return BuildBitmap(result, imgW, imgH, siz);
    }

    static void ParseSiz(byte[] d, int p, SizInfo s)
    {
        p += 2; // Rsiz
        s.W = R32(d, p); p += 4; s.H = R32(d, p); p += 4;
        s.XO = R32(d, p); p += 4; s.YO = R32(d, p); p += 4;
        s.TW = R32(d, p); p += 4; s.TH = R32(d, p); p += 4;
        s.TXO = R32(d, p); p += 4; s.TYO = R32(d, p); p += 4;
        s.Csiz = R16(d, p); p += 2;
        s.Depth = new int[s.Csiz]; s.Signed = new bool[s.Csiz];
        s.XR = new int[s.Csiz]; s.YR = new int[s.Csiz];
        for (int i = 0; i < s.Csiz; i++)
        {
            int v = d[p++]; s.Signed[i] = (v & 0x80) != 0; s.Depth[i] = (v & 0x7F) + 1;
            s.XR[i] = d[p++]; s.YR[i] = d[p++];
        }
    }

    static void ParseCod(byte[] d, int p, int end, CodInfo c)
    {
        c.Scod = d[p++]; c.ProgOrder = d[p++];
        c.NumLayers = R16(d, p); p += 2;
        c.Mct = d[p++]; c.NL = d[p++];
        c.CbW = 1 << (d[p++] + 2); c.CbH = 1 << (d[p++] + 2);
        c.CbStyle = d[p++]; c.Wavelet = d[p++];
    }

    static void ParseQcd(byte[] d, int p, int end, QcdInfo q, int nl)
    {
        int sqcd = d[p++]; q.Style = sqcd & 0x1F; q.Guard = sqcd >> 5;
        int nb = 1 + 3 * nl;
        q.Steps = new (int, int)[nb];
        if (q.Style == 0)
            for (int i = 0; i < nb && p < end; i++) { q.Steps[i] = (d[p] >> 3, 0); p++; }
        else if (q.Style == 1)
        {
            int v = R16(d, p); p += 2;
            q.Steps[0] = (v >> 11, v & 0x7FF);
            for (int i = 1; i < nb; i++)
                q.Steps[i] = (Math.Max(q.Steps[0].exp - ((i - 1) / 3 + 1), 0), q.Steps[0].man);
        }
        else
            for (int i = 0; i < nb && p + 1 < end; i++) { int v = R16(d, p); p += 2; q.Steps[i] = (v >> 11, v & 0x7FF); }
    }

    // --- Tile structures ---

    sealed class CBlk
    {
        public int X0, Y0, W, H, SubIdx;
        public int ZeroBp, LenBits = 3;
        public bool Included;
        public List<(int passes, int dataOff, int dataLen)> Segs = new();
    }

    // One per subband within a precinct (with default precincts, one per subband per resolution)
    sealed class PrecBand
    {
        public CBlk[] Blks = [];
        public TagTree Incl = null!, Zbp = null!;
        public int NCbX, NCbY;
    }

    // One per resolution level
    sealed class ResLevel
    {
        public PrecBand[] Bands = [];
    }

    struct SbInfo
    {
        public int W, H, BandIdx, Type; // Type: 0=LL,1=HL,2=LH,3=HH
    }

    // --- Tile decoding ---

    static void DecodeTile(byte[] data, int tx0, int ty0, int tx1, int ty1,
        SizInfo siz, CodInfo cod, QcdInfo qcd, float[][] result, int imgW)
    {
        int nl = cod.NL;
        int imgH = siz.H - siz.YO;

        // Build subbands and precincts for all components, parse packets, decode
        // We parse packets once for all components together (they're interleaved)
        var compSubs = new SbInfo[siz.Csiz][];
        var compRes = new ResLevel[siz.Csiz][];
        var compTcW = new int[siz.Csiz];
        var compTcH = new int[siz.Csiz];

        for (int c = 0; c < siz.Csiz; c++)
        {
            int tcx0 = CeilDiv(tx0, siz.XR[c]), tcy0 = CeilDiv(ty0, siz.YR[c]);
            int tcx1 = CeilDiv(tx1, siz.XR[c]), tcy1 = CeilDiv(ty1, siz.YR[c]);
            compTcW[c] = tcx1 - tcx0; compTcH[c] = tcy1 - tcy0;
            compSubs[c] = BuildSubbands(tcx0, tcy0, tcx1, tcy1, nl);
            compRes[c] = BuildResLevels(compSubs[c], cod, nl);
        }

        // Parse all packets in progression order
        ParseAllPackets(data, compRes, cod, siz.Csiz, nl);

        // Decode each component
        for (int c = 0; c < siz.Csiz; c++)
        {
            int tcW = compTcW[c], tcH = compTcH[c];
            if (tcW <= 0 || tcH <= 0) continue;
            int tcx0 = CeilDiv(tx0, siz.XR[c]), tcy0 = CeilDiv(ty0, siz.YR[c]);
            var coeffs = new float[tcW * tcH];
            DecodeAndPlaceBlocks(data, compRes[c], compSubs[c], coeffs, tcW, tcH, qcd, cod, siz.Depth[c], tcx0, tcy0);
            InverseDWT(coeffs, tcW, tcH, nl, cod.Wavelet, tcx0, tcy0);
            for (int y = 0; y < tcH; y++)
            for (int x = 0; x < tcW; x++)
            {
                int ix = (tcx0 + x) * siz.XR[c] - siz.XO;
                int iy = (tcy0 + y) * siz.YR[c] - siz.YO;
                if ((uint)ix < (uint)imgW && (uint)iy < (uint)imgH)
                    result[c][iy * imgW + ix] = coeffs[y * tcW + x];
            }
        }
    }

    static SbInfo[] BuildSubbands(int tcx0, int tcy0, int tcx1, int tcy1, int nl)
    {
        var list = new List<SbInfo>();
        // Resolution 0: LL band
        {
            int d = 1 << nl;
            int w = CeilDiv(tcx1, d) - CeilDiv(tcx0, d);
            int h = CeilDiv(tcy1, d) - CeilDiv(tcy0, d);
            list.Add(new SbInfo { W = Math.Max(w, 0), H = Math.Max(h, 0), BandIdx = 0, Type = 0 });
        }
        // Resolutions 1..NL: HL, LH, HH bands
        for (int r = 1; r <= nl; r++)
        {
            int d = nl - r + 1;
            int s = 1 << d, s2 = 1 << (d - 1);
            int bi = 1 + 3 * (r - 1);
            // HL
            int hlW = CeilDiv(tcx1, s2) - CeilDiv(tcx0, s2) - (CeilDiv(tcx1, s) - CeilDiv(tcx0, s));
            int hlH = CeilDiv(tcy1, s) - CeilDiv(tcy0, s);
            list.Add(new SbInfo { W = Math.Max(hlW, 0), H = Math.Max(hlH, 0), BandIdx = bi, Type = 1 });
            // LH
            int lhW = CeilDiv(tcx1, s) - CeilDiv(tcx0, s);
            int lhH = CeilDiv(tcy1, s2) - CeilDiv(tcy0, s2) - (CeilDiv(tcy1, s) - CeilDiv(tcy0, s));
            list.Add(new SbInfo { W = Math.Max(lhW, 0), H = Math.Max(lhH, 0), BandIdx = bi + 1, Type = 2 });
            // HH
            int hhW = CeilDiv(tcx1, s2) - CeilDiv(tcx0, s2) - (CeilDiv(tcx1, s) - CeilDiv(tcx0, s));
            int hhH = CeilDiv(tcy1, s2) - CeilDiv(tcy0, s2) - (CeilDiv(tcy1, s) - CeilDiv(tcy0, s));
            list.Add(new SbInfo { W = Math.Max(hhW, 0), H = Math.Max(hhH, 0), BandIdx = bi + 2, Type = 3 });
        }
        return list.ToArray();
    }

    static ResLevel[] BuildResLevels(SbInfo[] subs, CodInfo cod, int nl)
    {
        var levels = new ResLevel[nl + 1];
        for (int r = 0; r <= nl; r++)
        {
            int sbStart, sbEnd;
            if (r == 0) { sbStart = 0; sbEnd = 1; }
            else { sbStart = 1 + 3 * (r - 1); sbEnd = sbStart + 3; }

            var bands = new List<PrecBand>();
            for (int si = sbStart; si < sbEnd && si < subs.Length; si++)
            {
                var sb = subs[si];
                if (sb.W <= 0 || sb.H <= 0)
                {
                    bands.Add(new PrecBand { NCbX = 1, NCbY = 1, Incl = new TagTree(1, 1), Zbp = new TagTree(1, 1) });
                    continue;
                }
                int ncbx = CeilDiv(sb.W, cod.CbW), ncby = CeilDiv(sb.H, cod.CbH);
                var blks = new CBlk[ncbx * ncby];
                for (int cby = 0; cby < ncby; cby++)
                for (int cbx = 0; cbx < ncbx; cbx++)
                {
                    int cx0 = cbx * cod.CbW, cy0 = cby * cod.CbH;
                    blks[cby * ncbx + cbx] = new CBlk
                    {
                        X0 = cx0, Y0 = cy0,
                        W = Math.Min(cod.CbW, sb.W - cx0),
                        H = Math.Min(cod.CbH, sb.H - cy0),
                        SubIdx = si
                    };
                }
                bands.Add(new PrecBand
                {
                    Blks = blks, NCbX = ncbx, NCbY = ncby,
                    Incl = new TagTree(ncbx, ncby), Zbp = new TagTree(ncbx, ncby)
                });
            }
            levels[r] = new ResLevel { Bands = bands.ToArray() };
        }
        return levels;
    }

    // --- Tag Tree ---

    sealed class TagTree
    {
        readonly int[] _w, _val, _state;
        readonly int[] _off;
        readonly int _nLevels;

        public TagTree(int w, int h)
        {
            w = Math.Max(w, 1); h = Math.Max(h, 1);
            var ws = new List<int>(); var hs = new List<int>();
            int cw = w, ch = h;
            while (true)
            {
                ws.Add(cw); hs.Add(ch);
                if (cw == 1 && ch == 1) break;
                cw = CeilDiv(cw, 2); ch = CeilDiv(ch, 2);
            }
            _nLevels = ws.Count;
            _w = new int[_nLevels];
            _off = new int[_nLevels];
            int total = 0;
            for (int i = 0; i < _nLevels; i++) { _off[i] = total; _w[i] = ws[i]; total += ws[i] * hs[i]; }
            _val = new int[total];
            _state = new int[total];
        }

        public int Decode(BitReader br, int leafX, int leafY, int threshold)
        {
            Span<int> stack = stackalloc int[_nLevels];
            int cx = leafX, cy = leafY;
            for (int l = 0; l < _nLevels; l++)
            {
                stack[l] = _off[l] + cy * _w[l] + cx;
                cx >>= 1; cy >>= 1;
            }

            int low = 0;
            for (int l = _nLevels - 1; l >= 0; l--)
            {
                int idx = stack[l];
                if (idx < 0 || idx >= _val.Length) continue;
                if (_state[idx] < low) _state[idx] = low;
                while (_state[idx] < threshold)
                {
                    if (br.ReadBit() != 0) { _val[idx] = _state[idx]; _state[idx] = int.MaxValue; break; }
                    _state[idx]++;
                }
                low = _state[idx];
                if (low >= int.MaxValue) low = _val[idx];
            }
            int leafIdx = _off[0] + leafY * _w[0] + leafX;
            if (leafIdx < 0 || leafIdx >= _val.Length) return 0;
            return _state[leafIdx] >= int.MaxValue ? _val[leafIdx] : _state[leafIdx];
        }
    }

    // --- Bit Reader (for packet headers, with byte-stuffing after 0xFF) ---

    sealed class BitReader
    {
        readonly byte[] _d;
        int _pos;
        readonly int _end;
        int _buf, _bits;
        bool _lastFF;

        public BitReader(byte[] d, int start, int end)
        {
            _d = d; _pos = start; _end = end; _bits = 0; _buf = 0; _lastFF = false;
        }

        void Fill()
        {
            if (_pos >= _end) { _buf = 0; _bits = 8; return; }
            _buf = _d[_pos++];
            _bits = _lastFF ? 7 : 8;
            _lastFF = _buf == 0xFF;
        }

        public int ReadBit()
        {
            if (_bits == 0) Fill();
            _bits--;
            return (_buf >> _bits) & 1;
        }

        public int ReadBits(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++) v = (v << 1) | ReadBit();
            return v;
        }

        public int Pos => _pos;
        public void AlignByte() { _bits = 0; _lastFF = false; }
    }

    // --- Tier 2: Packet parsing ---

    static void ParseAllPackets(byte[] data, ResLevel[][] compRes, CodInfo cod, int nComp, int nl)
    {
        int pos = 0;
        bool sop = (cod.Scod & 2) != 0;
        bool eph = (cod.Scod & 4) != 0;

        // LRCP (and fallback for all progression orders)
        for (int lay = 0; lay < cod.NumLayers; lay++)
        for (int r = 0; r <= nl; r++)
        for (int c = 0; c < nComp; c++)
        {
            if (pos >= data.Length) return;
            pos = ParseOnePacket(data, pos, compRes[c][r], lay, sop, eph);
        }
    }

    static int ParseOnePacket(byte[] data, int pos, ResLevel res, int layer, bool sop, bool eph)
    {
        if (pos >= data.Length) return pos;
        if (sop && pos + 5 < data.Length && data[pos] == 0xFF && data[pos + 1] == 0x91) pos += 6;

        var br = new BitReader(data, pos, data.Length);
        int nonEmpty = br.ReadBit();
        if (nonEmpty == 0)
        {
            br.AlignByte(); pos = br.Pos;
            if (eph && pos + 1 < data.Length && data[pos] == 0xFF && data[pos + 1] == 0x92) pos += 2;
            return pos;
        }

        // Collect all included code-blocks across all sub-bands of this precinct
        var inclList = new List<(PrecBand band, int blkIdx, int np, int len)>();

        foreach (var band in res.Bands)
        {
            for (int i = 0; i < band.Blks.Length; i++)
            {
                var cb = band.Blks[i];
                bool firstTime = !cb.Included;
                bool incl;
                if (firstTime)
                {
                    int bx = i % band.NCbX, by = i / band.NCbX;
                    int val = band.Incl.Decode(br, bx, by, layer + 1);
                    incl = val <= layer;
                }
                else
                    incl = br.ReadBit() != 0;

                if (!incl) continue;

                if (firstTime)
                {
                    cb.Included = true;
                    int bx = i % band.NCbX, by = i / band.NCbX;
                    int thr = 1;
                    while (band.Zbp.Decode(br, bx, by, thr) >= thr) thr++;
                    cb.ZeroBp = thr - 1;
                }

                int np = ReadNumPasses(br);
                int dLenBits = 0;
                while (br.ReadBit() != 0) dLenBits++;
                cb.LenBits += dLenBits;

                int bitsNeeded = cb.LenBits + FloorLog2(np);
                int byteLen = br.ReadBits(bitsNeeded);
                inclList.Add((band, i, np, byteLen));
            }
        }

        br.AlignByte(); pos = br.Pos;
        if (eph && pos + 1 < data.Length && data[pos] == 0xFF && data[pos + 1] == 0x92) pos += 2;

        // Read code-block data
        foreach (var (band, blkIdx, np, len) in inclList)
        {
            int actual = Math.Min(len, data.Length - pos);
            band.Blks[blkIdx].Segs.Add((np, pos, actual));
            pos += len;
        }
        return pos;
    }

    static int ReadNumPasses(BitReader br)
    {
        if (br.ReadBit() == 0) return 1;
        if (br.ReadBit() == 0) return 2;
        int n = br.ReadBits(2);
        if (n != 3) return 3 + n;
        n = br.ReadBits(5);
        if (n != 31) return 6 + n;
        return 37 + br.ReadBits(7);
    }

    static int FloorLog2(int v) { int r = 0; while ((1 << (r + 1)) <= v) r++; return r; }

    // --- Tier 1: MQ Arithmetic Decoder ---

    static readonly (int qe, int nmps, int nlps, int sw)[] MqTbl = {
        (0x5601,1,1,1),(0x3401,2,6,0),(0x1801,3,9,0),(0x0AC1,4,12,0),(0x0521,5,29,0),
        (0x0221,38,33,0),(0x5601,7,6,1),(0x5401,8,14,0),(0x4801,9,14,0),(0x3801,10,14,0),
        (0x3001,11,17,0),(0x2401,12,18,0),(0x1C01,13,20,0),(0x1601,29,21,0),(0x5601,15,14,1),
        (0x5401,16,14,0),(0x5101,17,15,0),(0x4801,18,16,0),(0x3801,19,17,0),(0x3401,20,18,0),
        (0x3001,21,19,0),(0x2801,22,19,0),(0x2401,23,20,0),(0x2201,24,21,0),(0x1C01,25,22,0),
        (0x1801,26,23,0),(0x1601,27,24,0),(0x1401,28,25,0),(0x1201,29,26,0),(0x1101,30,27,0),
        (0x0AC1,31,28,0),(0x09C1,32,29,0),(0x08A1,33,30,0),(0x0521,34,31,0),(0x0441,35,32,0),
        (0x02A1,36,33,0),(0x0221,37,34,0),(0x0141,38,35,0),(0x0111,39,36,0),(0x0085,40,37,0),
        (0x0049,41,38,0),(0x0025,42,39,0),(0x0015,43,40,0),(0x0009,44,41,0),(0x0005,45,42,0),
        (0x0001,45,43,0),(0x5601,46,46,0)
    };

    sealed class MqDec
    {
        uint _a, _c; int _ct;
        readonly byte[] _d; int _bp; readonly int _end; readonly int _start;
        readonly int[] _st = new int[19]; // context states
        readonly int[] _mps = new int[19]; // MPS values

        public MqDec(byte[] data, int start, int len)
        {
            _d = data; _bp = start; _end = start + len; _start = start;
            _st[0] = 4;   // ZC[0] (OpenJPEG convention)
            _st[17] = 3;  // run-length context (T.800 Table D.7)
            _st[18] = 46; // uniform context (T.800 Table D.7)
            if (_bp < _end) { _c = (uint)(_d[_bp] << 16); _bp++; } else { _c = 0; }
            ByteIn();
            _c <<= 7;
            _ct -= 7;
            _a = 0x8000;
        }

        void ByteIn()
        {
            if (_bp >= _end) { _c += 0xFF00; _ct = 8; return; }
            byte cur = _d[_bp];
            // Previous byte: the one just before current _bp
            byte prev = (_bp > _start) ? _d[_bp - 1] : (byte)0;
            if (prev == 0xFF)
            {
                if (cur > 0x8F) { _c += 0xFF00; _ct = 8; return; }
                _bp++; _c += (uint)(cur << 9); _ct = 7;
            }
            else
            {
                _bp++; _c += (uint)(cur << 8); _ct = 8;
            }
        }

        void Renorm()
        {
            do
            {
                if (_ct == 0) ByteIn();
                _a <<= 1; _c <<= 1; _ct--;
            } while (_a < 0x8000);
        }

        public int Decode(int cx)
        {
            var e = MqTbl[_st[cx]];
            uint qe = (uint)e.qe;
            _a -= qe;
            int d;
            if ((_c >> 16) < qe)
            {
                // LPS sub-interval (C.high < Qe)
                if (_a < qe) { d = _mps[cx]; _st[cx] = e.nmps; }
                else { d = 1 - _mps[cx]; if (e.sw != 0) _mps[cx] ^= 1; _st[cx] = e.nlps; }
                _a = qe; Renorm();
            }
            else
            {
                // MPS sub-interval (C.high >= Qe)
                _c -= qe << 16;
                if (_a < 0x8000)
                {
                    if (_a < qe) { d = 1 - _mps[cx]; if (e.sw != 0) _mps[cx] ^= 1; _st[cx] = e.nlps; }
                    else { d = _mps[cx]; _st[cx] = e.nmps; }
                    Renorm();
                }
                else d = _mps[cx];
            }
            return d;
        }
    }

    // --- Tier 1: Bit-plane coding ---

    const int F_SIG = 1, F_SIGN = 2, F_REF = 4, F_VIS = 8;
    // MQ contexts: 0-8 significance, 9-13 sign, 14-16 refinement, 17 run-length, 18 uniform

    static int SigCtx(int[] fl, int stride, int i, int bandType)
    {
        int sH = ((fl[i - 1] & F_SIG) != 0 ? 1 : 0) + ((fl[i + 1] & F_SIG) != 0 ? 1 : 0);
        int sV = ((fl[i - stride] & F_SIG) != 0 ? 1 : 0) + ((fl[i + stride] & F_SIG) != 0 ? 1 : 0);
        int sD = ((fl[i - stride - 1] & F_SIG) != 0 ? 1 : 0) + ((fl[i - stride + 1] & F_SIG) != 0 ? 1 : 0)
               + ((fl[i + stride - 1] & F_SIG) != 0 ? 1 : 0) + ((fl[i + stride + 1] & F_SIG) != 0 ? 1 : 0);
        if (bandType == 1) { (sH, sV) = (sV, sH); } // HL: swap (T.800 Table D.1)
        if (bandType == 3) // HH - primary by sD (T.800 Table D.1 / Taubman Table 8.3)
        {
            int hv = sH + sV;
            if (sD >= 3) return 8;
            if (sD == 2) return hv >= 1 ? 7 : 6;
            if (sD == 1) { if (hv >= 2) return 5; if (hv == 1) return 4; return 3; }
            // sD == 0
            if (hv >= 2) return 2; if (hv == 1) return 1; return 0;
        }
        // LL/LH (T.800 Table D.1)
        if (sH >= 2) return 8;
        if (sH == 1) return sV >= 1 ? 7 : sD >= 1 ? 6 : 5;
        if (sV == 2) return 4;
        if (sV == 1) return 3;  // V=1 always 3 regardless of D
        if (sD >= 2) return 2;
        if (sD == 1) return 1;
        return 0;
    }

    static bool HasSigNeighbor(int[] fl, int stride, int i)
    {
        return ((fl[i - 1] | fl[i + 1] | fl[i - stride] | fl[i + stride]
               | fl[i - stride - 1] | fl[i - stride + 1] | fl[i + stride - 1] | fl[i + stride + 1]) & F_SIG) != 0;
    }

    static void DecodeSign(MqDec mq, int[] fl, int stride, int i, int[] mag, int mi, int bv)
    {
        int L = (fl[i - 1] & F_SIG) != 0 ? ((fl[i - 1] & F_SIGN) != 0 ? -1 : 1) : 0;
        int R = (fl[i + 1] & F_SIG) != 0 ? ((fl[i + 1] & F_SIGN) != 0 ? -1 : 1) : 0;
        int U = (fl[i - stride] & F_SIG) != 0 ? ((fl[i - stride] & F_SIGN) != 0 ? -1 : 1) : 0;
        int D = (fl[i + stride] & F_SIG) != 0 ? ((fl[i + stride] & F_SIGN) != 0 ? -1 : 1) : 0;
        int h = Math.Clamp(L + R, -1, 1), v = Math.Clamp(U + D, -1, 1);
        int ctx, xb;
        if (h == 1) { ctx = v == 1 ? 13 : v == 0 ? 12 : 11; xb = 0; }
        else if (h == 0) { ctx = v >= 0 ? (v == 1 ? 10 : 9) : 10; xb = v < 0 ? 1 : 0; }
        else { ctx = v == -1 ? 13 : v == 0 ? 12 : 11; xb = 1; }
        int sign = mq.Decode(ctx) ^ xb;
        mag[mi] |= bv;
        fl[i] |= F_SIG;
        if (sign != 0) fl[i] |= F_SIGN;
    }

    static void T1Decode(byte[] data, int off, int len, int w, int h, int numBp, int totalPasses, int bandType, int[] mag, int[] fl, int flStride)
    {
        if (len <= 0 || numBp <= 0 || totalPasses <= 0) return;
        var mq = new MqDec(data, off, len);
        int passIdx = 0;

        for (int bp = numBp - 1; bp >= 0 && passIdx < totalPasses; bp--)
        {
            int bv = 1 << bp;
            bool firstBp = (bp == numBp - 1);

            if (!firstBp)
            {
                // Significance propagation pass
                if (passIdx >= totalPasses) break;
                for (int sy = 0; sy < h; sy += 4)
                for (int x = 0; x < w; x++)
                for (int j = 0; j < 4 && sy + j < h; j++)
                {
                    int y = sy + j;
                    int fi = (y + 1) * flStride + (x + 1);
                    int mi = y * w + x;
                    if ((fl[fi] & F_SIG) != 0 || !HasSigNeighbor(fl, flStride, fi)) continue;
                    int ctx = SigCtx(fl, flStride, fi, bandType);
                    if (mq.Decode(ctx) != 0) DecodeSign(mq, fl, flStride, fi, mag, mi, bv);
                    fl[fi] |= F_VIS;
                }
                passIdx++;

                // Magnitude refinement pass
                if (passIdx >= totalPasses) { ClearVis(fl, w, h, flStride); break; }
                for (int sy = 0; sy < h; sy += 4)
                for (int x = 0; x < w; x++)
                for (int j = 0; j < 4 && sy + j < h; j++)
                {
                    int y = sy + j;
                    int fi = (y + 1) * flStride + (x + 1);
                    int mi = y * w + x;
                    if ((fl[fi] & F_SIG) == 0 || (fl[fi] & F_VIS) != 0) continue;
                    int ctx;
                    if ((fl[fi] & F_REF) == 0) ctx = HasSigNeighbor(fl, flStride, fi) ? 15 : 14;
                    else ctx = 16;
                    if (mq.Decode(ctx) != 0) mag[mi] |= bv;
                    fl[fi] |= F_REF;
                }
                passIdx++;
                if (passIdx >= totalPasses) { ClearVis(fl, w, h, flStride); break; }
            }

            // Cleanup pass
            for (int sy = 0; sy < h; sy += 4)
            for (int x = 0; x < w; x++)
            {
                int stripeH = Math.Min(4, h - sy);
                int j = 0;
                // Run-length mode: only when full stripe of 4 and all are clean
                if (stripeH == 4)
                {
                    bool allClean = true;
                    for (int k = 0; k < 4 && allClean; k++)
                    {
                        int fk = (sy + k + 1) * flStride + (x + 1);
                        if ((fl[fk] & (F_SIG | F_VIS)) != 0 || HasSigNeighbor(fl, flStride, fk))
                            allClean = false;
                    }
                    if (allClean)
                    {
                        if (mq.Decode(17) == 0) { j = 4; }
                        else
                        {
                            int runPos = (mq.Decode(18) << 1) | mq.Decode(18);
                            j = runPos;
                            int fi = (sy + j + 1) * flStride + (x + 1);
                            int mi = (sy + j) * w + x;
                            DecodeSign(mq, fl, flStride, fi, mag, mi, bv);
                            j++;
                        }
                    }
                }

                for (; j < stripeH; j++)
                {
                    int y = sy + j;
                    int fi = (y + 1) * flStride + (x + 1);
                    int mi = y * w + x;
                    if ((fl[fi] & (F_SIG | F_VIS)) != 0) { fl[fi] &= ~F_VIS; continue; }
                    int ctx = SigCtx(fl, flStride, fi, bandType);
                    if (mq.Decode(ctx) != 0) DecodeSign(mq, fl, flStride, fi, mag, mi, bv);
                    fl[fi] &= ~F_VIS;
                }
            }
            passIdx++;

            ClearVis(fl, w, h, flStride);
        }
    }

    static void ClearVis(int[] fl, int w, int h, int flStride)
    {
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            fl[(y + 1) * flStride + (x + 1)] &= ~F_VIS;
    }

    // --- Decode all code-blocks and place into DWT buffer ---

    static void DecodeAndPlaceBlocks(byte[] tileData, ResLevel[] levels, SbInfo[] subs,
        float[] coeffs, int tcW, int tcH, QcdInfo qcd, CodInfo cod, int depth, int tcx0, int tcy0)
    {
        int nl = cod.NL;
        foreach (var res in levels)
        foreach (var band in res.Bands)
        foreach (var cb in band.Blks)
        {
            if (cb.Segs.Count == 0 || cb.W <= 0 || cb.H <= 0) continue;
            var sb = subs[cb.SubIdx];
            if (sb.W <= 0 || sb.H <= 0) continue;

            // Gather contiguous data for this codeblock
            int totalLen = 0, totalPasses = 0;
            foreach (var s in cb.Segs) { totalLen += s.dataLen; totalPasses += s.passes; }
            if (totalLen <= 0) continue;

            var cbData = new byte[totalLen];
            int dp = 0;
            foreach (var s in cb.Segs)
            {
                int copyLen = Math.Min(s.dataLen, tileData.Length - s.dataOff);
                if (copyLen > 0) Buffer.BlockCopy(tileData, s.dataOff, cbData, dp, copyLen);
                dp += s.dataLen;
            }

            // Compute number of bit-planes
            int bi = sb.BandIdx;
            int numBp;
            if (cod.Wavelet == 1) // 5-3 reversible
                numBp = depth + qcd.Guard - 1 - cb.ZeroBp;
            else // 9-7
            {
                int eps = bi < qcd.Steps.Length ? qcd.Steps[bi].exp : 0;
                numBp = eps + qcd.Guard - 1 - cb.ZeroBp;
            }
            if (numBp <= 0) continue;

            int[] mag = new int[cb.W * cb.H];
            int flStride = cb.W + 2;
            int[] fl = new int[flStride * (cb.H + 2)];

            T1Decode(cbData, 0, cbData.Length, cb.W, cb.H, numBp, totalPasses, sb.Type, mag, fl, flStride);

            // Dequantize and place
            // stepsize = (1 + mu/2048) * 2^(Rb - eps), Rb = depth + gain
            // gain: LL=0, HL/LH=1, HH=2 (Table E.1)
            float step = 1.0f;
            if (cod.Wavelet == 0 && bi < qcd.Steps.Length)
            {
                var (e, m) = qcd.Steps[bi];
                int gain = sb.Type == 0 ? 0 : sb.Type == 3 ? 2 : 1;
                step = (float)((1.0 + m / 2048.0) * Math.Pow(2, depth + gain - e));
            }

            SubbandOffset(sb, tcx0, tcy0, tcx0 + tcW, tcy0 + tcH, nl, out int offX, out int offY);

            for (int y = 0; y < cb.H; y++)
            for (int x = 0; x < cb.W; x++)
            {
                int px = offX + cb.X0 + x;
                int py = offY + cb.Y0 + y;
                if ((uint)px >= (uint)tcW || (uint)py >= (uint)tcH) continue;
                int v = mag[y * cb.W + x];
                int fi = (y + 1) * flStride + (x + 1);
                float fv;
                if (v > 0)
                {
                    // Midpoint reconstruction per T.800 §E.1.1.2 (r=0.5)
                    float signedMag = v + 0.5f;
                    fv = (fl[fi] & F_SIGN) != 0 ? -signedMag : signedMag;
                }
                else fv = 0;
                if (cod.Wavelet == 0) fv *= step;
                coeffs[py * tcW + px] = fv;
            }
        }
    }

    static void SubbandOffset(SbInfo sb, int x0, int y0, int x1, int y1, int nl, out int ox, out int oy)
    {
        ox = 0; oy = 0;
        if (sb.Type == 0) return;
        int r = (sb.BandIdx - 1) / 3 + 1;
        int d = nl - r + 1;
        int halfW = CeilDiv(x1, 1 << d) - CeilDiv(x0, 1 << d);
        int halfH = CeilDiv(y1, 1 << d) - CeilDiv(y0, 1 << d);
        if (sb.Type == 1 || sb.Type == 3) ox = halfW;
        if (sb.Type == 2 || sb.Type == 3) oy = halfH;
    }

    // --- Inverse DWT ---

    static void InverseDWT(float[] c, int w, int h, int nl, int wavelet, int x0, int y0)
    {
        int x1 = x0 + w, y1 = y0 + h;
        for (int lev = nl; lev >= 1; lev--)
        {
            int rw = CeilDiv(x1, 1 << (lev - 1)) - CeilDiv(x0, 1 << (lev - 1));
            int rh = CeilDiv(y1, 1 << (lev - 1)) - CeilDiv(y0, 1 << (lev - 1));
            int lw = CeilDiv(x1, 1 << lev) - CeilDiv(x0, 1 << lev);
            int lh = CeilDiv(y1, 1 << lev) - CeilDiv(y0, 1 << lev);
            if (rw < 2 && rh < 2) continue;

            int phaseX = CeilDiv(x0, 1 << (lev - 1)) & 1;
            int phaseY = CeilDiv(y0, 1 << (lev - 1)) & 1;

            // Horizontal
            if (rw >= 2)
            {
                var tmp = new float[rw];
                for (int y = 0; y < rh; y++)
                {
                    Synth1D(c, y * w, lw, rw - lw, rw, tmp, wavelet, phaseX);
                    for (int i = 0; i < rw; i++) c[y * w + i] = tmp[i];
                }
            }

            // Vertical
            if (rh >= 2)
            {
                var col = new float[rh];
                var tmp = new float[rh];
                for (int x = 0; x < rw; x++)
                {
                    for (int i = 0; i < rh; i++) col[i] = c[i * w + x];
                    Synth1DCol(col, lh, rh - lh, rh, tmp, wavelet, phaseY);
                    for (int i = 0; i < rh; i++) c[i * w + x] = tmp[i];
                }
            }
        }
    }

    static void Synth1D(float[] src, int srcOff, int ne, int no, int len, float[] dst, int wavelet, int phase)
    {
        if (ne <= 0 && no <= 0) return;
        if (len == 1) { dst[0] = src[srcOff]; return; }
        var e = new float[ne]; var o = new float[no];
        for (int i = 0; i < ne; i++) e[i] = src[srcOff + i];
        for (int i = 0; i < no; i++) o[i] = src[srcOff + ne + i];
        LiftInv(e, o, dst, ne, no, len, wavelet, phase);
    }

    static void Synth1DCol(float[] col, int ne, int no, int len, float[] dst, int wavelet, int phase)
    {
        if (ne <= 0 && no <= 0) return;
        if (len == 1) { dst[0] = col[0]; return; }
        var e = new float[ne]; var o = new float[no];
        for (int i = 0; i < ne; i++) e[i] = col[i];
        for (int i = 0; i < no; i++) o[i] = col[ne + i];
        LiftInv(e, o, dst, ne, no, len, wavelet, phase);
    }

    static float Ext(float[] a, int i) => a[Math.Clamp(i, 0, a.Length - 1)];

    static void LiftInv(float[] e, float[] o, float[] dst, int ne, int no, int len, int wavelet, int phase)
    {
        // Phase 0 (even-first): e[i] neighbors o[i-1],o[i]; o[i] neighbors e[i],e[i+1]
        // Phase 1 (odd-first):  e[i] neighbors o[i],o[i+1]; o[i] neighbors e[i-1],e[i]
        int eL = phase == 0 ? -1 : 0, eR = phase == 0 ? 0 : 1;  // offsets for even's odd neighbors
        int oL = phase == 0 ? 0 : -1, oR = phase == 0 ? 1 : 0;  // offsets for odd's even neighbors

        if (wavelet == 1)
        {
            for (int i = 0; i < ne; i++)
                e[i] -= (float)Math.Floor((Ext(o, i + eL) + Ext(o, i + eR) + 2) / 4.0);
            for (int i = 0; i < no; i++)
                o[i] += (float)Math.Floor((Ext(e, i + oL) + Ext(e, i + oR)) / 2.0);
        }
        else
        {
            const float a = -1.586134342f, b = -0.052980118f, g = 0.882911075f, d = 0.443506852f, K = 1.230174105f;
            for (int i = 0; i < ne; i++) e[i] *= K;
            for (int i = 0; i < no; i++) o[i] /= K;
            for (int i = 0; i < ne; i++) e[i] -= d * (Ext(o, i + eL) + Ext(o, i + eR));
            for (int i = 0; i < no; i++) o[i] -= g * (Ext(e, i + oL) + Ext(e, i + oR));
            for (int i = 0; i < ne; i++) e[i] -= b * (Ext(o, i + eL) + Ext(o, i + eR));
            for (int i = 0; i < no; i++) o[i] -= a * (Ext(e, i + oL) + Ext(e, i + oR));
        }
        if (phase == 0)
        {
            for (int i = 0; i < ne; i++) dst[2 * i] = e[i];
            for (int i = 0; i < no; i++) dst[2 * i + 1] = o[i];
        }
        else
        {
            for (int i = 0; i < no; i++) dst[2 * i] = o[i];
            for (int i = 0; i < ne; i++) dst[2 * i + 1] = e[i];
        }
    }

    // --- Component transform ---

    static void ApplyMCT(float[][] comp, int w, int h, int mct, int wavelet)
    {
        if (mct == 0 || comp.Length < 3) return;
        int n = w * h;
        if (wavelet == 1)
        {
            // Inverse RCT
            for (int i = 0; i < n; i++)
            {
                float y = comp[0][i], cb = comp[1][i], cr = comp[2][i];
                float g = y - (float)Math.Floor((cb + cr) / 4.0);
                comp[0][i] = cr + g;
                comp[1][i] = g;
                comp[2][i] = cb + g;
            }
        }
        else
        {
            // Inverse ICT
            for (int i = 0; i < n; i++)
            {
                float y = comp[0][i], cb = comp[1][i], cr = comp[2][i];
                comp[0][i] = y + 1.402f * cr;
                comp[1][i] = y - 0.34413f * cb - 0.71414f * cr;
                comp[2][i] = y + 1.772f * cb;
            }
        }
    }

    // --- Build output bitmap ---

    static Bitmap BuildBitmap(float[][] comp, int w, int h, SizInfo siz)
    {
        if (siz.Csiz == 1)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format8bppIndexed);
            var pal = bmp.Palette;
            for (int i = 0; i < 256; i++) pal.Entries[i] = Color.FromArgb(i, i, i);
            bmp.Palette = pal;
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bmp.PixelFormat);
            int max = (1 << siz.Depth[0]) - 1;
            int shift = siz.Depth[0] - 8;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int v = Math.Clamp((int)Math.Round(comp[0][y * w + x]), 0, max);
                byte b = (byte)(shift > 0 ? v >> shift : shift < 0 ? v << -shift : v);
                Marshal.WriteByte(bd.Scan0 + y * bd.Stride + x, b);
            }
            bmp.UnlockBits(bd);
            return bmp;
        }
        else
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bmp.PixelFormat);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                int rc = To8(comp[0][idx], siz.Depth[0]);
                int gc = To8(comp.Length > 1 ? comp[1][idx] : comp[0][idx], siz.Depth[Math.Min(1, siz.Csiz - 1)]);
                int bc = To8(comp.Length > 2 ? comp[2][idx] : comp[0][idx], siz.Depth[Math.Min(2, siz.Csiz - 1)]);
                // BGRA layout for Format32bppArgb
                Marshal.WriteInt32(bd.Scan0 + y * bd.Stride + x * 4, (255 << 24) | (rc << 16) | (gc << 8) | bc);
            }
            bmp.UnlockBits(bd);
            return bmp;
        }
    }

    static int To8(float v, int depth)
    {
        int max = (1 << depth) - 1;
        int iv = Math.Clamp((int)Math.Round(v), 0, max);
        int shift = depth - 8;
        return shift > 0 ? iv >> shift : shift < 0 ? iv << -shift : iv;
    }
}
