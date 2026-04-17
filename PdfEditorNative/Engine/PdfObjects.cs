// Engine/PdfObjects.cs
using System.Text;
namespace PdfEditorNative.Engine;

public abstract class PdfObj { }
public sealed class PdfNull  : PdfObj { public static readonly PdfNull I = new(); }
public sealed class PdfBool  : PdfObj { public bool   Value; public PdfBool(bool v)   => Value = v; }
public sealed class PdfInt   : PdfObj { public int    Value; public PdfInt(int v)    => Value = v; }
public sealed class PdfReal  : PdfObj { public double Value; public PdfReal(double v) => Value = v; }
public sealed class PdfName  : PdfObj { public string Value; public PdfName(string v) => Value = v; }
public sealed class PdfRef   : PdfObj { public int Num, Gen; public PdfRef(int n, int g){Num=n;Gen=g;} }
public sealed class PdfStr   : PdfObj
{
    public byte[] Bytes;
    public PdfStr(byte[] b) => Bytes = b;
    public string Latin1 => Encoding.Latin1.GetString(Bytes);
}
public sealed class PdfArray : PdfObj
{
    public List<PdfObj> Items = new();
    public PdfArray() { }
    public PdfArray(IEnumerable<PdfObj> items) => Items = new(items);
}
public sealed class PdfDict : PdfObj
{
    public Dictionary<string, PdfObj> Items = new();
    public PdfObj? Get(string k) => Items.TryGetValue(k, out var v) ? v : null;
    public void Set(string k, PdfObj v) => Items[k] = v;
}
public sealed class PdfStream : PdfObj
{
    public PdfDict Dict   = new();
    public byte[]  RawData = Array.Empty<byte>();
    public byte[] Decode()
    {
        string f = FilterName();
        byte[] raw = f switch
        {
            "FlateDecode"    => Inflate(RawData),
            "DCTDecode"      => RawData,
            "ASCIIHexDecode" => AsciiHex(RawData),
            _                => RawData,
        };
        // Apply PNG/TIFF predictor if present
        var dp = Dict.Get("DecodeParms") as PdfDict
              ?? (Dict.Get("DecodeParms") is PdfArray dpa && dpa.Items.Count > 0
                  ? dpa.Items[0] as PdfDict : null);
        if (dp != null)
        {
            int predictor = dp.Get("Predictor")?.AsInt() ?? 1;
            if (predictor >= 10) // PNG predictors 10-15
            {
                int columns = dp.Get("Columns")?.AsInt() ?? 1;
                int colors  = dp.Get("Colors")?.AsInt()  ?? 1;
                int bpc     = dp.Get("BitsPerComponent")?.AsInt() ?? 8;
                raw = UndoPngPredictor(raw, columns, colors, bpc);
            }
        }
        return raw;
    }
    public string FilterName()
    {
        var flt = Dict.Get("Filter");
        return flt switch
        {
            PdfName n  => n.Value,
            PdfArray a => a.Items.Count > 0 && a.Items[0] is PdfName n2 ? n2.Value : "",
            _ => ""
        };
    }
    private static byte[] Inflate(byte[] data)
    {
        // Try zlib-wrapped deflate (handles all valid CMF bytes: 0x78, 0x68, 0x58, etc.)
        try
        {
            using var ms = new MemoryStream(data);
            using var zlib = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionMode.Decompress);
            using var out_ = new MemoryStream();
            zlib.CopyTo(out_);
            return out_.ToArray();
        }
        catch { }
        // Fallback: raw deflate (no zlib header)
        try
        {
            using var ms = new MemoryStream(data);
            using var def = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
            using var out_ = new MemoryStream();
            def.CopyTo(out_);
            return out_.ToArray();
        }
        catch { }
        return data;
    }
    private static byte[] AsciiHex(byte[] data)
    {
        var ms = new MemoryStream();
        int i = 0;
        while (i < data.Length)
        {
            while (i < data.Length && data[i] <= 32) i++;
            if (i >= data.Length || data[i] == '>') break;
            char hi = (char)data[i++];
            while (i < data.Length && data[i] <= 32) i++;
            char lo = (i < data.Length && data[i] != '>') ? (char)data[i++] : '0';
            try { ms.WriteByte(Convert.ToByte(new string(new[]{hi,lo}), 16)); } catch { }
        }
        return ms.ToArray();
    }

    // PNG predictor (filters 10–15, applied row by row)
    private static byte[] UndoPngPredictor(byte[] data, int columns, int colors, int bpc)
    {
        int bpp    = Math.Max(1, (colors * bpc + 7) / 8); // bytes per pixel
        int stride = (columns * colors * bpc + 7) / 8;    // bytes per row (without filter byte)
        int rowLen = stride + 1;                           // +1 for filter byte

        if (data.Length < rowLen) return data;
        int rows = data.Length / rowLen;
        var out_ = new byte[rows * stride];

        byte[] prev = new byte[stride]; // previous row (starts as zeros)
        for (int r = 0; r < rows; r++)
        {
            int srcRow = r * rowLen;
            int dstRow = r * stride;
            int filter = data[srcRow];
            byte[] cur = new byte[stride];
            Array.Copy(data, srcRow + 1, cur, 0, stride);

            switch (filter)
            {
                case 0: // None
                    break;
                case 1: // Sub
                    for (int i = bpp; i < stride; i++) cur[i] += cur[i - bpp];
                    break;
                case 2: // Up
                    for (int i = 0; i < stride; i++) cur[i] += prev[i];
                    break;
                case 3: // Average
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        cur[i] += (byte)((a + prev[i]) / 2);
                    }
                    break;
                case 4: // Paeth
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        int b = prev[i];
                        int c = i >= bpp ? prev[i - bpp] : 0;
                        cur[i] += (byte)PaethPredictor(a, b, c);
                    }
                    break;
            }
            Array.Copy(cur, 0, out_, dstRow, stride);
            prev = cur;
        }
        return out_;
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        int p  = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

public static class PdfObjExt
{
    public static double AsDouble(this PdfObj? o) => o switch { PdfInt i=>i.Value, PdfReal r=>r.Value, _=>0 };
    public static int    AsInt(this PdfObj? o)    => o switch { PdfInt i=>i.Value, PdfReal r=>(int)r.Value, _=>0 };
    public static string AsName(this PdfObj? o)   => o is PdfName n ? n.Value : "";
}
