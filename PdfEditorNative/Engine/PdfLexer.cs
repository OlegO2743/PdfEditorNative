// Engine/PdfLexer.cs
using System.Text;
namespace PdfEditorNative.Engine;

public sealed class PdfLexer
{
    public readonly byte[] Buf;
    public int Position { get; set; }
    public PdfLexer(byte[] buf, int start=0){Buf=buf;Position=start;}
    public bool AtEnd => Position >= Buf.Length;
    public byte Cur   => Buf[Position];
    private static bool IsWS(byte b) => b==0||b==9||b==10||b==12||b==13||b==32;
    private static bool IsDelim(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
        or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public void SkipWS()
    {
        while (!AtEnd){ if(IsWS(Cur)){Position++;continue;} if(Cur=='%'){SkipLine();continue;} break; }
    }
    public void SkipLine() { while(!AtEnd&&Cur!=10&&Cur!=13)Position++; }
    public byte PeekByte()  => AtEnd ? (byte)0 : Cur;
    public void SkipByte()  { if(!AtEnd)Position++; }

    public object? NextToken()
    {
        SkipWS(); if(AtEnd) return null;
        byte b = Cur;
        if(b=='/') return ReadName();
        if(b=='(') return ReadLitStr();
        if(b=='<'){ if(Position+1<Buf.Length&&Buf[Position+1]=='<'){Position+=2;return "<<";}  return ReadHexStr(); }
        if(b=='>'){ if(Position+1<Buf.Length&&Buf[Position+1]=='>'){Position+=2;return ">>"; } Position++;return ">"; }
        if(b=='['){Position++;return "[";}
        if(b==']'){Position++;return "]";}
        if(b=='-'||b=='+'||b=='.'||(b>='0'&&b<='9')) return ReadNumber();
        return ReadKeyword();
    }

    private PdfName ReadName()
    {
        Position++;
        int s=Position;
        while(!AtEnd&&!IsWS(Cur)&&!IsDelim(Cur))Position++;
        string raw=Encoding.Latin1.GetString(Buf,s,Position-s);
        if(!raw.Contains('#')) return new PdfName(raw);
        var sb=new StringBuilder(raw.Length);
        for(int i=0;i<raw.Length;i++){
            if(raw[i]=='#'&&i+2<raw.Length){sb.Append((char)Convert.ToByte(raw.Substring(i+1,2),16));i+=2;}
            else sb.Append(raw[i]);
        }
        return new PdfName(sb.ToString());
    }

    private PdfStr ReadLitStr()
    {
        Position++;
        var ms=new MemoryStream(); int depth=1;
        while(!AtEnd&&depth>0){
            byte c=Buf[Position++];
            if(c=='\\'&&!AtEnd){ byte e=Buf[Position++];
                if(e>='0'&&e<='7'){ int v=e-'0'; for(int k=0;k<2&&!AtEnd&&Buf[Position]>='0'&&Buf[Position]<='7';k++)v=v*8+(Buf[Position++]-'0'); ms.WriteByte((byte)v); }
                else ms.WriteByte(e switch{(byte)'n'=>10,(byte)'r'=>13,(byte)'t'=>9,(byte)'b'=>8,(byte)'f'=>12,(byte)'\\'=>92,(byte)'('=>40,(byte)')'=>41,_=>e});
            } else if(c=='('){depth++;ms.WriteByte(c);}
              else if(c==')'){depth--;if(depth>0)ms.WriteByte(c);}
              else ms.WriteByte(c);
        }
        return new PdfStr(ms.ToArray());
    }

    private PdfStr ReadHexStr()
    {
        Position++;
        var ms=new MemoryStream();
        while(!AtEnd&&Cur!='>'){
            SkipWS(); if(AtEnd||Cur=='>') break;
            char hi=(char)Buf[Position++]; SkipWS();
            char lo=(!AtEnd&&Cur!='>')?(char)Buf[Position++]:'0';
            try{ms.WriteByte(Convert.ToByte(new string(new[]{hi,lo}),16));}catch{}
        }
        if(!AtEnd) Position++;
        return new PdfStr(ms.ToArray());
    }

    private PdfObj ReadNumber()
    {
        int s=Position; bool real=false;
        if(!AtEnd&&(Cur=='+'||Cur=='-'))Position++;
        while(!AtEnd&&Cur>='0'&&Cur<='9')Position++;
        if(!AtEnd&&Cur=='.'){real=true;Position++;while(!AtEnd&&Cur>='0'&&Cur<='9')Position++;}
        string str=Encoding.Latin1.GetString(Buf,s,Position-s);
        if(real&&double.TryParse(str,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double d))return new PdfReal(d);
        if(int.TryParse(str,out int iv))return new PdfInt(iv);
        return new PdfInt(0);
    }

    public string ReadKeyword()
    {
        int s=Position;
        while(!AtEnd&&!IsWS(Cur)&&!IsDelim(Cur))Position++;
        return Encoding.Latin1.GetString(Buf,s,Position-s);
    }

    public string ReadLine()
    {
        int s=Position;
        while(!AtEnd&&Cur!=10&&Cur!=13)Position++;
        string line=Encoding.Latin1.GetString(Buf,s,Position-s);
        if(!AtEnd&&Cur==13)Position++;
        if(!AtEnd&&Cur==10)Position++;
        return line;
    }
}
