// Engine/PdfMerger.cs
using System.Text;
namespace PdfEditorNative.Engine;

public sealed class PdfMerger
{
    private readonly MemoryStream _body = new();
    private int _nextObj = 1;
    private readonly List<(int num, long off)> _objOffsets = new();
    private readonly List<int> _pageRefs = new();

    public void AddPages(byte[] srcBytes, IEnumerable<int>? pages = null)
    {
        var p = new PdfParser(srcBytes);
        p.Load();
        var indices = pages?.ToList() ?? Enumerable.Range(0, p.PageCount).ToList();
        var remap = new Dictionary<int, int>();

        int CopyObj(int srcNum)
        {
            if (remap.TryGetValue(srcNum, out int ex)) return ex;
            int dstNum = _nextObj++;
            remap[srcNum] = dstNum;

            var obj = p.Resolve(p.GetObj(srcNum));
            if (obj == null || obj is PdfNull) return dstNum;

            var copied = DeepCopy(obj, CopyObj);
            long off = _body.Position;
            _body.Write(PdfSerializer.SerializeIndirect(dstNum, 0, copied));
            _objOffsets.Add((dstNum, off));
            return dstNum;
        }

        foreach (int pi in indices)
        {
            int srcNum = p.GetPageObjNum(pi);
            int dstNum = CopyObj(srcNum);
            _pageRefs.Add(dstNum);
        }
    }

    public byte[] Build()
    {
        using var ms = new MemoryStream();
        ms.Write("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n"u8);

        long bodyStart = ms.Position;
        _body.Position = 0;
        _body.CopyTo(ms);

        // Pages dict
        int pagesNum   = _nextObj++;
        int catalogNum = _nextObj++;
        int totalObjs  = _nextObj;

        long pagesOff = ms.Position;
        var pd = new PdfDict();
        pd.Set("Type",  new PdfName("Pages"));
        pd.Set("Count", new PdfInt(_pageRefs.Count));
        pd.Set("Kids",  new PdfArray(_pageRefs.Select(n => (PdfObj)new PdfRef(n, 0))));
        ms.Write(PdfSerializer.SerializeIndirect(pagesNum, 0, pd));

        long catOff = ms.Position;
        var cd = new PdfDict();
        cd.Set("Type",  new PdfName("Catalog"));
        cd.Set("Pages", new PdfRef(pagesNum, 0));
        ms.Write(PdfSerializer.SerializeIndirect(catalogNum, 0, cd));

        // xref
        long xrefPos = ms.Position;
        ms.Write(Encoding.Latin1.GetBytes($"xref\n0 {totalObjs}\n"));
        ms.Write(Encoding.Latin1.GetBytes("0000000000 65535 f \r\n"));

        for (int n = 1; n < totalObjs; n++)
        {
            long off = 0;
            var found = _objOffsets.Find(x => x.num == n);
            if (found != default) off = bodyStart + found.off;
            else if (n == pagesNum)   off = pagesOff;
            else if (n == catalogNum) off = catOff;
            ms.Write(Encoding.Latin1.GetBytes($"{off:D10} 00000 n \r\n"));
        }

        ms.Write(Encoding.Latin1.GetBytes("trailer\n"));
        var td = new PdfDict();
        td.Set("Size", new PdfInt(totalObjs));
        td.Set("Root", new PdfRef(catalogNum, 0));
        ms.Write(PdfSerializer.Serialize(td));
        ms.Write(Encoding.Latin1.GetBytes($"\nstartxref\n{xrefPos}\n%%EOF\n"));
        return ms.ToArray();
    }

    private static PdfObj DeepCopy(PdfObj? obj, Func<int, int> remap)
    {
        if (obj == null || obj is PdfNull) return PdfNull.I;
        if (obj is PdfBool  b) return new PdfBool(b.Value);
        if (obj is PdfInt   i) return new PdfInt(i.Value);
        if (obj is PdfReal  r) return new PdfReal(r.Value);
        if (obj is PdfName  n) return new PdfName(n.Value);
        if (obj is PdfStr   s) return new PdfStr((byte[])s.Bytes.Clone());
        if (obj is PdfRef  rf) return new PdfRef(remap(rf.Num), 0);
        if (obj is PdfArray a) return new PdfArray(a.Items.Select(x => DeepCopy(x, remap)));
        if (obj is PdfStream ps)
        {
            var ns = new PdfStream();
            ns.Dict = (PdfDict)DeepCopy(ps.Dict, remap);
            ns.RawData = (byte[])ps.RawData.Clone();
            return ns;
        }
        if (obj is PdfDict  d)
        {
            var nd = new PdfDict();
            foreach (var kv in d.Items) nd.Items[kv.Key] = DeepCopy(kv.Value, remap);
            return nd;
        }
        return PdfNull.I;
    }
}
