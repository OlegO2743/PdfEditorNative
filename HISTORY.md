# PDF Editor Native — История сессий

Хронологический лог крупных изменений, по сессиям (датированным).
Для текущего состояния см. [STATUS.md](STATUS.md).

Формат записи каждой сессии: **что спрашивали → что сделано → что сломалось и как починено → что осталось**.

---

## Сессия 2026-04-17 (текущая)

### Запрос 1: написать собственный CFF charstring interpreter, рисовать глифы через GraphicsPath

**Контекст.** На момент начала в проекте была OpenType-обёртка над CFF
(`OpenTypeBuilder.cs`) — мы строили валидный .otf из raw CFF и отдавали
GDI+ через `PrivateFontCollection.AddMemoryFont`. Это работало, но метрики
отличались от Chrome/PDFium (другой хинтинг, антиалиас, subpixel).
STATUS.md уже содержал план реализации — см. раздел 5 старой версии.

**Согласованный объём:** «полный Type 2» (все операторы, не только path) +
интеграция «A» (новая ветка для CFF, OpenType-обёртка остаётся fallback'ом).

**Сделано:**

1. **`Engine/Render/CffParser.cs`** — расширен. Было: только метаданные
   (NumGlyphs, FontBBox, CidToGid, GidWidths). Добавлено:
   - `CharStrings byte[][]` — сырые Type 2 байты по GID
   - `GlobalSubrs / LocalSubrs byte[][]` (для non-CID)
   - `LocalSubrsPerFd byte[][][]` + `FdSelect int[]` (для CID)
   - Per-FD `FdDefaultWidthX` / `FdNominalWidthX` + non-CID варианты
   - `FontMatrix double[6]` + пересчёт `UnitsPerEm` из FontMatrix[0]
   - `CidToGid` как инверсия `GidToCid`
   - Новая helper `ReadPrivate` — читает Private DICT + локальные subrs
     (subrs offset относителен началу Private DICT)
   - `ExtractGidWidths` упрощён — работает теперь на уже разобранных полях

2. **`Engine/Render/Type2Interpreter.cs`** — новый файл, ~400 строк.
   Полный интерпретатор per Adobe Tech Note #5177:
   - Path: rmoveto/hmoveto/vmoveto, rlineto, hlineto, vlineto (с альтернированием),
     rrcurveto, rcurveline, rlinecurve, vvcurveto, hhcurveto, hvcurveto, vhcurveto
   - Flex: flex (12 35), hflex (12 34), hflex1 (12 36), flex1 (12 37) —
     все разворачиваются в 2 кубика bezier, threshold игнорируется
   - Stems / hints: hstem, vstem, hstemhm, vstemhm, hintmask, cntrmask
     с implicit vstem для остатка стека
   - Subroutines: callsubr, callgsubr, return с bias (107/1131/32768) и
     защитой глубины ≤ 10
   - endchar с корректной обработкой width (0/1/5 arg) + игнор seac 4-arg
   - Arithmetic: abs, add, sub, div, neg, mul, sqrt, random
   - Stack: drop, exch, index, roll, dup
   - Storage: put/get, 32-slot transient array
   - Conditionals: and, or, not, eq, ifelse
   - CFF number encoding включая 16.16 fixed (байт 255)

   **Width handling** — первый операнд hstem/vstem/moveto/endchar, если
   parity не совпадает с нормальной (odd для stems, normal+1 для arity ops).
   Защитный `_widthParsed` flag.

3. **`Engine/Render/FontResolver.cs`** — изменения:
   - `FontInfo` получил `Cff : CffInfo?` + `CffPathCache : Dictionary<int, GraphicsPath?>`
   - `TryLoadEmbeddedFont` вернула `out CffInfo? cffInfo` — при парсинге
     CFF (`/FontFile3 /Subtype /CIDFontType0C` или `/Type1C`) также вызываем
     `CffParser.Parse` и сохраняем
   - В `Build()` — сохраняем CffInfo в FontInfo ТОЛЬКО если `IsCid == true`
     (non-CID CFF требует name→SID→GID через encoding, пока не реализовано)

4. **`Engine/Render/GdiRenderer.cs`** — интеграция:
   - В `ShowText()` — новая ветка если `fi.Cff != null`:
     код → CID (== code для Identity-H) → GID (через CidToGid) →
     `DrawCffGlyph` + advance через `GidWidths`
   - `DrawCffGlyph` — берёт/строит `GraphicsPath` через Type2Interpreter,
     кэширует в `CffPathCache`, строит матрицу
     `scale(fontSize/UnitsPerEm) × fullCtm`, `Graphics.Transform = m`,
     `FillPath(fillColor)`. Для text render mode 1/2/5/6 дополнительно
     `DrawPath` с pen шириной 20 (в font units)
   - Save/Restore `ctx.G` вокруг Transform изменений

**Баг №1 (найден и починен):** Первый рендер показал, что многие глифы
прозрачны/пусты, хотя path-ы строились (log показал pts=60+ для «R», «B» и т.п.,
но они не отрисовывались). **Причина:** `GraphicsPath.FillMode` по умолчанию
`Alternate` (even-odd), а CFF/PostScript использует non-zero winding. У сложных
глифов с перекрывающимися контурами even-odd даёт схлопывание в пустоту.
**Фикс:** `Path = new() { FillMode = FillMode.Winding }`. Сразу всё заработало.

**Проверка:**
- `poradnik2013.pdf` стр. 5 — TOC с польскими диакритиками + повёрнутый
  вертикальный текст на правом крае → рендерится полностью корректно
- `poradnik2013.pdf` стр. 186 — сложные таблицы со всеми CFF шрифтами
  (ArialMT, Arial-BoldMT, SwitzerlandCondensed, MicrogrammaD-MediExte, etc.) →
  весь текст читается, совпадает с браузерным рендером

### Запрос 2: некоторые линии серые / полупрозрачные vs браузер

**Контекст.** На PDF `запчасти форд фиеста сокращенно - Лист1 (4).pdf`
пользователь заметил, что часть горизонтальных линий таблицы рендерится
слабее чем другие. Первая гипотеза была: `MakePen` имеет hairline-clamp
к 0.5px, который даёт 50%-прозрачную линию при антиалиасе.

**Расследование 1:** дамп всех `Stroke()` вызовов показал — только ОДИН
stroke call на всей странице, 48 точек, penW=1.0. Все «тонкие» линии —
это составной путь (~12 прямоугольников) в одном DrawPath.

**Расследование 2:** дамп content stream PDF через Python:
- Только `m`/`l` операторы (нет `re`)
- 24 line segments (16 horizontal, 8 vertical), все на PDF координатах `.5`
- Один `w` (=1.0), один `0 0 0 RG`, один `S`

Т.е. PDF сам по себе рисует все линии одним штрихом одного цвета.
**Разница серостей в браузере — ровно от sub-pixel antialiasing**:
после цепочки CTM (0.75 × 0.847 × 1.0 ≈ 0.635) × zoom, координаты `n.5`
уходят в дробные device-пиксели, но НЕ все одинаково — у одних смещение
ближе к 0, у других к 0.5. Антиалиас покрывает 1px vs 2px.

**Попытка фикса №1:** Включил `PixelOffsetMode.Half` + снэп точек пути в
целые device coords (`SnapToPixels` с проверкой на отсутствие Bezier'ов).
Результат: ВСЕ линии стали одинаково crisp 1px. Пользователь сказал
«теперь одинаковые» но потом показал браузер где есть естественная
градация → «все ещё отличается, верни вариацию но без exremes».

**Финальный фикс:** убрал и снэп и `PixelOffsetMode.Half`. `Stroke()` теперь
просто `G.DrawPath(pen, Path)`. Антиалиас работает естественно — где
линия попадает на пиксель-центр, она crisp; где между — серее.
Результат при `zoom=1` совпадает с браузерным видом.

**Остаточный вопрос:** при высоком zoom (>3.0) наши линии выглядят
«плотнее» браузерных. Причина в том, что в нашем коде `MakePen` даёт
pen width = 1px (независимо от zoom) — это значит, что на высоком
zoom 1-pixel pen не утолщается. НО! антиалиасный sub-pixel эффект
ослабевает с ростом zoom: при zoom=1 линия размазана на 0.6px (видно
серость); при zoom=4 линия тоже 1px (по pen width), но subpixel shift
0.6*4=2.4 = «почти целый пиксель смещения», и antialias покрывает 1
полный + один почти полный = 2 пикселя но почти сплошь. Выглядит плотнее.

Это принципиальное ограничение подхода «pen width = fixed 1px at all zooms».
Альтернатива — рендерить на zoom=1 всегда и скейлить битмап при UI zoom.
Но это ухудшит резкость текста и растровых изображений. Оставили как есть;
записано в STATUS.md раздел 5 как кандидат на будущее.

**Примечание:** в `GdiRenderer.cs` всё ещё лежит дубликат `MakePen`
(два метода с тем же именем — в `GdiRenderer` class и в `RenderContext`).
`GdiRenderer.MakePen` dead code (нигде не вызывается). Оставлен нетронутым
(сурджикальные правки — не трогаем то, что не просили).

### Запрос 3: запиши историю в md-файлы

Создан этот файл + обновлён STATUS.md.

### Запрос 4: в браузере серые линии сетки, у нас только чёрные рамки

**Контекст.** Пользователь показал скриншот браузера на 100% и 375% zoom.
Видно ЧЁТКО ДВА типа линий:
- **Чёрные толстые** — рамки вокруг групп ячеек
- **Очень светло-серые (~15%)** — сетка между всеми строками, как в Excel

Мой предыдущий анализ content stream нашёл ТОЛЬКО ОДИН `S` (stroke) с чёрным
1pt. Решил что серость — это антиалиас, и сделал правку `Stroke()` без
`PixelOffsetMode.Half` (оставив естественный антиалиас). Но пользователь
показал что серой сетки у нас вообще НЕТ.

**Расследование (повторное, глубже):**
1. Page объект (`5 0 obj`) имеет `/Contents 6 0 R` + `/Resources 7 0 R` +
   `/Group << /S /Transparency /CS /DeviceRGB >>` — прозрачность включена.
2. Resources (`7 0 obj`) содержит `/ExtGState << /Alpha0 10 0 R >>`.
3. **Obj 10** (ExtGState Alpha0):
   ```
   << /CA 0.14901961 /ca 0.14901961 >>
   ```
   CA = stroke alpha, ca = fill alpha = **15%**.
4. Content stream содержит `/Alpha0 gs` — значит серая сетка рисуется
   под этим extGState с 15% прозрачности. Это не stroke, а **заливка
   тонких прямоугольников чёрным с alpha=0.149** → визуально светло-серый.

**Баг в коде:** `ApplyExtGState` читал только `/LW` (line width),
игнорировал `/CA` и `/ca`. Поэтому все drawing с `/Alpha0 gs` рисовались
с alpha=1.0 — поверх них потом шли белые заливки белых ячеек и серые
линии исчезали вообще.

**Фикс.** В нескольких местах:
1. **`GraphicsState.cs`** — добавлены `StrokeAlpha` и `FillAlpha` (по
   умолчанию 1.0). Plus helpers `EffectiveFillColor` / `EffectiveStrokeColor`,
   которые берут `Color` и домножают его `A` канал на соответствующий alpha.
2. **`GdiRenderer.cs ApplyExtGState`** — читает `/CA` → `StrokeAlpha`,
   `/ca` → `FillAlpha`.
3. **`GdiRenderer.cs`** — все места где создавался `SolidBrush` и `Pen`
   из `FillColor`/`StrokeColor` переведены на `EffectiveFillColor`/
   `EffectiveStrokeColor`:
   - `RenderContext.Fill`, `FillStroke` (заливка)
   - `RenderContext.MakePen` (обводка)
   - `DrawGlyph` (TrueType текст)
   - `DrawCffGlyph` (CFF текст)
4. `q`/`Q` (push/pop state) уже корректно сохраняют/восстанавливают alpha —
   `GfxState.Clone` использует `MemberwiseClone` который копирует всё.

**Результат.** Рендер фиесты теперь визуально совпадает с браузером:
светло-серая сетка + чёрные рамки групп. Проверено на `zoom=1.5`.

**Pitfall замечен:** мой первоначальный анализ PDF был слишком
поверхностным. Я нашёл ОДИН stroke и решил что задача «объяснить
разницу серостей через антиалиас». Настоящая причина была в
необработанной transparency — нужно было с первого захода проверить
ExtGState и обработку `gs` operator.

### Запрос 5: удалить все упоминания о SkiaSharp

**Контекст.** По STATUS.md раздел 4.1 — был архивный эксперимент с заменой GDI+
на SkiaSharp. Созданный `SkiaRenderer.cs` не использовался (`Program.cs` рендерит
через `GdiRenderer`), но NuGet-пакеты `SkiaSharp` 3.119.2 и
`SkiaSharp.NativeAssets.Win32` 3.119.2 оставались в зависимостях, как и
`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` в csproj.

**Сделано:**

1. Удалён файл `PdfEditorNative/Engine/Render/SkiaRenderer.cs` (940 строк).
2. `PdfEditorNative.csproj`:
   - Убраны обе `PackageReference` на SkiaSharp + весь `<ItemGroup>` с ними.
   - Убран `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` — `unsafe`-блоки были
     только в SkiaRenderer.cs. В `Jpeg2000Decoder.cs` есть
     `stackalloc` с `Span<int>`, но это не требует unsafe-контекста.
3. `STATUS.md`:
   - В шапке убрана ремарка про SkiaSharp в скобках.
   - Удалён раздел «### 4.1 SkiaSharp branch (не используется)» целиком.
   - Прежний «### 4.2 Отладочные файлы» поднят до «## 4. Отладочные файлы»,
     из списка дебаг-файлов убран `skia_debug.txt`.
   - В списке кандидатов (раздел 5) п. 6 переписан: вместо «удалить
     архивные эксперименты» — подчистить оставшийся dead code
     (`GdiRenderer.MakePen`, `FontResolver` ветка `extended`,
     `CffParser.SkipIndex`).

**Решения / обоснование:**
- `AllowUnsafeBlocks` убран, потому что единственное место `unsafe` было в
  удалённом файле. `Span<int> stackalloc` в C# 7.2+ разрешён в safe-контексте.
- Комментарий `<!-- Zero NuGet packages — all PDF work done from scratch -->`
  в csproj оставлен на месте — теперь он соответствует действительности.
- SkiaRenderer не оставлен «как референс» — Git-история (если когда-нибудь
  проект в Git попадёт) или старая версия STATUS.md содержит описание
  того подхода; хранить 940 строк мёртвого кода ради референса — overkill.

**Проверено:** `dotnet build` проходит без ошибок (см. ниже).

---

## До этой сессии (суммарно из старого STATUS.md)

- JPEG 2000 декодер (`Jpeg2000Decoder.cs`) — полный, все baseline-фичи T.800
- PDF content stream рендер (`GdiRenderer.cs`) — основные операторы q/Q/cm,
  path builds, text, XObjects, images
- OpenType-обёртка над CFF (`OpenTypeBuilder.cs` + `CffParser.cs` metadata
  extraction) — для загрузки CFF шрифтов через GDI+ `PrivateFontCollection`
- Headless CLI: `--render <pdf> <page> <zoom> <out.png>` (в `Program.cs`)
- FontResolver: имена → системные шрифты, /ToUnicode, /W, Differences
- Базовый GUI в WinForms: `MainForm.cs`

Эти компоненты в этой сессии не трогали.
