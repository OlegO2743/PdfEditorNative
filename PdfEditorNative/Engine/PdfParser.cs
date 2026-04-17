// Engine/PdfParser.cs
// Supports:
//   • Classic xref tables (PDF ≤ 1.4)
//   • XRef streams with /W encoding (PDF 1.5+)
//   • Object streams /Type /ObjStm  (PDF 1.5+)
//   • /Prev chain for incremental updates

using System.Text;

namespace PdfEditorNative.Engine;

public sealed class PdfParser
{
    public readonly byte[] Raw;
    // obj-number → byte offset  (xref type 1)
    public readonly Dictionary<int, long> Xref = new();
    // obj-number → (stmObjNum, indexInStm)  (xref type 2)
    private readonly Dictionary<int, (int stmNum, int idx)> _objStmMap = new();

    public PdfDict Trailer { get; } = new();
    private readonly Dictionary<int, PdfObj?> _cache = new();
    private readonly Dictionary<int, List<(int num, PdfObj obj)>> _objStmCache = new();

    public int PageCount  { get; private set; }
    public int NextObjNum { get; private set; }

    public PdfParser(byte[] data) => Raw = data;

    // ═══════════════════════════════════════════════════════════════
    //  LOAD
    // ═══════════════════════════════════════════════════════════════
    public void Load()
    {
        long sxOff = FindStartXref();
        LoadXrefAt(sxOff);

        var rootObj = Resolve(Trailer.Get("Root"))
            ?? throw new Exception("No /Root in trailer");
        var catalog = rootObj as PdfDict
            ?? throw new Exception("Expected dict for Catalog");
        var pages = MustDict(Resolve(catalog.Get("Pages")), "Pages");
        PageCount = Resolve(pages.Get("Count")).AsInt();
        int maxXref   = Xref.Keys.Count   > 0 ? Xref.Keys.Max()      : 0;
        int maxObjStm = _objStmMap.Keys.Count > 0 ? _objStmMap.Keys.Max() : 0;
        NextObjNum = Math.Max(maxXref, maxObjStm) + 1;
    }

    // ── Find startxref value ──────────────────────────────────────
    private long FindStartXref()
    {
        byte[] needle = Encoding.Latin1.GetBytes("startxref");
        int from = Math.Max(0, Raw.Length - 4096);
        long found = -1;
        for (int i = from; i <= Raw.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (Raw[i + j] != needle[j]) { ok = false; break; }
            if (ok)
            {
                var lx = new PdfLexer(Raw, i + needle.Length);
                if (lx.NextToken() is PdfInt pi) found = pi.Value;
            }
        }
        if (found < 0) throw new Exception("startxref not found – not a valid PDF");
        return found;
    }

    // ── Dispatch: classic table or xref stream ────────────────────
    private void LoadXrefAt(long offset)
    {
        var lex = new PdfLexer(Raw, (int)offset);
        lex.SkipWS();
        int saved = lex.Position;
        var tok   = lex.NextToken();

        if (tok is string s && s == "xref")
            ParseClassicXref(lex);
        else
        {
            lex.Position = saved;
            ParseXrefStream(lex);
        }
    }

    // ── Classic xref table ────────────────────────────────────────
    private void ParseClassicXref(PdfLexer lex)
    {
        while (true)
        {
            lex.SkipWS();
            int pre = lex.Position;
            var tok = lex.NextToken();
            if (tok is string s && (s == "trailer" || s == "xref")) { lex.Position = pre; break; }
            if (tok is not PdfInt first) break;
            if (lex.NextToken() is not PdfInt cnt) break;

            for (int i = 0; i < cnt.Value; i++)
            {
                lex.SkipWS();
                var offTok  = lex.NextToken();
                var genTok  = lex.NextToken();
                var typeTok = lex.NextToken() as string;
                int num = first.Value + i;
                if (typeTok == "n" && offTok is PdfInt oi
                    && !Xref.ContainsKey(num) && !_objStmMap.ContainsKey(num) && num > 0)
                    Xref[num] = oi.Value;
            }
        }

        lex.SkipWS();
        if (lex.NextToken() as string == "trailer")
        {
            var td = ParseObject(lex) as PdfDict;
            if (td != null)
            {
                MergeTrailer(td);
                if (td.Get("Prev") is PdfInt prev && prev.Value > 0)
                    LoadXrefAt(prev.Value);
            }
        }
    }

