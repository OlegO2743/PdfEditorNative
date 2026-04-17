// Engine/PdfSerializer.cs + PdfIncrementalWriter
using System.Text;
namespace PdfEditorNative.Engine;

public static class PdfSerializer
{
    private static readonly Encoding L1 = Encoding.Latin1;

    public static byte[] Serialize(PdfObj obj) => obj switch
    {
        PdfNull  _  => L1.GetBytes("null"),
        PdfBool  b  => L1.GetBytes(b.Value?"true":"false"),
        PdfInt   i  => L1.GetBytes(i.Value.ToString()),
        PdfReal  r  => L1.GetBytes(r.Value.ToString("G",System.Globalization.CultureInfo.InvariantCulture)),
        PdfName  n  => SerName(n),
        PdfStr   s  => SerStr(s),
        PdfRef   r  => L1.GetBytes($"{r.Num} {r.Gen} R"),
        PdfArray a  => SerArr(a),
        PdfDict  d  => SerDict(d),
        PdfStream s => SerStream(s),
        _ => L1.GetBytes("null"),
    };

    private static byte[] SerName(PdfName n)
    {
        var sb=new StringBuilder("/");
        foreach(char c in n.Value){
            if(c<=32||c>126||"#()[]{}<>/% ".Contains(c))sb.Append($"#{(int)c:X2}");
            else sb.Append(c);
        }
        return L1.GetBytes(sb.ToString());
    }
    private static byte[] SerStr(PdfStr s){
        var sb=new StringBuilder("<");
        foreach(byte b in s.Bytes)sb.Append(b.ToString("X2"));
        sb.Append('>');
        return L1.GetBytes(sb.ToString());
    }
    private static byte[] SerArr(PdfArray a){
        using var ms=new MemoryStream();
        ms.Write("[ "u8);
        foreach(var item in a.Items){ms.Write(Serialize(item));ms.WriteByte((byte)' ');}
        ms.Write("]"u8);
        return ms.ToArray();
    }
    private static byte[] SerDict(PdfDict d){
        using var ms=new MemoryStream();
        ms.Write("<<\n"u8);
        foreach(var kv in d.Items){ms.Write(Serialize(new PdfName(kv.Key)));ms.WriteByte((byte)' ');ms.Write(Serialize(kv.Value));ms.WriteByte((byte)'\n');}
        ms.Write(">>"u8);
        return ms.ToArray();
    }
    private static byte[] SerStream(PdfStream s){
        using var ms=new MemoryStream();
        s.Dict.Set("Length",new PdfInt(s.RawData.Length));
        ms.Write(SerDict(s.Dict));
        ms.Write("\nstream\n"u8);
        ms.Write(s.RawData);
        ms.Write("\nendstream"u8);
        return ms.ToArray();
    }
    public static byte[] SerializeIndirect(int num,int gen,PdfObj obj){
        using var ms=new MemoryStream();
        ms.Write(L1.GetBytes($"{num} {gen} obj\n"));
        ms.Write(Serialize(obj));
        ms.Write("\nendobj\n"u8);
        return ms.ToArray();
    }
}

public sealed class PdfIncrementalWriter
{
    private readonly PdfParser _src;
    private readonly Dictionary<int,byte[]> _newObjs=new();
    private int _nextNum;

    public PdfIncrementalWriter(PdfParser src){_src=src;_nextNum=src.NextObjNum;}

    public int AddObject(PdfObj obj){int n=_nextNum++;_newObjs[n]=PdfSerializer.SerializeIndirect(n,0,obj);return n;}
    public void ReplaceObject(int num,PdfObj obj)=>_newObjs[num]=PdfSerializer.SerializeIndirect(num,0,obj);

    public byte[] Build()
    {
        using var ms=new MemoryStream();
        ms.Write(_src.Raw);

        var offsets=new Dictionary<int,long>();
        foreach(var kv in _newObjs){offsets[kv.Key]=ms.Position;ms.Write(kv.Value);}

        long prevXref=FindPrevStartXref();
        long xrefPos=ms.Position;
        ms.Write(Encoding.Latin1.GetBytes("xref\n"));

        var nums=offsets.Keys.OrderBy(n=>n).ToList();
        int i=0;
        while(i<nums.Count){
            int j=i;
            while(j+1<nums.Count&&nums[j+1]==nums[j]+1)j++;
            ms.Write(Encoding.Latin1.GetBytes($"{nums[i]} {j-i+1}\n"));
            for(int k=i;k<=j;k++)ms.Write(Encoding.Latin1.GetBytes($"{offsets[nums[k]]:D10} 00000 n \r\n"));
            i=j+1;
        }

        ms.Write(Encoding.Latin1.GetBytes("trailer\n"));
        var td=new PdfDict();
        td.Set("Size",new PdfInt(_nextNum));
        if(_src.Trailer.Get("Root") is {} root)td.Set("Root",root);
        if(_src.Trailer.Get("Info") is {} info)td.Set("Info",info);
        if(_src.Trailer.Get("ID") is {} id)td.Set("ID",id);
        td.Set("Prev",new PdfInt((int)prevXref));
        ms.Write(PdfSerializer.Serialize(td));
        ms.Write(Encoding.Latin1.GetBytes($"\nstartxref\n{xrefPos}\n%%EOF\n"));
        return ms.ToArray();
    }

