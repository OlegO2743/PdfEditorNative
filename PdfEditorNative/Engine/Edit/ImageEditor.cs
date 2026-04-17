// Engine/Edit/ImageEditor.cs
// Extracts image XObjects from a PDF page for editing,
// and writes modified images back as new XObject streams.

using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using PdfEditorNative.Engine;

namespace PdfEditorNative.Engine.Edit;

public sealed class PageImage
{
    public string  XObjectName { get; init; } = "";
    public int     ObjNum      { get; init; }
    public int     Width       { get; init; }
    public int     Height      { get; init; }
    public Bitmap  Bitmap      { get; set; } = null!;
    public string  Filter      { get; init; } = "";
}

public static class ImageEditor
{
    // ── List all images on a page ─────────────────────────────────
    public static List<PageImage> GetPageImages(PdfParser pdf, int pageIndex)
    {
        var page   = pdf.GetPageDict(pageIndex);
        var res    = pdf.GetResources(page);
        var xobjs  = pdf.Resolve(res.Get("XObject")) as PdfDict;
        if (xobjs == null) return new();

        var result = new List<PageImage>();
        foreach (var kv in xobjs.Items)
        {
            var stm = pdf.Resolve(kv.Value) as PdfStream;
            if (stm == null) continue;
            if (pdf.Resolve(stm.Dict.Get("Subtype")).AsName() != "Image") continue;

            int w = pdf.Resolve(stm.Dict.Get("Width")).AsInt();
            int h = pdf.Resolve(stm.Dict.Get("Height")).AsInt();
            int objNum = kv.Value is PdfRef r ? r.Num : -1;

            Bitmap? bmp = DecodeToBitmap(stm, pdf);
            if (bmp == null) continue;

            result.Add(new PageImage
            {
                XObjectName = kv.Key,
                ObjNum      = objNum,
                Width       = w,
                Height      = h,
                Bitmap      = bmp,
                Filter      = stm.FilterName(),
            });
        }
        return result;
    }

    // ── Decode an image XObject to Bitmap ────────────────────────
    public static Bitmap? DecodeToBitmap(PdfStream stm, PdfParser pdf)
    {
        string filter = stm.FilterName();
        try
        {
            if (filter == "DCTDecode")
                return new Bitmap(new MemoryStream(stm.RawData));

            byte[] decoded = stm.Decode();
            int w = pdf.Resolve(stm.Dict.Get("Width")).AsInt();
            int h = pdf.Resolve(stm.Dict.Get("Height")).AsInt();
            string cs = pdf.Resolve(stm.Dict.Get("ColorSpace")).AsName();
            int bpc = pdf.Resolve(stm.Dict.Get("BitsPerComponent")).AsInt();
            if (bpc == 0) bpc = 8;

            return RawToBitmap(decoded, w, h, cs, bpc);
        }
        catch { return null; }
    }

    private static Bitmap? RawToBitmap(byte[] data, int w, int h, string cs, int bpc)
    {
        if (w <= 0 || h <= 0) return null;
        int comps = cs switch { "DeviceGray"=>"DeviceGray".Length>0?1:1, "DeviceCMYK"=>4, _=>3 };
        // Simple: DeviceGray→1 comp, DeviceCMYK→4, else→3
        comps = cs == "DeviceGray" ? 1 : cs == "DeviceCMYK" ? 4 : 3;

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        int stride = (w * comps * bpc + 7) / 8;
        for (int row = 0; row < h; row++)
        for (int col = 0; col < w; col++)
        {
            int idx = row * stride + col * comps;
            if (idx + comps - 1 >= data.Length) goto done;
            Color c;
            if (comps == 1) { byte v = data[idx]; c = Color.FromArgb(v,v,v); }
            else if (comps == 4)
            {
                double cy=data[idx]/255.0,m=data[idx+1]/255.0,y=data[idx+2]/255.0,k=data[idx+3]/255.0;
                c = Color.FromArgb((int)((1-cy)*(1-k)*255),(int)((1-m)*(1-k)*255),(int)((1-y)*(1-k)*255));
            }
            else c = Color.FromArgb(data[idx], data[idx+1], idx+2<data.Length?data[idx+2]:(byte)0);
            bmp.SetPixel(col, row, c);
        }
        done:
        return bmp;
    }

    // ── Replace an image XObject with a new bitmap ────────────────
    /// <summary>
    /// Returns new PDF bytes with the specified XObject replaced by <paramref name="newImage"/>.
    /// The image is JPEG-encoded and injected as an incremental update.
    /// </summary>
    public static byte[] ReplaceImage(byte[] srcBytes, int objNum, Bitmap newImage,
        long jpegQuality = 90L)
    {
        // Encode bitmap to JPEG bytes
        byte[] jpeg;
        using (var ms = new MemoryStream())
        {
            var encoder   = GetJpegEncoder();
            var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(Encoder.Quality, jpegQuality);
            newImage.Save(ms, encoder, encParams);
            jpeg = ms.ToArray();
        }

        // Build new stream dict
        var dict = new PdfDict();
        dict.Set("Type",             new PdfName("XObject"));
        dict.Set("Subtype",          new PdfName("Image"));
        dict.Set("Width",            new PdfInt(newImage.Width));
        dict.Set("Height",           new PdfInt(newImage.Height));
        dict.Set("ColorSpace",       new PdfName("DeviceRGB"));
        dict.Set("BitsPerComponent", new PdfInt(8));
        dict.Set("Filter",           new PdfName("DCTDecode"));
        dict.Set("Length",           new PdfInt(jpeg.Length));

        var newStream = new PdfStream { Dict = dict, RawData = jpeg };

        // Write as incremental update
        var parser = new PdfParser(srcBytes);
        parser.Load();
        var writer = new PdfIncrementalWriter(parser);
        writer.ReplaceObject(objNum, newStream);
        return writer.Build();
    }

    private static ImageCodecInfo GetJpegEncoder()
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
            if (codec.FormatID == ImageFormat.Jpeg.Guid) return codec;
        throw new Exception("JPEG encoder not found");
    }
}
