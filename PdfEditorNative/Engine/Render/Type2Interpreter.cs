// Full Type 2 charstring interpreter per Adobe Tech Note #5177.
// Consumes CFF charstring bytes and produces a GDI+ GraphicsPath in font units
// (Y-up, origin at glyph baseline/left-sidebearing).
//
// Coordinate convention: the returned path is in CFF font units (typically 1000
// UPM). Y axis points UP. The caller is responsible for scaling by fontSize/UPM
// and composing with the PDF CTM (which already flips Y for screen rendering).

using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;

namespace PdfEditorNative.Engine.Render;

internal static class Type2Interpreter
{
    public static GraphicsPath? BuildGlyphPath(CffInfo cff, int gid)
    {
        if (cff.CharStrings == null || gid < 0 || gid >= cff.CharStrings.Length) return null;
        var cs = cff.CharStrings[gid];
        if (cs == null || cs.Length == 0) return null;

        byte[][] localSubrs = cff.LocalSubrs;
        if (cff.FdSelect != null && gid < cff.FdSelect.Length)
        {
            int fd = cff.FdSelect[gid];
            if (fd >= 0 && fd < cff.LocalSubrsPerFd.Length)
                localSubrs = cff.LocalSubrsPerFd[fd];
        }

        var vm = new Vm(cff.GlobalSubrs, localSubrs);
        try { vm.Run(cs); }
        catch { /* tolerate malformed charstrings */ }
        vm.CloseFigure();
        return vm.Path;
    }

    // Subroutine index bias per Type 2 spec, based on subr count.
    private static int Bias(int count) =>
        count < 1240 ? 107 : (count < 33900 ? 1131 : 32768);

    private sealed class Vm
    {
        private readonly byte[][] _gs;
        private readonly byte[][] _ls;
        private readonly int _gBias;
        private readonly int _lBias;
        private readonly List<double> _st = new(48);
        private readonly double[] _tr = new double[32];
        public readonly GraphicsPath Path = new() { FillMode = FillMode.Winding };
        private float _x, _y;
        private bool _widthParsed;
        private int _hintCount;
        private bool _openFig;
        private int _depth;
        private bool _stop;
        private static readonly Random _rng = new();

        public Vm(byte[][] globalSubrs, byte[][] localSubrs)
        {
            _gs = globalSubrs; _ls = localSubrs;
            _gBias = Bias(_gs.Length);
            _lBias = Bias(_ls.Length);
        }

        public void CloseFigure()
        {
            if (_openFig) { Path.CloseFigure(); _openFig = false; }
        }

        // --- Execute a charstring (or subroutine body). ---
        public void Run(byte[] code)
        {
            if (_depth > 10) return;  // per spec, max call depth is 10
            _depth++;
            int i = 0;
            while (i < code.Length && !_stop)
            {
                byte b = code[i];
                // Operand (number) markers: 28, 32-255
                if (b >= 32 || b == 28 || b == 255)
                {
                    _st.Add(ReadNumber(code, ref i));
                    continue;
                }
                // Operator byte
                int op = b; i++;
                if (b == 12)
                {
                    if (i >= code.Length) break;
                    op = 0x0C00 | code[i]; i++;
                }
                // hintmask / cntrmask consume following mask bytes
                if (op == 19 || op == 20)
                {
                    // Implicit vstem(hm): if the stack has pending pairs, they count as stems
                    if (_st.Count >= 2)
                    {
                        TakeWidthStems();
                        _hintCount += _st.Count / 2;
                        _st.Clear();
                    }
                    i += (_hintCount + 7) / 8;
                    continue;
                }
                // return ends the current subroutine
                if (op == 11) { _depth--; return; }
                Dispatch(op);
            }
            _depth--;
        }

