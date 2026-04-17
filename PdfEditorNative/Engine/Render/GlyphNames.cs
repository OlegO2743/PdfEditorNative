// Engine/Render/GlyphNames.cs
// Adobe Glyph List subset covering Latin, Cyrillic (afii), Greek, common symbols.
// Used to decode /Encoding /Differences arrays.

namespace PdfEditorNative.Engine.Render;

public static class GlyphNames
{
    // Returns Unicode codepoint for an Adobe glyph name, or 0 if unknown.
    public static int ToUnicode(string name) =>
        _map.TryGetValue(name, out int cp) ? cp : 0;

    // ── Cyrillic afii codes ────────────────────────────────────────
    // Capital letters А-Я + Ё
    // Small letters а-я + ё
    private static readonly Dictionary<string, int> _map = new()
    {
        // ── Cyrillic uppercase ────────────────────────────────────
        {"afii10017", 0x0410}, {"afii10018", 0x0411}, {"afii10019", 0x0412},
        {"afii10020", 0x0413}, {"afii10021", 0x0414}, {"afii10022", 0x0415},
        {"afii10023", 0x0401}, // Ё
        {"afii10024", 0x0416}, {"afii10025", 0x0417}, {"afii10026", 0x0418},
        {"afii10027", 0x0419}, {"afii10028", 0x041A}, {"afii10029", 0x041B},
        {"afii10030", 0x041C}, {"afii10031", 0x041D}, {"afii10032", 0x041E},
        {"afii10033", 0x041F}, {"afii10034", 0x0420}, {"afii10035", 0x0421},
        {"afii10036", 0x0422}, {"afii10037", 0x0423}, {"afii10038", 0x0424},
        {"afii10039", 0x0425}, {"afii10040", 0x0426}, {"afii10041", 0x0427},
        {"afii10042", 0x0428}, {"afii10043", 0x0429}, {"afii10044", 0x042A},
        {"afii10045", 0x042B}, {"afii10046", 0x042C}, {"afii10047", 0x042D},
        {"afii10048", 0x042E}, {"afii10049", 0x042F},
        // ── Cyrillic lowercase ────────────────────────────────────
        {"afii10065", 0x0430}, {"afii10066", 0x0431}, {"afii10067", 0x0432},
        {"afii10068", 0x0433}, {"afii10069", 0x0434}, {"afii10070", 0x0435},
        {"afii10071", 0x0451}, // ё
        {"afii10072", 0x0436}, {"afii10073", 0x0437}, {"afii10074", 0x0438},
        {"afii10075", 0x0439}, {"afii10076", 0x043A}, {"afii10077", 0x043B},
        {"afii10078", 0x043C}, {"afii10079", 0x043D}, {"afii10080", 0x043E},
        {"afii10081", 0x043F}, {"afii10082", 0x0440}, {"afii10083", 0x0441},
        {"afii10084", 0x0442}, {"afii10085", 0x0443}, {"afii10086", 0x0444},
        {"afii10087", 0x0445}, {"afii10088", 0x0446}, {"afii10089", 0x0447},
        {"afii10090", 0x0448}, {"afii10091", 0x0449}, {"afii10092", 0x044A},
        {"afii10093", 0x044B}, {"afii10094", 0x044C}, {"afii10095", 0x044D},
        {"afii10096", 0x044E}, {"afii10097", 0x044F},
        // ── Other afii ───────────────────────────────────────────
        {"afii10058", 0x042A}, {"afii10059", 0x044A},
        {"afii10060", 0x042A}, {"afii10061", 0x044A},
        {"afii10062", 0x042A}, {"afii10063", 0x044A},
        {"afii10145", 0x040D}, {"afii10146", 0x045D},
        {"afii10193", 0x0449},

        // ── Latin standard names ──────────────────────────────────
        {"space",     0x0020}, {"exclam",    0x0021}, {"quotedbl",  0x0022},
        {"numbersign",0x0023}, {"dollar",    0x0024}, {"percent",   0x0025},
        {"ampersand", 0x0026}, {"quotesingle",0x0027},{"parenleft", 0x0028},
        {"parenright",0x0029}, {"asterisk",  0x002A}, {"plus",      0x002B},
        {"comma",     0x002C}, {"hyphen",    0x002D}, {"period",    0x002E},
        {"slash",     0x002F},
        {"zero",0x30},{"one",0x31},{"two",0x32},{"three",0x33},{"four",0x34},
        {"five",0x35},{"six",0x36},{"seven",0x37},{"eight",0x38},{"nine",0x39},
        {"colon",0x3A},{"semicolon",0x3B},{"less",0x3C},{"equal",0x3D},
        {"greater",0x3E},{"question",0x3F},{"at",0x40},
        {"A",0x41},{"B",0x42},{"C",0x43},{"D",0x44},{"E",0x45},{"F",0x46},
        {"G",0x47},{"H",0x48},{"I",0x49},{"J",0x4A},{"K",0x4B},{"L",0x4C},
        {"M",0x4D},{"N",0x4E},{"O",0x4F},{"P",0x50},{"Q",0x51},{"R",0x52},
        {"S",0x53},{"T",0x54},{"U",0x55},{"V",0x56},{"W",0x57},{"X",0x58},
        {"Y",0x59},{"Z",0x5A},
        {"bracketleft",0x5B},{"backslash",0x5C},{"bracketright",0x5D},
        {"asciicircum",0x5E},{"underscore",0x5F},{"grave",0x60},
        {"a",0x61},{"b",0x62},{"c",0x63},{"d",0x64},{"e",0x65},{"f",0x66},
        {"g",0x67},{"h",0x68},{"i",0x69},{"j",0x6A},{"k",0x6B},{"l",0x6C},
        {"m",0x6D},{"n",0x6E},{"o",0x6F},{"p",0x70},{"q",0x71},{"r",0x72},
        {"s",0x73},{"t",0x74},{"u",0x75},{"v",0x76},{"w",0x77},{"x",0x78},
        {"y",0x79},{"z",0x7A},
        {"braceleft",0x7B},{"bar",0x7C},{"braceright",0x7D},{"asciitilde",0x7E},

        // ── Extended Latin / accented ─────────────────────────────
        {"Agrave",0xC0},{"Aacute",0xC1},{"Acircumflex",0xC2},{"Atilde",0xC3},
        {"Adieresis",0xC4},{"Aring",0xC5},{"AE",0xC6},{"Ccedilla",0xC7},
        {"Egrave",0xC8},{"Eacute",0xC9},{"Ecircumflex",0xCA},{"Edieresis",0xCB},
        {"Igrave",0xCC},{"Iacute",0xCD},{"Icircumflex",0xCE},{"Idieresis",0xCF},
        {"Eth",0xD0},{"Ntilde",0xD1},{"Ograve",0xD2},{"Oacute",0xD3},
        {"Ocircumflex",0xD4},{"Otilde",0xD5},{"Odieresis",0xD6},
        {"multiply",0xD7},{"Oslash",0xD8},{"Ugrave",0xD9},{"Uacute",0xDA},
        {"Ucircumflex",0xDB},{"Udieresis",0xDC},{"Yacute",0xDD},{"Thorn",0xDE},
        {"germandbls",0xDF},
        {"agrave",0xE0},{"aacute",0xE1},{"acircumflex",0xE2},{"atilde",0xE3},
        {"adieresis",0xE4},{"aring",0xE5},{"ae",0xE6},{"ccedilla",0xE7},
        {"egrave",0xE8},{"eacute",0xE9},{"ecircumflex",0xEA},{"edieresis",0xEB},
        {"igrave",0xEC},{"iacute",0xED},{"icircumflex",0xEE},{"idieresis",0xEF},
        {"eth",0xF0},{"ntilde",0xF1},{"ograve",0xF2},{"oacute",0xF3},
        {"ocircumflex",0xF4},{"otilde",0xF5},{"odieresis",0xF6},
        {"divide",0xF7},{"oslash",0xF8},{"ugrave",0xF9},{"uacute",0xFA},
        {"ucircumflex",0xFB},{"udieresis",0xFC},{"yacute",0xFD},{"thorn",0xFE},
        {"ydieresis",0xFF},

        // ── Typographic / special ─────────────────────────────────
        {"endash",    0x2013},{"emdash",     0x2014},
        {"quoteleft", 0x2018},{"quoteright", 0x2019},
        {"quotedblleft",0x201C},{"quotedblright",0x201D},
        {"bullet",    0x2022},{"ellipsis",   0x2026},
        {"perthousand",0x2030},{"guilsinglleft",0x2039},{"guilsinglright",0x203A},
        {"fraction",  0x2044},{"Euro",       0x20AC},
        {"trademark", 0x2122},{"partialdiff",0x2202},{"summation",0x2211},
        {"minus",     0x2212},{"radical",    0x221A},{"infinity",  0x221E},
        {"integral",  0x222B},{"approxequal",0x2248},{"notequal",  0x2260},
        {"lessequal", 0x2264},{"greaterequal",0x2265},
        {"fi",        0xFB01},{"fl",         0xFB02},{"ff",        0xFB00},
        {"ffi",       0xFB03},{"ffl",        0xFB04},
        {"acute",     0x00B4},{"cedilla",   0x00B8},
        {"dieresis",  0x00A8},{"circumflex", 0x02C6},{"tilde",     0x02DC},
        {"macron",    0x00AF},{"breve",      0x02D8},{"dotaccent", 0x02D9},
        {"ring",      0x02DA},{"ogonek",     0x02DB},{"caron",     0x02C7},
        {"hungarumlaut",0x02DD},
        {"copyright", 0x00A9},{"registered",0x00AE},{"section",   0x00A7},
        {"paragraph", 0x00B6},{"dagger",     0x2020},{"daggerdbl", 0x2021},
        {"degree",    0x00B0},{"plusminus",  0x00B1},{"mu",        0x00B5},
        {"onehalf",   0x00BD},{"onequarter", 0x00BC},{"threequarters",0x00BE},
        {"sterling",  0x00A3},{"yen",        0x00A5},{"cent",      0x00A2},
        {"currency",  0x00A4},{"brokenbar",  0x00A6},{"notsign",   0x00AC},
        {"guillemotleft",0x00AB},{"guillemotright",0x00BB},
        {"guillemotleft.cyr",0x00AB},{"guillemotright.cyr",0x00BB},
        {"emdash.cyr",0x2014},
        {"lslash",    0x0142},{"Lslash",     0x0141},
        {"oe",        0x0153},{"OE",         0x0152},
        {"scaron",    0x0161},{"Scaron",     0x0160},
        {"zcaron",    0x017E},{"Zcaron",     0x017D},
        {"florin",    0x0192},

        // ── Greek ─────────────────────────────────────────────────
        {"Alpha",0x0391},{"Beta",0x0392},{"Gamma",0x0393},{"Delta",0x0394},
        {"Epsilon",0x0395},{"Zeta",0x0396},{"Eta",0x0397},{"Theta",0x0398},
        {"Iota",0x0399},{"Kappa",0x039A},{"Lambda",0x039B},{"Mu",0x039C},
        {"Nu",0x039D},{"Xi",0x039E},{"Omicron",0x039F},{"Pi",0x03A0},
        {"Rho",0x03A1},{"Sigma",0x03A3},{"Tau",0x03A4},{"Upsilon",0x03A5},
        {"Phi",0x03A6},{"Chi",0x03A7},{"Psi",0x03A8},{"Omega",0x03A9},
        {"alpha",0x03B1},{"beta",0x03B2},{"gamma",0x03B3},{"delta",0x03B4},
        {"epsilon",0x03B5},{"zeta",0x03B6},{"eta",0x03B7},{"theta",0x03B8},
        {"iota",0x03B9},{"kappa",0x03BA},{"lambda",0x03BB},{"mugreek",0x03BC},
        {"nu",0x03BD},{"xi",0x03BE},{"omicron",0x03BF},{"pi",0x03C0},
        {"rho",0x03C1},{"sigma",0x03C3},{"tau",0x03C4},{"upsilon",0x03C5},
        {"phi",0x03C6},{"chi",0x03C7},{"psi",0x03C8},{"omega",0x03C9},
        {"theta1",0x03D1},{"phi1",0x03D5},{"pi1",0x03D6},
        {"arrowleft",0x2190},{"arrowup",0x2191},{"arrowright",0x2192},
        {"arrowdown",0x2193},
    };
}
