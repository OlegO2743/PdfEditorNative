# PDF Editor Native — Status Report

Дата: 2026-04-17 (обновлено)
Проект: `D:\Projects\C#\PdfEditorNative_31` / `PdfEditorNative.exe` (.NET 8, Windows Forms, x64)

Собственный PDF-рендерер без внешних библиотек.

История изменений по сессиям: см. [HISTORY.md](HISTORY.md).

---

## 1. Что сделано

### 1.1 JPEG 2000 декодер (`Engine/Render/Jpeg2000Decoder.cs`)

Полный собственный декодер JPEG 2000 Part 1 (T.800) — никаких внешних библиотек.

**Поддержано:**
- JP2 container + raw codestream
- SIZ, COD, QCD маркеры
- Tile parts (RLCP progression, LRCP-compatible для 1 слоя)
- Tag trees (inclusion, zero bit-planes)
- Packet parsing, code-block data extraction
- MQ arithmetic decoder (T.800 C.3) со всеми 47 состояниями
- T1 bit-plane coding:
  - Cleanup pass (включая run-length mode)
  - Significance propagation pass
  - Magnitude refinement pass
- Significance context tables D.1 (LL/LH, HL, HH)
- Sign coding (Table D.3)
- Dequantization (scalar, 9-7 wavelet)
- Midpoint reconstruction (+0.5 × step)
- Inverse DWT 9-7 с K-нормализацией
- Inverse ICT (YCbCr → RGB)
- DC level shift

**Критичные баги, которые были найдены и исправлены** (на основе референс-кода OpenJPEG):
1. SigCtx для LL/LH: `V=1` всегда даёт ctx 3 (не условно от D)
2. SigCtx для HH: приоритет по sD (не по h+v)
3. Swap H/V для **HL** (не LH)
4. MQ init: ZC[0] начинает в state 4 (OpenJPEG convention)

Результат: обложка `poradnik2013.pdf` (JPEG 2000) декодируется корректно,
без зернистости и цветных артефактов.

### 1.2 PDF рендеринг (`Engine/Render/GdiRenderer.cs`)

Основной рендерер через GDI+ (`System.Drawing.Graphics`).

**Поддержанные операторы content stream:**
- Graphics state: `q`, `Q`, `cm`, `w`, `J`, `j`, `d`, `gs` (частично)
- Цвет: `G`, `g`, `RG`, `rg`, `K`, `k`, `CS`/`cs` (no-op), `SC`/`SCN`/`sc`/`scn` (по количеству операндов)
- Пути: `m`, `l`, `c`, `v`, `y`, `h`, `re`
- Операции пути: `S`, `s`, `f`, `F`, `f*`, `B`, `B*`, `b`, `b*`, `n`, `W`, `W*`
- Текст: `BT`, `ET`, `Tf`, `Td`, `TD`, `Tm`, `T*`, `Tc`, `Tw`, `Tz`, `TL`, `Tr`, `Ts`, `Tj`, `TJ`, `'`, `"`
- XObject: `Do` (Image + Form), `BI..EI` (inline image)

**Изображения:**
- JPEG (DCTDecode) через GDI+
- JPEG 2000 (JPXDecode) — через наш Jpeg2000Decoder
- Raw + Flate: DeviceGray, DeviceRGB, DeviceCMYK, Indexed (палитра с baseColorSpace)
- ImageMask (1-bit stencil)
- SMask (soft mask / alpha channel) — применяется как альфа

**Линии:**
- Тонкие PDF-линии (`LineWidth ≤ 1.0`) фиксируются на `1 device pixel`
  независимо от zoom — `RenderContext.MakePen`. Это даёт браузеро-подобный
  внешний вид: тонкие разделители таблиц не утолщаются пропорционально zoom.
- `ExtGState` `/CA` (stroke alpha) и `/ca` (fill alpha) читаются и
  применяются к цветам через `EffectiveFillColor`/`EffectiveStrokeColor`.
  Без этого полупрозрачные stroke/fill рисовались как solid black
  (важно для PDF с `gs` + transparency group — таблицы с серой сеткой
  = чёрный fill с alpha=0.149).