        // --- Operator dispatch. ---
        private void Dispatch(int op)
        {
            switch (op)
            {
                // Stems
                case 1: case 3: case 18: case 23:
                    TakeWidthStems();
                    _hintCount += _st.Count / 2;
                    _st.Clear();
                    break;

                // Moveto
                case 21: TakeWidthArity(2); MoveTo(Get(0), Get(1)); _st.Clear(); break; // rmoveto
                case 22: TakeWidthArity(1); MoveTo(Get(0), 0);      _st.Clear(); break; // hmoveto
                case 4:  TakeWidthArity(1); MoveTo(0, Get(0));      _st.Clear(); break; // vmoveto

                // Lineto
                case 5: // rlineto
                    for (int k = 0; k + 1 < _st.Count; k += 2) LineTo(_st[k], _st[k + 1]);
                    _st.Clear(); break;
                case 6: // hlineto: H,V,H,V...
                    for (int k = 0; k < _st.Count; k++)
                        if ((k & 1) == 0) LineTo(_st[k], 0); else LineTo(0, _st[k]);
                    _st.Clear(); break;
                case 7: // vlineto: V,H,V,H...
                    for (int k = 0; k < _st.Count; k++)
                        if ((k & 1) == 0) LineTo(0, _st[k]); else LineTo(_st[k], 0);
                    _st.Clear(); break;

                // Curveto
                case 8: // rrcurveto
                    for (int k = 0; k + 5 < _st.Count; k += 6)
                        CurveTo(_st[k], _st[k+1], _st[k+2], _st[k+3], _st[k+4], _st[k+5]);
                    _st.Clear(); break;
                case 24: // rcurveline
                    {
                        int k = 0;
                        while (k + 8 <= _st.Count)  // leave 2 for final line
                        { CurveTo(_st[k], _st[k+1], _st[k+2], _st[k+3], _st[k+4], _st[k+5]); k += 6; }
                        if (k + 1 < _st.Count) LineTo(_st[k], _st[k+1]);
                        _st.Clear();
                    } break;
                case 25: // rlinecurve
                    {
                        int k = 0;
                        while (k + 8 <= _st.Count)  // leave 6 for final curve
                        { LineTo(_st[k], _st[k+1]); k += 2; }
                        if (k + 5 < _st.Count)
                            CurveTo(_st[k], _st[k+1], _st[k+2], _st[k+3], _st[k+4], _st[k+5]);
                        _st.Clear();
                    } break;
                case 26: VvCurveTo(); break;
                case 27: HhCurveTo(); break;
                case 30: HvAlternatingCurveTo(startH: false); break; // vhcurveto
                case 31: HvAlternatingCurveTo(startH: true);  break; // hvcurveto

                // Subroutines
                case 10: CallSubr(local: true);  break;
                case 29: CallSubr(local: false); break;

                // Endchar
                case 14:
                    if (!_widthParsed)
                    {
                        _widthParsed = true;
                        // width may precede endchar alone (count==1) or the Type1 seac form
                        // (count==5). Normal forms: 0 args or 4 args (seac without width).
                        if (_st.Count == 1 || _st.Count == 5) _st.RemoveAt(0);
                    }
                    CloseFigure();
                    _stop = true;
                    break;

                // Flex family (two-byte ops)
                case 0x0C22: Hflex();  break;   // 12 34
                case 0x0C23: Flex();   break;   // 12 35
                case 0x0C24: Hflex1(); break;   // 12 36
                case 0x0C25: Flex1();  break;   // 12 37

                // Arithmetic
                case 0x0C09: Push(Math.Abs(Pop())); break;                                   // abs
                case 0x0C0A: { double b = Pop(), a = Pop(); Push(a + b); } break;            // add
                case 0x0C0B: { double b = Pop(), a = Pop(); Push(a - b); } break;            // sub
                case 0x0C0C: { double b = Pop(), a = Pop(); Push(b == 0 ? 0 : a / b); } break; // div
                case 0x0C0E: Push(-Pop()); break;                                            // neg
                case 0x0C17: Push(_rng.NextDouble()); break;                                 // random
                case 0x0C18: { double b = Pop(), a = Pop(); Push(a * b); } break;            // mul
                case 0x0C1A: Push(Math.Sqrt(Pop())); break;                                  // sqrt

                // Stack manipulation
                case 0x0C12: if (_st.Count > 0) _st.RemoveAt(_st.Count - 1); break;          // drop
                case 0x0C1C: { double b = Pop(), a = Pop(); Push(b); Push(a); } break;       // exch
                case 0x0C1D:                                                                  // index
                    {
                        int j = (int)Pop();
                        if (j < 0) j = 0;
                        int si = _st.Count - 1 - j;
                        if (si < 0) si = 0;
                        if (_st.Count > 0) Push(_st[si]);
                    } break;
                case 0x0C1E:                                                                  // roll
                    {
                        int j = (int)Pop(), n = (int)Pop();
                        Roll(n, j);
                    } break;
                case 0x0C1B: if (_st.Count > 0) Push(_st[_st.Count - 1]); break;             // dup

                // Storage
                case 0x0C14:                                                                  // put
                    {
                        int idx = (int)Pop(); double v = Pop();
                        if (idx >= 0 && idx < _tr.Length) _tr[idx] = v;
                    } break;
                case 0x0C15:                                                                  // get
                    {
                        int idx = (int)Pop();
                        Push(idx >= 0 && idx < _tr.Length ? _tr[idx] : 0);
                    } break;

                // Conditionals
                case 0x0C03: { double b = Pop(), a = Pop(); Push(a != 0 && b != 0 ? 1 : 0); } break; // and
                case 0x0C04: { double b = Pop(), a = Pop(); Push(a != 0 || b != 0 ? 1 : 0); } break; // or
                case 0x0C05: Push(Pop() == 0 ? 1 : 0); break;                                        // not
                case 0x0C0F: { double b = Pop(), a = Pop(); Push(a == b ? 1 : 0); } break;           // eq
                case 0x0C16:                                                                          // ifelse
                    {
                        double v2 = Pop(), v1 = Pop(), s2 = Pop(), s1 = Pop();
                        Push(v1 <= v2 ? s1 : s2);
                    } break;
            }
        }