    // ── Xref stream (PDF 1.5+) ────────────────────────────────────
    private void ParseXrefStream(PdfLexer lex)
    {
        lex.NextToken(); lex.NextToken(); // obj-num, gen
        if (lex.NextToken() as string != "obj") return;

        var dict = ParseObject(lex) as PdfDict;
        if (dict == null) return;

        lex.SkipWS();
        if (lex.NextToken() as string != "stream") return;
        if (lex.PeekByte() == 13) lex.SkipByte();
        if (lex.PeekByte() == 10) lex.SkipByte();

        int ds  = lex.Position;
        int len = ResolveInt(dict.Get("Length"));
        var stm = new PdfStream { Dict = dict };
        stm.RawData = new byte[Math.Min(len, Raw.Length - ds)];
        Array.Copy(Raw, ds, stm.RawData, 0, stm.RawData.Length);

        MergeTrailer(dict);

        // /W
        var wArr = dict.Get("W") as PdfArray;
        if (wArr == null || wArr.Items.Count < 3) return;
        int w1 = wArr.Items[0].AsInt(), w2 = wArr.Items[1].AsInt(), w3 = wArr.Items[2].AsInt();
        int entrySize = w1 + w2 + w3;
        if (entrySize == 0) return;

        // /Index
        var indexArr = dict.Get("Index") as PdfArray;
        var sections = new List<(int first, int count)>();
        if (indexArr != null && indexArr.Items.Count >= 2)
        {
            for (int i = 0; i + 1 < indexArr.Items.Count; i += 2)
                sections.Add((indexArr.Items[i].AsInt(), indexArr.Items[i + 1].AsInt()));
        }
        else
        {
            sections.Add((0, dict.Get("Size")?.AsInt() ?? 0));
        }

        byte[] decoded = stm.Decode();
        int pos = 0;
        foreach (var (firstObj, count) in sections)
        {
            for (int i = 0; i < count; i++)
            {
                if (pos + entrySize > decoded.Length) break;
                int  type   = ReadW(decoded, pos, w1);
                long field2 = ReadW(decoded, pos + w1, w2);
                int  field3 = ReadW(decoded, pos + w1 + w2, w3);
                pos += entrySize;

                int num = firstObj + i;
                if (num == 0) continue;
                bool known = Xref.ContainsKey(num) || _objStmMap.ContainsKey(num);
                if (known) continue;

                if (type == 1)      Xref[num]      = field2;
                else if (type == 2) _objStmMap[num] = ((int)field2, field3);
                // type==0 → free, ignore
            }
        }

        if (dict.Get("Prev") is PdfInt prev && prev.Value > 0)
            LoadXrefAt(prev.Value);
    }

    private static int ReadW(byte[] data, int pos, int width)
    {
        if (width == 0) return 1; // default for type field = 1
        int val = 0;
        for (int i = 0; i < width; i++) val = (val << 8) | data[pos + i];
        return val;
    }

    private void MergeTrailer(PdfDict td)
    {
        // Skip stream-specific keys
        var skip = new HashSet<string>{ "W","Index","Length","Filter","DecodeParms","Type","Columns","Predictor" };
        foreach (var kv in td.Items)
            if (!Trailer.Items.ContainsKey(kv.Key) && !skip.Contains(kv.Key))
                Trailer.Items[kv.Key] = kv.Value;
    }