- `RenderContext.Stroke` — обычный `G.DrawPath(pen, Path)` без
  `PixelOffsetMode.Half` и без pixel-snap (попытка snap'ить в целые пиксели
  сделала все линии одинаково crisp и убила антиалиас-вариацию; попытка
  Half сдвигала линии на 0.5 и давала extreme разницу «то invisible, то
  solid»; естественный антиалиас без обоих — компромисс).
- Dash patterns с `DashCap.Round` для точек (w=0 dashes)
- В `GdiRenderer` лежит дубликат `MakePen` (dead code, не вызывается) —
  не удалён, чтобы не трогать то что не просили.

### 1.2a CFF Type 2 charstring interpreter (`Engine/Render/Type2Interpreter.cs`)

Написан полный собственный Type 2 интерпретатор per Adobe Tech Note #5177.
CFF-шрифты (CIDFontType0C / Type1C) теперь рисуются как векторные пути —
глифы строятся напрямую из charstring байтов в `GraphicsPath` и заливаются
через `Graphics.FillPath`.

**Что поддержано:**
- Path operators: rmoveto, hmoveto, vmoveto, rlineto, hlineto, vlineto,
  rrcurveto, rcurveline, rlinecurve, vvcurveto, hhcurveto, hvcurveto, vhcurveto
- Flex family: flex, hflex, hflex1, flex1 (разворачиваем в 2 кубика bezier;
  depth threshold игнорируется — для рендера не нужен)
- Stems: hstem, vstem, hstemhm, vstemhm — корректно считаются для hint count,
  сам хинтинг игнорируется (для GDI+ AA не нужен)
- hintmask / cntrmask с implicit vstem из остатка стека
- Subroutines: callsubr / callgsubr / return с правильным bias
  (107 / 1131 / 32768 по count) и защитой от глубины > 10
- endchar включая Type 1 seac-форму (4/5 аргументов)
- Full arithmetic: abs, add, sub, div, neg, mul, sqrt, random
- Stack manipulation: drop, exch, index, roll, dup
- Storage: put / get (32-slot transient array)
- Conditionals: and, or, not, eq, ifelse
- Full CFF number encoding (1/2/3/5 байт + 16.16 fixed)

**Интеграция** (`Engine/Render/GdiRenderer.cs`):
- `FontInfo` получает опциональный `CffInfo?` + кэш `Dictionary<int, GraphicsPath>` по GID
- `CffParser` расширен: отдаёт raw charstrings, global/local subrs, FDArray +
  FdSelect, per-FD default/nominal widths, FontMatrix, CidToGid инверсию
- В `ShowText` — отдельная ветка для CFF: код → CID → GID → charstring → путь
- `DrawCffGlyph` строит матрицу `scale(fontSize/UPM) × fullCtm` и фиксирует
  через `Graphics.Transform` + `FillPath`
- OpenType-обёртка (`OpenTypeBuilder`) остаётся fallback'ом для TrueType
  и для случаев когда CFF parse провалится

**Критичный баг, найденный в процессе:** `GraphicsPath.FillMode` по умолчанию
`Alternate` (even-odd), что у сложных глифов CFF (с перекрывающимися или
self-intersecting контурами) давало ПУСТОЙ рендер. Исправлено
`FillMode = Winding` — non-zero winding соответствует CFF/PostScript конвенции.

**Проверено на:**
- poradnik2013.pdf стр. 5 (TOC с польскими диакритиками + повёрнутый текст)
- poradnik2013.pdf стр. 186 (таблицы со всеми CFF-шрифтами: Arial, ArialBold,
  SwitzerlandCondensed, MicrogrammaD и их bold-вариантами)

### 1.3 Шрифты (`Engine/Render/FontResolver.cs`)

**Поддерживается:**
- Type1, TrueType, Type0 (composite CID)
- Standard encodings: WinAnsi, MacRoman, Standard
- `/Encoding /Differences` массивы
- `/ToUnicode` CMap (bfchar, bfrange)
- `/W` массив (CID widths)
- Mapping PDF имён на системные шрифты (Arial, Times New Roman, Courier New,
  Arial Narrow для condensed вариантов)