    private long FindPrevStartXref()
    {
        byte[] needle=Encoding.Latin1.GetBytes("startxref");
        int from=Math.Max(0,_src.Raw.Length-2048); long last=0;
        for(int i=from;i<=_src.Raw.Length-needle.Length;i++){
            bool ok=true;for(int j=0;j<needle.Length;j++)if(_src.Raw[i+j]!=needle[j]){ok=false;break;}
            if(ok){var lex=new PdfLexer(_src.Raw,i+9);if(lex.NextToken() is PdfInt pi)last=pi.Value;}
        }
        return last;
    }
}

// High-level operations
public static class PdfEditOperations
{
    public static byte[] AddText(byte[] src,int page,float x,float y,string text,float fs,float r,float g,float b)
    {
        var parser=new PdfParser(src);parser.Load();
        var writer=new PdfIncrementalWriter(parser);

        var fontDict=new PdfDict();
        fontDict.Set("Type",new PdfName("Font"));
        fontDict.Set("Subtype",new PdfName("Type1"));
        fontDict.Set("BaseFont",new PdfName("Helvetica"));
        fontDict.Set("Encoding",new PdfName("WinAnsiEncoding"));
        int fontNum=writer.AddObject(fontDict);

        string esc=text.Replace("\\","\\\\").Replace("(","\\(").Replace(")","\\)");
        string ops=$"q\nBT\n/F1_Added {fs.ToString("F2",System.Globalization.CultureInfo.InvariantCulture)} Tf\n"
            +$"{r.ToString("F4",System.Globalization.CultureInfo.InvariantCulture)} "
            +$"{g.ToString("F4",System.Globalization.CultureInfo.InvariantCulture)} "
            +$"{b.ToString("F4",System.Globalization.CultureInfo.InvariantCulture)} rg\n"
            +$"{x.ToString("F2",System.Globalization.CultureInfo.InvariantCulture)} "
            +$"{y.ToString("F2",System.Globalization.CultureInfo.InvariantCulture)} Td\n({esc}) Tj\nET\nQ\n";
        byte[] sd=Encoding.Latin1.GetBytes(ops);
        var cs=new PdfStream();cs.RawData=sd;cs.Dict.Set("Length",new PdfInt(sd.Length));
        int csNum=writer.AddObject(cs);

        int pageNum=parser.GetPageObjNum(page);
        var oldPage=(PdfDict)parser.Resolve(parser.GetObj(pageNum))!;
        var newPage=Clone(oldPage);

        var conts=new List<PdfObj>();
        var raw=oldPage.Get("Contents");
        if(raw is PdfArray ca)conts.AddRange(ca.Items);
        else if(raw is PdfRef rf2)conts.Add(rf2);
        conts.Add(new PdfRef(csNum,0));
        newPage.Set("Contents",new PdfArray(conts));

        var res=new PdfDict();
        if(parser.Resolve(oldPage.Get("Resources")) is PdfDict er)foreach(var kv in er.Items)res.Items[kv.Key]=kv.Value;
        var fr=new PdfDict();
        if(parser.Resolve(res.Get("Font")) is PdfDict efr)foreach(var kv in efr.Items)fr.Items[kv.Key]=kv.Value;
        fr.Set("F1_Added",new PdfRef(fontNum,0));
        res.Set("Font",fr);
        newPage.Set("Resources",res);
        writer.ReplaceObject(pageNum,newPage);
        return writer.Build();
    }

    public static byte[] RotatePage(byte[] src,int page,int deg)
    {
        var p=new PdfParser(src);p.Load();
        var w=new PdfIncrementalWriter(p);
        int pn=p.GetPageObjNum(page);
        var od=(PdfDict)p.Resolve(p.GetObj(pn))!;
        var nd=Clone(od);
        int cur=p.Resolve(od.Get("Rotate")).AsInt();
        int nr=((cur+deg)%360+360)%360;
        if(nr==0)nd.Items.Remove("Rotate");else nd.Set("Rotate",new PdfInt(nr));
        w.ReplaceObject(pn,nd);return w.Build();
    }

    public static byte[] ModifyPageContentStream(byte[] src, int pageIndex, byte[] newStream)
    {
        var parser = new PdfParser(src); parser.Load();
        var writer = new PdfIncrementalWriter(parser);

        var cs = new PdfStream();
        cs.RawData = newStream;
        cs.Dict.Set("Length", new PdfInt(newStream.Length));
        int csNum = writer.AddObject(cs);

        int pageNum = parser.GetPageObjNum(pageIndex);
        var oldPage = (PdfDict)parser.Resolve(parser.GetObj(pageNum))!;
        var newPage = Clone(oldPage);
        newPage.Set("Contents", new PdfRef(csNum, 0));
        writer.ReplaceObject(pageNum, newPage);
        return writer.Build();
    }

    private static PdfDict Clone(PdfDict d){var n=new PdfDict();foreach(var kv in d.Items)n.Items[kv.Key]=kv.Value;return n;}
}
