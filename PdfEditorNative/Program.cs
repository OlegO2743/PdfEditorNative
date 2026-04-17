using System.Windows.Forms;
using PdfEditorNative;
using PdfEditorNative.Engine;
using PdfEditorNative.Engine.Render;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Headless render mode: --render <pdf> <page1-based> <zoom> <outputPng>
        if (args.Length >= 4 && args[0] == "--render")
        {
            try
            {
                var bytes = System.IO.File.ReadAllBytes(args[1]);
                int page = int.Parse(args[2]) - 1;
                float zoom = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
                var parser = new PdfParser(bytes); parser.Load();
                var renderer = new GdiRenderer(parser);
                using var bmp = renderer.RenderPage(page, zoom);
                bmp.Save(args[4], System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine($"Rendered page {page+1} to {args[4]} ({bmp.Width}x{bmp.Height})");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.Exit(1); }
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.Run(new MainForm());
    }
}