- Стили: Bold, Italic из имени шрифта
- Загрузка встроенных TrueType (`/FontFile2`) через `PrivateFontCollection.AddMemoryFont`

**Частично работает:**
- Wrapping CFF (CIDFontType0C / Type1C) из `/FontFile3` в OpenType контейнер
  - Файлы: `CffParser.cs`, `OpenTypeBuilder.cs`
  - Парсятся: numGlyphs, FontBBox, UnitsPerEm, ItalicAngle, charset (форматы 0/1/2),
    ROS/CID detection, widths из Type2 charstrings (с fallback на PDF /W)
  - Собираются OpenType таблицы: CFF, cmap (format 4), head, hhea, hmtx, maxp,
    name, OS/2, post — все с правильными checksums и checkSumAdjustment
  - CID→GID mapping через инверсию charset
  - `Build` в FontResolver.cs вызывает эту обёртку для CFF шрифтов
  - **Ограничение**: глифы рендерятся через GDI+, метрики шрифтов чуть
    отличаются от Chrome/PDFium

### 1.4 PDF-парсер (`Engine/PdfParser.cs`)

(Уже было в проекте, не меняли кардинально)
- Lexer + ParseObject
- XRef (cross-reference)
- Trailer
- Object streams
- FlateDecode, ASCII85Decode, etc.
- Resources, Pages tree

### 1.5 UI (`MainForm.cs`)

(Не трогали кроме auto-open debug строки — она удалена)
- Загрузка PDF
- Список страниц слева
- Основной рендер страницы
- Кнопки: Открыть, Сохранить, Как..., масштаб, поворот, Текст, Найти/Заменить,
  Картинки, Слить, Страница

### 1.6 Инструменты разработки

- **Headless render режим**: `PdfEditorNative.exe --render <pdf> <page> <zoom> <out.png>`
  Создаёт PNG страницы без запуска GUI. Используется для быстрой итерации
  и сравнения с эталоном.
- **Разрешения Claude Code** (`.claude/settings.local.json`):
  - `"defaultMode": "bypassPermissions"` — все команды без запросов
  - Явные разрешения на dotnet, python, rm, grep, ls, cmd и т.д.

---

## 2. Тестовые PDF и статус рендеринга

### 2.1 `PdfEditorNative/poradnik2013.pdf` (188 страниц, польский каталог)

**Полностью корректно:**
- Стр. 1 (обложка): JPEG 2000 декодируется правильно
- Стр. 5 (оглавление): пунктирные точки-заполнители работают
- Стр. 9, 15, 30, 100: тексты, таблицы, польские диакритики
- Стр. 166, 160: графики IRIS/DFR
- Стр. 177, 182, 183, 188: продукты с изображениями, иконки SMask

**С мелкими отличиями от Chrome:**
- Стр. 186: таблицы с данными — метрики шрифтов чуть отличаются от Chrome.
  Наши CFF-обёртки производят валидный OpenType, но GDI+ рендерит шрифты
  иначе, чем PDFium (разный хинтинг, антиалиас, subpixel).
- Отличия:
  - Ширина символов чуть больше/меньше в некоторых местах
  - Толщина штрихов может отличаться
  - Встроенные шрифты: ArialMT, Arial-BoldMT, SwitzerlandCondensed (+Bold),
    MicrogrammaD-MediExte, MicrogrammaD-BoldExte — все CFF CIDFontType0C

### 2.2 Другие PDF в папке `PdfEditorNative/`

- `REVIT 2027 OPTI.pdf`
- `20260409164221915_de640cea-c449-4551-bd85-4b337f334b59.pdf`

Не проверялись подробно в этой сессии. В раннем `diag_output.txt` есть
результаты автоматического анализа.

---

## 3. Что ещё не сделано / ограничения

### 3.1 Шрифты

- **CFF Type 2 interpreter** — РЕАЛИЗОВАН (см. раздел 1.2a).
  Остаётся: `seac` (Type 1 accent composite) — аргументы принимаются,
  но не применяются (нужна рекурсивная композиция base+accent);
  Type 1 charstrings (`/FontFile`) — не декодируются (другой формат).

