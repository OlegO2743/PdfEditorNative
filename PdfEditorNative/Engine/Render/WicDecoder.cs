// Engine/Render/WicDecoder.cs
// Decodes images via Windows Imaging Component (COM interop).
// No external libraries — uses only built-in Windows APIs.
// Handles any format that WIC has a codec for (JPEG, PNG, TIFF, BMP, JPEG-XR,
// and JPEG 2000 if a WIC codec is installed).

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PdfEditorNative.Engine.Render;

internal static class WicDecoder
{
    private static readonly Guid CLSID_WICImagingFactory =
        new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid GUID_WICPixelFormat32bppBGRA =
        new("6fddc324-4e03-4bfe-b185-3d77768dc910");

    [DllImport("shlwapi.dll")]
    private static extern IntPtr SHCreateMemStream(byte[] pInit, uint cbInit);

    /// <summary>Try to decode image data using WIC. Returns null on failure.</summary>
    internal static Bitmap? TryDecode(byte[] data)
    {
        IWICImagingFactory? factory = null;
        IWICBitmapDecoder? decoder = null;
        IWICBitmapFrameDecode? frame = null;
        IWICFormatConverter? converter = null;
        IntPtr pStream = IntPtr.Zero;
        try
        {
            factory = (IWICImagingFactory)Activator.CreateInstance(
                Type.GetTypeFromCLSID(CLSID_WICImagingFactory)!)!;

            pStream = SHCreateMemStream(data, (uint)data.Length);
            if (pStream == IntPtr.Zero) return null;

            if (factory.CreateDecoderFromStream(pStream, IntPtr.Zero, 0, out decoder) < 0)
                return null;
            if (decoder!.GetFrame(0, out frame) < 0)
                return null;
            frame!.GetSize(out uint w, out uint h);

            if (factory.CreateFormatConverter(out converter) < 0)
                return null;

            var fmt = GUID_WICPixelFormat32bppBGRA;
            IntPtr pFrame = Marshal.GetIUnknownForObject(frame);
            try
            {
                if (converter!.Initialize(pFrame, ref fmt, 0, IntPtr.Zero, 0.0, 0) < 0)
                    return null;
            }
            finally { Marshal.Release(pFrame); }

            int stride = (int)w * 4;
            var buf = new byte[stride * (int)h];
            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                if (converter.CopyPixels(IntPtr.Zero, (uint)stride,
                        (uint)buf.Length, pin.AddrOfPinnedObject()) < 0)
                    return null;
            }
            finally { pin.Free(); }

            var bmp = new Bitmap((int)w, (int)h, PixelFormat.Format32bppArgb);
            var bits = bmp.LockBits(new Rectangle(0, 0, (int)w, (int)h),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(buf, 0, bits.Scan0, buf.Length);
            bmp.UnlockBits(bits);
            return bmp;
        }
        catch { return null; }
        finally
        {
            if (converter != null) Marshal.ReleaseComObject(converter);
            if (frame != null) Marshal.ReleaseComObject(frame);
            if (decoder != null) Marshal.ReleaseComObject(decoder);
            if (factory != null) Marshal.ReleaseComObject(factory);
            if (pStream != IntPtr.Zero) Marshal.Release(pStream);
        }
    }

    // ── WIC COM interfaces (minimal vtable stubs) ───────────────

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        void _0(); // CreateDecoderFromFilename
        [PreserveSig] int CreateDecoderFromStream(
            IntPtr pIStream, IntPtr vendor, uint options,
            out IWICBitmapDecoder decoder);
        void _2(); void _3(); void _4(); void _5(); void _6();
        [PreserveSig] int CreateFormatConverter(out IWICFormatConverter converter);
    }

    [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapDecoder
    {
        void _0(); void _1(); void _2(); void _3(); void _4();
        void _5(); void _6(); void _7(); void _8(); void _9();
        [PreserveSig] int GetFrame(uint index, out IWICBitmapFrameDecode frame);
    }

    [ComImport, Guid("e8eda601-3d48-431a-ab44-69059be88bbe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapFrameDecode
    {
        [PreserveSig] int GetSize(out uint w, out uint h);
        void _1(); void _2(); void _3();
        [PreserveSig] int CopyPixels(
            IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pbBuffer);
    }

    [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICFormatConverter
    {
        [PreserveSig] int GetSize(out uint w, out uint h);
        void _1(); void _2(); void _3();
        [PreserveSig] int CopyPixels(
            IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pbBuffer);
        [PreserveSig] int Initialize(
            IntPtr pISource, ref Guid dstFormat, int dither,
            IntPtr pIPalette, double alphaThreshold, int paletteTranslate);
    }
}