        // --- vvcurveto (26): all-vertical curve starts (optional dx1 for first curve). ---
        private void VvCurveTo()
        {
            int k = 0;
            double dxa = 0;
            if ((_st.Count & 1) == 1) { dxa = _st[0]; k = 1; }
            while (k + 3 < _st.Count)
            {
                CurveTo(dxa, _st[k], _st[k + 1], _st[k + 2], 0, _st[k + 3]);
                dxa = 0;
                k += 4;
            }
            _st.Clear();
        }

        // --- hhcurveto (27): all-horizontal curve starts (optional dy1 for first curve). ---
        private void HhCurveTo()
        {
            int k = 0;
            double dya = 0;
            if ((_st.Count & 1) == 1) { dya = _st[0]; k = 1; }
            while (k + 3 < _st.Count)
            {
                CurveTo(_st[k], dya, _st[k + 1], _st[k + 2], _st[k + 3], 0);
                dya = 0;
                k += 4;
            }
            _st.Clear();
        }

        // --- hvcurveto (31) / vhcurveto (30): alternating H,V or V,H start tangents.
        // Args grouped in 4s. If one arg left over, it is the perpendicular end offset
        // (dxf or dyf) of the LAST curve only. ---
        private void HvAlternatingCurveTo(bool startH)
        {
            int n = _st.Count;
            int extraIdx = (n % 4 == 1) ? n - 1 : -1;
            int effN = extraIdx >= 0 ? extraIdx : n;
            bool horiz = startH;
            int i = 0;
            while (i + 4 <= effN)
            {
                bool last = (i + 4 == effN);
                double a = _st[i], b = _st[i + 1], c = _st[i + 2], d = _st[i + 3];
                double perp = (last && extraIdx >= 0) ? _st[extraIdx] : 0;
                if (horiz)
                    CurveTo(a, 0, b, c, perp, d);   // start horizontal, end vertical
                else
                    CurveTo(0, a, b, c, d, perp);   // start vertical,   end horizontal
                horiz = !horiz;
                i += 4;
            }
            _st.Clear();
        }

        // --- Flex operators (emitted as two cubic beziers; depth threshold ignored). ---
        private void Flex()
        {
            if (_st.Count < 12) { _st.Clear(); return; }
            CurveTo(_st[0], _st[1], _st[2], _st[3], _st[4], _st[5]);
            CurveTo(_st[6], _st[7], _st[8], _st[9], _st[10], _st[11]);
            _st.Clear();
        }
        private void Hflex()
        {
            if (_st.Count < 7) { _st.Clear(); return; }
            double dx1 = _st[0], dx2 = _st[1], dy2 = _st[2], dx3 = _st[3];
            double dx4 = _st[4], dx5 = _st[5], dx6 = _st[6];
            CurveTo(dx1, 0, dx2, dy2, dx3, 0);
            CurveTo(dx4, 0, dx5, -dy2, dx6, 0);
            _st.Clear();
        }
        private void Hflex1()
        {
            if (_st.Count < 9) { _st.Clear(); return; }
            double dx1 = _st[0], dy1 = _st[1], dx2 = _st[2], dy2 = _st[3], dx3 = _st[4];
            double dx4 = _st[5], dx5 = _st[6], dy5 = _st[7], dx6 = _st[8];
            double dy6 = -(dy1 + dy2 + dy5);  // ends on starting Y
            CurveTo(dx1, dy1, dx2, dy2, dx3, 0);
            CurveTo(dx4, 0,   dx5, dy5, dx6, dy6);
            _st.Clear();
        }
        private void Flex1()
        {
            if (_st.Count < 11) { _st.Clear(); return; }
            double dx1 = _st[0], dy1 = _st[1], dx2 = _st[2], dy2 = _st[3];
            double dx3 = _st[4], dy3 = _st[5], dx4 = _st[6], dy4 = _st[7];
            double dx5 = _st[8], dy5 = _st[9], d6 = _st[10];
            double dxT = dx1 + dx2 + dx3 + dx4 + dx5;
            double dyT = dy1 + dy2 + dy3 + dy4 + dy5;
            double dx6, dy6;
            // d6 is whichever axis had the larger total; the other closes back to start.
            if (Math.Abs(dxT) > Math.Abs(dyT)) { dx6 = d6; dy6 = -dyT; }
            else                               { dy6 = d6; dx6 = -dxT; }
            CurveTo(dx1, dy1, dx2, dy2, dx3, dy3);
            CurveTo(dx4, dy4, dx5, dy5, dx6, dy6);
            _st.Clear();
        }