- Font subsetting, variable fonts — не рассматриваются.

### 3.2 PDF операторы / features

- **Формулы** (встречаются во многих PDF с математикой) — не проверялись
  подробно. С реализованным CFF interpreter это теперь должно работать
  «из коробки» для CFF-шрифтов (CMR, CMSY и т.п.) — проверить на реальных PDF.

- **Patterns** (tile/shading как заливка): `CS /Pattern cs /PatternName scn`
  — сейчас игнорируется.
- **Shadings**: axial, radial — не поддержаны.
- **Transparency group** (`BMC`, `EMC`, soft masks через ExtGState) — частично,
  только SMask на изображениях.
- **Annotations** (ссылки, подписи, формы) — не рендерятся.
- **Structure tree** — не используется.
- **JavaScript** — не поддерживается.
- **Encrypted PDFs** — не поддерживаются.
- **XFA forms** — не поддерживаются.

### 3.3 Изображения

- CCITTFax (`CCITTFaxDecode`) — не поддержан (факсимильные 1-битные).
- JBIG2 (`JBIG2Decode`) — не поддержан.
- Lab, ICCBased color spaces — обрабатываются как RGB (приближение).

### 3.4 UI / редактирование

- Не проверялись: функции Текст, Найти/Заменить, Картинки (редактирование),
  Слить PDF, Страница (добавление/удаление).

---

## 4. Отладочные файлы

Могли остаться в корне проекта (нужно удалить перед production):
- `r_*.png`, `render_*.png` — временные рендеры
- `font_debug.txt`, `chars_debug.txt`, `img_debug.txt`,
  `jp2_debug.txt` — логи отладки
- `test_tile0.j2k` — выделенный codestream для отладки JPEG 2000
- `ref_t1_decode.py`, `fwd_dwt97.py`, `compare_j2k.py` — Python-скрипты для
  верификации JPEG 2000

---

## 5. Следующие задачи (кандидаты)

1. **Проверка CFF interpreter на реальных математических PDF** —
   для шрифтов CMR/CMSY/Symbol/STIX. Сейчас протестирован на
   poradnik2013.pdf (коммерческий каталог с Arial/Microgramma), но не на
   математике.

2. **Сравнение рендера с браузером на разных zoom** — сейчас при высоком
   zoom (>3.0) наши тонкие линии выглядят немного «солиднее» браузерных.
   Причина: мы рендерим bitmap на именно том zoom что запросил пользователь,
   и 1-pixel pen при reasonable sub-pixel смещении даёт более плотный цвет,
   чем браузерный рендер (который делает native-resolution raster + CSS
   zoom). Возможный фикс: рендер всегда на zoom=1 + апскейл битмапа для
   просмотра. Но это ломает резкость текста/растровых изображений при
   zoom'е, так что прежде обсудить — это trade-off по UX.

3. **Shadings / Patterns / Transparency groups** — см. раздел 3.2.

4. **Annotation rendering** — ссылки, подписи форм.

5. **Type 1 charstrings** (`/FontFile`) — для старых PDF, сейчас они
   падают в GDI fallback.

6. **Подчистить dead code** — `GdiRenderer.MakePen` (дубликат, нигде не
   вызывается), неиспользуемая ветка `extended` в `FontResolver.MapToGdi`,
   `CffParser.SkipIndex`.

---

## 6. Полезные ссылки

- Adobe Tech Note #5176 (CFF spec): https://adobe-type-tools.github.io/font-tech-notes/pdfs/5176.CFF.pdf
- Adobe Tech Note #5177 (Type 2 Charstring Format): https://adobe-type-tools.github.io/font-tech-notes/pdfs/5177.Type2.pdf
- ISO 32000-1 (PDF 1.7 spec) — для PDF-операторов и форматов
- T.800 (ITU-T / ISO 15444-1 JPEG 2000) — для JPEG 2000 декодера
- OpenJPEG source (BSD): https://github.com/uclouvain/openjpeg — референс
  для JPEG 2000 bug fixes