    // ═══════════════════════════════════════════════════════════════
    //  OBJECT RETRIEVAL
    // ═══════════════════════════════════════════════════════════════
    public PdfObj? GetObj(int num)
    {
        if (_cache.TryGetValue(num, out var cached)) return cached;

        // Type-2: inside object stream
        if (_objStmMap.TryGetValue(num, out var loc))
        {
            var list = GetObjStream(loc.stmNum);
            foreach (var (n, o) in list) _cache[n] = o;
            return _cache.TryGetValue(num, out var hit) ? hit : PdfNull.I;
        }

        // Type-1: at byte offset
        if (!Xref.TryGetValue(num, out long off)) return PdfNull.I;

        var lex = new PdfLexer(Raw, (int)off);
        lex.NextToken(); lex.NextToken();
        if (lex.NextToken() as string != "obj") return PdfNull.I;

        var obj = ParseObject(lex);
        if (obj is PdfDict dict)
        {
            lex.SkipWS();
            int pre = lex.Position;
            if (lex.NextToken() as string == "stream")
            {
                if (lex.PeekByte() == 13) lex.SkipByte();
                if (lex.PeekByte() == 10) lex.SkipByte();
                int dStart = lex.Position;
                int dLen   = ResolveInt(dict.Get("Length"));
                var stm    = new PdfStream { Dict = dict };
                stm.RawData = new byte[Math.Min(dLen, Raw.Length - dStart)];
                Array.Copy(Raw, dStart, stm.RawData, 0, stm.RawData.Length);
                obj = stm;
            }
            else lex.Position = pre;
        }

        _cache[num] = obj;
        return obj;
    }

    // Resolve /Length that may be an indirect ref
    private int ResolveInt(PdfObj? o)
    {
        if (o is PdfInt pi) return pi.Value;
        if (o is PdfRef rf && Xref.TryGetValue(rf.Num, out long loff))
        {
            var lx = new PdfLexer(Raw, (int)loff);
            lx.NextToken(); lx.NextToken(); lx.NextToken(); // n g obj
            return (lx.NextToken() as PdfInt)?.Value ?? 0;
        }
        return 0;
    }

    // ── Object stream /Type /ObjStm ───────────────────────────────
    private List<(int num, PdfObj obj)> GetObjStream(int stmNum)
    {
        if (_objStmCache.TryGetValue(stmNum, out var cached)) return cached;

        var stmObj = GetObj(stmNum) as PdfStream
            ?? throw new Exception($"Object stream {stmNum} not a stream");

        int n     = stmObj.Dict.Get("N")?.AsInt()     ?? 0;
        int first = stmObj.Dict.Get("First")?.AsInt() ?? 0;
        byte[] decoded = stmObj.Decode();

        // Header: N pairs of (objNum, relativeOffset)
        var hLex    = new PdfLexer(decoded, 0);
        var offsets = new List<(int num, int off)>(n);
        for (int i = 0; i < n; i++)
        {
            int oNum = (hLex.NextToken() as PdfInt)?.Value ?? 0;
            int oOff = (hLex.NextToken() as PdfInt)?.Value ?? 0;
            offsets.Add((oNum, first + oOff));
        }

        // IMPORTANT: sort by offset so consecutive slicing works correctly.
        // The PDF spec says offsets in the header may not be in ascending order.
        offsets.Sort((a, b) => a.off.CompareTo(b.off));

        var result = new List<(int, PdfObj)>(n);
        for (int i = 0; i < offsets.Count; i++)
        {
            int start = offsets[i].off;
            int end   = (i + 1 < offsets.Count) ? offsets[i + 1].off : decoded.Length;
            if (start >= decoded.Length) break;
            var slice = new byte[Math.Max(0, end - start)];
            Array.Copy(decoded, start, slice, 0, slice.Length);
            var obj = ParseObject(new PdfLexer(slice)) ?? PdfNull.I;
            result.Add((offsets[i].num, obj));
        }

        _objStmCache[stmNum] = result;
        return result;
    }

    public PdfObj? Resolve(PdfObj? o) =>
        o is PdfRef r ? Resolve(GetObj(r.Num)) : o;

    // ═══════════════════════════════════════════════════════════════
    //  PAGE TREE
    // ═══════════════════════════════════════════════════════════════
    public int GetPageObjNum(int index)
    {
        var catalog = MustDict(Resolve(Trailer.Get("Root")), "Root");
        var pages   = MustDict(Resolve(catalog.Get("Pages")), "Pages");
        int found = -1, visited = 0;
        WalkPages(pages, index, ref visited, ref found);
        if (found < 0) throw new Exception($"Page {index} not found");
        return found;
    }

