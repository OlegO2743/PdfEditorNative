// Engine/Edit/TextStreamEditor.cs
// Finds text in PDF content streams and replaces it.
// Works by tokenising the stream, finding Tj/TJ operators,
// and substituting the string operands.

using System.Text;
using PdfEditorNative.Engine;

namespace PdfEditorNative.Engine.Edit;

// ── Describes one text occurrence found in a content stream ───────
public sealed class TextOccurrence
{
    public int    StreamByteStart { get; init; }   // byte offset in decoded stream
    public int    StreamByteEnd   { get; init; }
    public string Text            { get; init; } = "";
    public string Operator        { get; init; } = ""; // Tj or TJ
    public float  PageX           { get; init; }
    public float  PageY           { get; init; }
}

// ── Main editor ──────────────────────────────────────────────────
public static class TextStreamEditor
{
    // ── Find all text spans in a decoded content stream ──────────
    public static List<TextOccurrence> FindText(byte[] stream)
    {
        var results = new List<TextOccurrence>();
        var lex     = new PdfLexer(stream);
        var operands= new List<(int start, int end, object tok)>();

        while (!lex.AtEnd)
        {
            lex.SkipWS();
            if (lex.AtEnd) break;
            int tokStart = lex.Position;
            var tok = lex.NextToken();
            if (tok == null) break;
            int tokEnd = lex.Position;

            if (tok is string op)
            {
                if (op == "Tj" && operands.Count > 0)
                {
                    var last = operands[^1];
                    if (last.tok is PdfStr ps)
                        results.Add(new TextOccurrence
                        {
                            StreamByteStart = last.start,
                            StreamByteEnd   = last.end,
                            Text            = Encoding.Latin1.GetString(ps.Bytes),
                            Operator        = "Tj",
                        });
                }
                else if (op == "TJ" && operands.Count > 0)
                {
                    var last = operands[^1];
                    if (last.tok is PdfArray arr)
                    {
                        var sb = new StringBuilder();
                        foreach (var item in arr.Items)
                            if (item is PdfStr ps2) sb.Append(Encoding.Latin1.GetString(ps2.Bytes));
                        results.Add(new TextOccurrence
                        {
                            StreamByteStart = last.start,
                            StreamByteEnd   = last.end,
                            Text            = sb.ToString(),
                            Operator        = "TJ",
                        });
                    }
                }
                operands.Clear();
            }
            else
            {
                operands.Add((tokStart, tokEnd, tok));
            }
        }

        return results;
    }

    // ── Replace a single occurrence with new text ─────────────────
    /// <summary>
    /// Replaces the string operand of the found occurrence in the decoded stream.
    /// Returns the modified stream bytes.
    /// </summary>
    public static byte[] ReplaceText(byte[] stream, TextOccurrence occ, string newText)
    {
        byte[] newBytes   = Encoding.Latin1.GetBytes(newText);
        byte[] replacement = BuildHexString(newBytes);

        var ms = new MemoryStream();
        ms.Write(stream, 0, occ.StreamByteStart);
        ms.Write(replacement);
        ms.Write(stream, occ.StreamByteEnd, stream.Length - occ.StreamByteEnd);
        return ms.ToArray();
    }

    // ── Search and replace all matches of a substring ─────────────
    public static byte[] ReplaceAll(byte[] stream, string search, string replacement)
    {
        if (string.IsNullOrEmpty(search)) return stream;

        var occurrences = FindText(stream);
        // Process in reverse order so byte offsets stay valid
        var toReplace = occurrences
            .Where(o => o.Text.Contains(search, StringComparison.Ordinal))
            .OrderByDescending(o => o.StreamByteStart)
            .ToList();

        foreach (var occ in toReplace)
        {
            string newText = occ.Text.Replace(search, replacement, StringComparison.Ordinal);
            stream = ReplaceText(stream, occ, newText);
        }
        return stream;
    }

    // ── Re-encode as PDF hex string <...> ────────────────────────
    private static byte[] BuildHexString(byte[] data)
    {
        var sb = new StringBuilder("<");
        foreach (byte b in data) sb.Append(b.ToString("X2"));
        sb.Append('>');
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