        private void CallSubr(bool local)
        {
            if (_st.Count == 0) return;
            int idx = (int)_st[_st.Count - 1]; _st.RemoveAt(_st.Count - 1);
            byte[][] arr = local ? _ls : _gs;
            int bias = local ? _lBias : _gBias;
            int n = idx + bias;
            if (n < 0 || n >= arr.Length) return;
            Run(arr[n]);
        }

        // --- Path building (all in CFF font units, Y-up). ---
        private void MoveTo(double dx, double dy)
        {
            if (_openFig) { Path.CloseFigure(); _openFig = false; }
            _x += (float)dx; _y += (float)dy;
            Path.StartFigure();
            _openFig = true;
        }
        private void LineTo(double dx, double dy)
        {
            float x0 = _x, y0 = _y;
            _x += (float)dx; _y += (float)dy;
            Path.AddLine(x0, y0, _x, _y);
            _openFig = true;
        }
        private void CurveTo(double dxa, double dya, double dxb, double dyb, double dxc, double dyc)
        {
            float x0 = _x, y0 = _y;
            float x1 = x0 + (float)dxa, y1 = y0 + (float)dya;
            float x2 = x1 + (float)dxb, y2 = y1 + (float)dyb;
            float x3 = x2 + (float)dxc, y3 = y2 + (float)dyc;
            Path.AddBezier(x0, y0, x1, y1, x2, y2, x3, y3);
            _x = x3; _y = y3;
            _openFig = true;
        }

        // --- Width handling. ---
        // Width is the first operand only on the FIRST hint/moveto/endchar encountered.
        // Arity-specific operators: width present iff stack count == normalArgs + 1.
        // Stem operators: normally even (pairs); width makes it odd.
        private void TakeWidthArity(int normalArgs)
        {
            if (_widthParsed) return;
            _widthParsed = true;
            if (_st.Count == normalArgs + 1) _st.RemoveAt(0);
        }
        private void TakeWidthStems()
        {
            if (_widthParsed) return;
            _widthParsed = true;
            if ((_st.Count & 1) == 1) _st.RemoveAt(0);
        }

        // --- Stack helpers. ---
        private double Pop()
        {
            int n = _st.Count;
            if (n == 0) return 0;
            double v = _st[n - 1];
            _st.RemoveAt(n - 1);
            return v;
        }
        private void Push(double v) => _st.Add(v);
        private double Get(int i) => i < _st.Count ? _st[i] : 0;

        // Roll top N values by J positions. Positive J: top wraps to bottom.
        private void Roll(int n, int j)
        {
            if (n <= 0 || n > _st.Count) return;
            int st = _st.Count - n;
            j %= n; if (j < 0) j += n;
            if (j == 0) return;
            var tmp = new double[n];
            for (int k = 0; k < n; k++) tmp[k] = _st[st + k];
            for (int k = 0; k < n; k++) _st[st + k] = tmp[(k - j + n) % n];
        }

        // --- CFF number encoding (same as DICT encoding except for 29 = long int which
        // is not used in charstrings; 255 is fixed 16.16 in charstrings). ---
        private static double ReadNumber(byte[] code, ref int i)
        {
            byte b = code[i];
            if (b >= 32 && b <= 246) { i++; return b - 139; }
            if (b >= 247 && b <= 250 && i + 1 < code.Length)
            { int b1 = code[i + 1]; i += 2; return (b - 247) * 256 + b1 + 108; }
            if (b >= 251 && b <= 254 && i + 1 < code.Length)
            { int b1 = code[i + 1]; i += 2; return -(b - 251) * 256 - b1 - 108; }
            if (b == 28 && i + 2 < code.Length)
            { int v = (short)((code[i + 1] << 8) | code[i + 2]); i += 3; return v; }
            if (b == 255 && i + 4 < code.Length)
            {
                int v = (code[i + 1] << 24) | (code[i + 2] << 16) | (code[i + 3] << 8) | code[i + 4];
                i += 5; return v / 65536.0;
            }
            i++; return 0;
        }
    }
}