    public PdfDict GetPageDict(int index) =>
        MustDict(Resolve(GetObj(GetPageObjNum(index))), $"page {index}");

    private void WalkPages(PdfDict node, int target, ref int visited, ref int found)
    {
        if (found >= 0) return;
        if (Resolve(node.Get("Kids")) is not PdfArray kids) return;
        foreach (var kid in kids.Items)
        {
            if (found >= 0) return;
            if (kid is not PdfRef kr) continue;
            var kd = MustDict(Resolve(kid), "kid");
            string t = Resolve(kd.Get("Type")).AsName();
            if (t == "Page")
            {
                if (visited == target) { found = kr.Num; return; }
                visited++;
            }
            else if (t == "Pages")
            {
                int cnt = Resolve(kd.Get("Count")).AsInt();
                if (visited + cnt > target) WalkPages(kd, target, ref visited, ref found);
                else visited += cnt;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  OBJECT PARSER
    // ═══════════════════════════════════════════════════════════════
    public PdfObj? ParseObject(PdfLexer lex)
    {
        lex.SkipWS();
        var tok = lex.NextToken();
        if (tok is string kw)
        {
            switch (kw)
            {
                case "<<":   return ParseDict(lex);
                case "[":    return ParseArray(lex);
                case "null": return PdfNull.I;
                case "true": return new PdfBool(true);
                case "false":return new PdfBool(false);
                default:     return null;
            }
        }
        if (tok is PdfName pn) return pn;
        if (tok is PdfStr  ps) return ps;
        if (tok is PdfReal pr) return pr;
        if (tok is PdfBool pb) return pb;
        if (tok is PdfNull)    return PdfNull.I;
        if (tok is PdfInt n1)
        {
            int pre = lex.Position; lex.SkipWS();
            var t2  = lex.NextToken();
            if (t2 is PdfInt gen)
            {
                lex.SkipWS(); int preR = lex.Position;
                if (lex.NextToken() as string == "R") return new PdfRef(n1.Value, gen.Value);
                // NOT an object reference — restore fully to before t2
            }
            lex.Position = pre; return n1;
        }
        return null;
    }

    private PdfDict ParseDict(PdfLexer lex)
    {
        var d = new PdfDict();
        while (true)
        {
            lex.SkipWS();
            var tok = lex.NextToken();
            if (tok == null || (tok is string s && s == ">>")) break;
            if (tok is not PdfName key) continue;
            var val = ParseObject(lex);
            if (val != null) d.Items[key.Value] = val;
        }
        return d;
    }

    private PdfArray ParseArray(PdfLexer lex)
    {
        var a = new PdfArray();
        while (true)
        {
            lex.SkipWS();
            if (lex.AtEnd || lex.PeekByte() == (byte)']') { lex.SkipByte(); break; }
            var v = ParseObject(lex);
            if (v == null) break;
            a.Items.Add(v);
        }
        return a;
    }

    // ═══════════════════════════════════════════════════════════════
    //  PAGE CONTENT & RESOURCES
    // ═══════════════════════════════════════════════════════════════
    public byte[] GetContentBytes(PdfDict page)
    {
        var cont = Resolve(page.Get("Contents"));
        if (cont is PdfArray arr)
        {
            var ms = new MemoryStream();
            foreach (var item in arr.Items)
            {
                if (Resolve(item) is PdfStream s) { ms.Write(s.Decode()); ms.WriteByte((byte)' '); }
            }
            return ms.ToArray();
        }
        if (cont is PdfStream stm) return stm.Decode();
        return Array.Empty<byte>();
    }

    public PdfDict GetResources(PdfDict page)
    {
        var r = Resolve(page.Get("Resources"));
        if (r is PdfDict d) return d;
        if (page.Get("Parent") is PdfRef pr)
            return GetResources(MustDict(Resolve(GetObj(pr.Num)), "parent"));
        return new PdfDict();
    }

    public static PdfDict MustDict(PdfObj? o, string ctx) =>
        o as PdfDict ?? throw new Exception($"Expected dict for {ctx}");
}
