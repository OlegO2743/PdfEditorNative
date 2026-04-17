// Engine/Render/GraphicsState.cs
// PDF graphics state machine — matrix math, color, text state.

using System.Drawing;
using System.Drawing.Drawing2D;

namespace PdfEditorNative.Engine.Render;

// ── 2D affine matrix [a b c d e f] ───────────────────────────────
public struct PdfMatrix
{
    public double A, B, C, D, E, F;

    public static PdfMatrix Identity => new() { A=1, B=0, C=0, D=1, E=0, F=0 };

    public static PdfMatrix From(double a,double b,double c,double d,double e,double f)
        => new(){A=a,B=b,C=c,D=d,E=e,F=f};

    /// <summary>Multiply: this × other (right-multiply).</summary>
    public PdfMatrix Mul(PdfMatrix m) => new()
    {
        A = A*m.A + B*m.C,
        B = A*m.B + B*m.D,
        C = C*m.A + D*m.C,
        D = C*m.B + D*m.D,
        E = E*m.A + F*m.C + m.E,
        F = E*m.B + F*m.D + m.F,
    };

    public (double x, double y) Transform(double x, double y)
        => (A*x + C*y + E, B*x + D*y + F);

    public System.Drawing.Drawing2D.Matrix ToGdi()
        => new((float)A,(float)B,(float)C,(float)D,(float)E,(float)F);
}

// ── Graphics state ────────────────────────────────────────────────
public sealed class GfxState
{
    public PdfMatrix CTM         = PdfMatrix.Identity;
    public Color     StrokeColor = Color.Black;
    public Color     FillColor   = Color.Black;
    public double    LineWidth   = 1;
    public int       LineCap     = 0;   // 0=butt 1=round 2=square
    public int       LineJoin    = 0;   // 0=miter 1=round 2=bevel
    public float[]?  DashArray   = null;
    public float     DashPhase   = 0;
    /// <summary>Stroke alpha (/CA from ExtGState). Multiplied into StrokeColor at draw time.</summary>
    public double    StrokeAlpha = 1.0;
    /// <summary>Fill alpha (/ca from ExtGState). Multiplied into FillColor at draw time.</summary>
    public double    FillAlpha   = 1.0;

    // Text state
    public string  FontName      = "Helvetica";
    public double  FontSize      = 12;
    public double  CharSpacing   = 0;
    public double  WordSpacing   = 0;
    public double  HorizScaling  = 100;
    public double  Leading       = 0;
    public int     TextRenderMode = 0;
    public double  TextRise      = 0;
    public PdfMatrix TextMatrix  = PdfMatrix.Identity;
    public PdfMatrix TextLineMatrix = PdfMatrix.Identity;

    public GfxState Clone() => (GfxState)MemberwiseClone();

    public LineCap  GdiLineCap  => LineCap  switch { 1=>System.Drawing.Drawing2D.LineCap.Round, 2=>System.Drawing.Drawing2D.LineCap.Square, _=>System.Drawing.Drawing2D.LineCap.Flat };
    public LineJoin GdiLineJoin => LineJoin switch { 1=>System.Drawing.Drawing2D.LineJoin.Round, 2=>System.Drawing.Drawing2D.LineJoin.Bevel, _=>System.Drawing.Drawing2D.LineJoin.Miter };

    /// <summary>FillColor with its alpha multiplied by FillAlpha (ExtGState /ca).</summary>
    public Color EffectiveFillColor =>
        FillAlpha >= 0.999 ? FillColor
        : Color.FromArgb((int)(FillColor.A * FillAlpha + 0.5), FillColor);
    /// <summary>StrokeColor with its alpha multiplied by StrokeAlpha (ExtGState /CA).</summary>
    public Color EffectiveStrokeColor =>
        StrokeAlpha >= 0.999 ? StrokeColor
        : Color.FromArgb((int)(StrokeColor.A * StrokeAlpha + 0.5), StrokeColor);
}
