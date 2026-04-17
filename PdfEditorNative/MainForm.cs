// MainForm.cs
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using PdfEditorNative.Engine;
using PdfEditorNative.Engine.Render;
using PdfEditorNative.Engine.Edit;

namespace PdfEditorNative;

public partial class MainForm : Form
{
    private string?   _filePath;
    private byte[]    _fileBytes = Array.Empty<byte>();
    private PdfParser?    _parser;
    private GdiRenderer?  _renderer;
    private int     _pageCount=0, _currentPage=0;
    private float   _zoom=1.5f;
    private Bitmap? _pageBitmap;
    private double  _pageW, _pageH;
    private bool    _addTextMode=false;
    private Color   _annColor=Color.DarkRed;
    private List<TextOccurrence> _textOccs=new();
    private List<PageImage>      _pageImgs=new();

    // Controls
    private ToolStrip   _tb=null!;
    private StatusStrip _st=null!;
    private ToolStripStatusLabel _lblSt=null!,_lblPg=null!,_lblZm=null!;
    private Panel  _left=null!,_right=null!,_scHost=null!,_scInner=null!,_annPanel=null!;
    private ListBox _thumbs=null!;
    private PictureBox _pb=null!;
    private TabControl _tabs=null!;
    private DataGridView _grid=null!;
    private ListView _lvImg=null!;
    private ImageList _iList=null!;
    private TextBox _annTxt=null!;
    private NumericUpDown _annSz=null!;
    private Button _annClr=null!;
    private ToolStripButton _bOpen=null!,_bSave=null!,_bSaveAs=null!,_bPrev=null!,_bNext=null!;
    private ToolStripButton _bZI=null!,_bZO=null!,_bZR=null!,_bCW=null!,_bCCW=null!;
    private ToolStripButton _bAddTxt=null!,_bFindTxt=null!,_bImgs=null!,_bMerge=null!,_bExtr=null!;

    public MainForm()
    {
        Text="PDF Editor — собственный рендер (без сторонних библиотек)";
        Size=new Size(1400,880); MinimumSize=new Size(1000,640);
        StartPosition=FormStartPosition.CenterScreen;
        BackColor=Color.FromArgb(28,28,34); Font=new Font("Segoe UI",9f);
        Build(); Wire(); Upd();
    }

    void Build()
    {
        // ── Toolbar ──────────────────────────────────────────────
        _tb=new ToolStrip{GripStyle=ToolStripGripStyle.Hidden,BackColor=Color.FromArgb(38,38,48),
            ForeColor=Color.White,RenderMode=ToolStripRenderMode.Professional,Padding=new Padding(6,2,6,2)};
        _bOpen  =TB("📂 Открыть",  "Открыть PDF");
        _bSave  =TB("💾 Сохранить","Сохранить");
        _bSaveAs=TB("💾 Как…",     "Сохранить как");
        _bPrev  =TB("◀","Пред."); _bNext=TB("▶","След.");
        _bZO    =TB("−","Уменьшить"); _bZR=TB("150%","150%"); _bZI=TB("+","Увеличить");
        _bCCW   =TB("↺","↺"); _bCW=TB("↻","↻");
        _bAddTxt=TB("✏ Текст",  "Добавить новый текст");
        _bFindTxt=TB("🔍 Найти/Заменить","Найти и заменить существующий текст");
        _bImgs  =TB("🖼 Картинки","Просмотр/замена изображений");
        _bMerge =TB("🔗 Слить","Объединить PDF");
        _bExtr  =TB("✂ Страница","Извлечь страницу");
        _tb.Items.AddRange(new ToolStripItem[]{_bOpen,_bSave,_bSaveAs,Sep(),
            _bPrev,_bNext,Sep(),_bZO,_bZR,_bZI,Sep(),_bCCW,_bCW,Sep(),
            _bAddTxt,_bFindTxt,_bImgs,Sep(),_bMerge,_bExtr});

        // ── Status ────────────────────────────────────────────────
        _st=new StatusStrip{BackColor=Color.FromArgb(18,18,24)};
        _lblSt=new ToolStripStatusLabel("Откройте PDF…"){ForeColor=Color.Silver,Spring=true,TextAlign=ContentAlignment.MiddleLeft};
        _lblPg=new ToolStripStatusLabel(""){ForeColor=Color.Silver};
        _lblZm=new ToolStripStatusLabel("100%"){ForeColor=Color.Silver};
        _st.Items.AddRange(new ToolStripItem[]{_lblSt,_lblPg,_lblZm});

        // ── Left: thumbnails ──────────────────────────────────────
        _left=new Panel{Width=155,Dock=DockStyle.Left,BackColor=Color.FromArgb(20,20,27),Padding=new Padding(4)};
        var lbl1=new Label{Text="Страницы",ForeColor=Color.DimGray,Dock=DockStyle.Top,Height=22,TextAlign=ContentAlignment.MiddleLeft,Font=new Font("Segoe UI",8f)};
        _thumbs=new ListBox{Dock=DockStyle.Fill,BackColor=Color.FromArgb(20,20,27),ForeColor=Color.LightGray,BorderStyle=BorderStyle.None,ItemHeight=24};
        _left.Controls.Add(_thumbs); _left.Controls.Add(lbl1);

        // ── Right: tabs ───────────────────────────────────────────
        _right=new Panel{Width=360,Dock=DockStyle.Right,BackColor=Color.FromArgb(22,22,30)};
        _tabs=new TabControl{Dock=DockStyle.Fill};

        // Text tab
        var tabTxt=new TabPage("Текст");
        _grid=new DataGridView{Dock=DockStyle.Fill,BackgroundColor=Color.FromArgb(25,25,33),ForeColor=Color.White,
            GridColor=Color.FromArgb(50,50,60),RowHeadersVisible=false,AllowUserToAddRows=false,
            AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode=DataGridViewSelectionMode.FullRowSelect,Font=new Font("Segoe UI",8.5f)};
        _grid.Columns.Add(new DataGridViewTextBoxColumn{Name="orig",HeaderText="Оригинал",ReadOnly=true});
        _grid.Columns.Add(new DataGridViewTextBoxColumn{Name="edit",HeaderText="Новый текст",ReadOnly=false});
        var btnApply=Btn("✔ Применить замены",Color.FromArgb(50,120,50));
        btnApply.Click+=ApplyTextEdits;
        tabTxt.Controls.Add(_grid); tabTxt.Controls.Add(btnApply);

        // Images tab
        var tabImg=new TabPage("Изображения");
        _iList=new ImageList{ImageSize=new Size(96,96),ColorDepth=ColorDepth.Depth32Bit};
        _lvImg=new ListView{Dock=DockStyle.Fill,LargeImageList=_iList,BackColor=Color.FromArgb(25,25,33),ForeColor=Color.White,BorderStyle=BorderStyle.None};
        var btnRepl=Btn("🖼 Заменить выбранное",Color.FromArgb(50,80,130));
        btnRepl.Click+=ReplaceImg;
        tabImg.Controls.Add(_lvImg); tabImg.Controls.Add(btnRepl);

        _tabs.TabPages.Add(tabTxt); _tabs.TabPages.Add(tabImg);
        _right.Controls.Add(_tabs);

        // ── Center: page view ─────────────────────────────────────
        _scHost=new Panel{Dock=DockStyle.Fill,BackColor=Color.FromArgb(42,42,52)};
        _scInner=new Panel{Dock=DockStyle.Fill,AutoScroll=true,BackColor=Color.FromArgb(42,42,52)};
        _pb=new PictureBox{SizeMode=PictureBoxSizeMode.AutoSize,BackColor=Color.FromArgb(42,42,52),Location=new Point(20,20)};
        _scInner.Controls.Add(_pb); _scHost.Controls.Add(_scInner);

        // ── Annotation panel ──────────────────────────────────────
        _annPanel=new Panel{Dock=DockStyle.Bottom,Height=50,BackColor=Color.FromArgb(30,30,40),Padding=new Padding(8,6,8,6),Visible=false};
        void AL(Control c){_annPanel.Controls.Add(c);}
        AL(new Label{Text="Текст:",ForeColor=Color.Silver,AutoSize=true,Top=15,Left=6});
        _annTxt=new TextBox{Left=60,Top=11,Width=270,BackColor=Color.FromArgb(50,50,60),ForeColor=Color.White,BorderStyle=BorderStyle.FixedSingle};AL(_annTxt);
        AL(new Label{Text="Размер:",ForeColor=Color.Silver,AutoSize=true,Top=15,Left=340});
        _annSz=new NumericUpDown{Left=400,Top=11,Width=56,Minimum=6,Maximum=72,Value=14,BackColor=Color.FromArgb(50,50,60),ForeColor=Color.White};AL(_annSz);
        _annClr=new Button{Text="🎨",Left=466,Top=9,Width=34,Height=28,BackColor=Color.FromArgb(50,50,60),ForeColor=Color.White,FlatStyle=FlatStyle.Flat};
        _annClr.FlatAppearance.BorderColor=Color.Gray; AL(_annClr);
        var bCanc=new Button{Text="✖",Left=508,Top=9,Width=34,Height=28,BackColor=Color.FromArgb(80,35,35),ForeColor=Color.White,FlatStyle=FlatStyle.Flat};
        bCanc.FlatAppearance.BorderColor=Color.FromArgb(80,35,35); bCanc.Click+=(_,_)=>EndTextMode(); AL(bCanc);
        AL(new Label{Text="← кликните по странице",ForeColor=Color.DimGray,AutoSize=true,Top=15,Left=550});

        Controls.Add(_scHost); Controls.Add(_left); Controls.Add(_right);
        Controls.Add(_annPanel); Controls.Add(_tb); Controls.Add(_st);
    }

    ToolStripButton TB(string t,string tip)=>new(t){ToolTipText=tip,ForeColor=Color.White,Padding=new Padding(8,1,8,1),DisplayStyle=ToolStripItemDisplayStyle.Text,AutoSize=true};
    ToolStripSeparator Sep()=>new();
    Button Btn(string t,Color c){var b=new Button{Text=t,Dock=DockStyle.Bottom,Height=32,BackColor=c,ForeColor=Color.White,FlatStyle=FlatStyle.Flat};b.FlatAppearance.BorderColor=c;return b;}

    void Wire()
    {
        _bOpen.Click   +=(_,_)=>OpenFile();
        _bSave.Click   +=(_,_)=>SaveFile(false);
        _bSaveAs.Click +=(_,_)=>SaveFile(true);
        _bPrev.Click   +=(_,_)=>GoTo(_currentPage-1);
        _bNext.Click   +=(_,_)=>GoTo(_currentPage+1);
        _bZI.Click     +=(_,_)=>Zoom(_zoom+.15f);
        _bZO.Click     +=(_,_)=>Zoom(_zoom-.15f);
        _bZR.Click     +=(_,_)=>Zoom(1.5f);
        _bCW.Click     +=(_,_)=>RotPg(90);
        _bCCW.Click    +=(_,_)=>RotPg(-90);
        _bAddTxt.Click +=(_,_)=>StartText();
        _bFindTxt.Click+=(_,_)=>LoadText();
        _bImgs.Click   +=(_,_)=>LoadImgs();
        _bMerge.Click  +=(_,_)=>Merge();
        _bExtr.Click   +=(_,_)=>Extract();
        _thumbs.SelectedIndexChanged+=(_,_)=>{if(_thumbs.SelectedIndex>=0)GoTo(_thumbs.SelectedIndex);};
        _pb.Click+=(s,e)=>{if(_addTextMode&&_pageBitmap!=null)PlaceText(((MouseEventArgs)e).Location);};
        _pb.MouseWheel+=(_,e)=>{if(ModifierKeys.HasFlag(Keys.Control))Zoom(_zoom+(e.Delta>0?.1f:-.1f));};
        _annClr.Click+=(_,_)=>{using var cd=new ColorDialog{Color=_annColor};if(cd.ShowDialog()==DialogResult.OK){_annColor=cd.Color;_annClr.BackColor=_annColor;}};
        KeyPreview=true;
        KeyDown+=(_,e)=>{
            if(e.KeyCode is Keys.Left or Keys.PageUp)GoTo(_currentPage-1);
            if(e.KeyCode is Keys.Right or Keys.PageDown)GoTo(_currentPage+1);
            if(e.KeyCode==Keys.Escape)EndTextMode();
        };
    }

    // ── File ─────────────────────────────────────────────────────
    void OpenFile(){using var d=new OpenFileDialog{Filter="PDF|*.pdf|Все|*.*"};if(d.ShowDialog()==DialogResult.OK)LoadFile(d.FileName);}
    void SaveFile(bool saveAs){
        if(_filePath==null)return;
        string p=_filePath;
        if(saveAs){using var d=new SaveFileDialog{Filter="PDF|*.pdf",FileName=Path.GetFileName(_filePath)};if(d.ShowDialog()!=DialogResult.OK)return;p=d.FileName;}
        try{File.WriteAllBytes(p,_fileBytes);_filePath=p;St($"Сохранён: {Path.GetFileName(p)}");}
        catch(Exception ex){MessageBox.Show(ex.Message);}
    }

    void LoadFile(string path){
        try{
            St("Загрузка…"); _filePath=path; _fileBytes=File.ReadAllBytes(path);
            Reload(); _pageCount=_parser!.PageCount; _currentPage=0; _zoom=1.5f;
            _thumbs.Items.Clear();
            for(int i=0;i<_pageCount;i++)_thumbs.Items.Add($"  Стр. {i+1}");
            Render(); Upd(); Text=$"PDF Editor — {Path.GetFileName(path)}  [{_pageCount} стр.]";
            St($"Открыт: {Path.GetFileName(path)}");
        }catch(Exception ex){MessageBox.Show($"Ошибка: {ex.Message}","Ошибка",MessageBoxButtons.OK,MessageBoxIcon.Error);}
    }

    void Reload(){_parser=new PdfParser(_fileBytes);_parser.Load();_renderer=new GdiRenderer(_parser);}

    // ── Navigation / render ───────────────────────────────────────
    void GoTo(int p){if(_parser==null||p<0||p>=_pageCount)return;_currentPage=p;_thumbs.SelectedIndexChanged-=ThCh;_thumbs.SelectedIndex=p;_thumbs.SelectedIndexChanged+=ThCh;Render();Upd();}
    void ThCh(object?s,EventArgs e){}

    void Render(){
        if(_renderer==null||_parser==null)return;
        try{
            St("Рендеринг…"); Cursor=Cursors.WaitCursor;
            _pageBitmap?.Dispose();
            _pageBitmap=_renderer.RenderPage(_currentPage,_zoom);
            _pb.Image=_pageBitmap; _pb.Size=_pageBitmap.Size;
            var pd=_parser.GetPageDict(_currentPage);
            var mb=pd.Get("MediaBox") as PdfArray;
            _pageW=mb!=null?mb.Items[2].AsDouble()-mb.Items[0].AsDouble():612;
            _pageH=mb!=null?mb.Items[3].AsDouble()-mb.Items[1].AsDouble():792;
            _lblPg.Text=$"  Стр.{_currentPage+1}/{_pageCount}  "; _lblZm.Text=$"Масштаб:{(int)(_zoom*100)}%";
            St("Готово.");
        }catch(Exception ex){St($"Ошибка рендера: {ex.Message}");}finally{Cursor=Cursors.Default;}
    }

    void Zoom(float z){_zoom=Math.Clamp(z,.15f,5f);if(_renderer!=null)Render();}

    void RotPg(int deg){
        if(_filePath==null)return;
        try{_fileBytes=PdfEditOperations.RotatePage(_fileBytes,_currentPage,deg);Reload();Render();St($"Повёрнута на {deg}°");}
        catch(Exception ex){MessageBox.Show(ex.Message);}
    }

    // ── Add text ─────────────────────────────────────────────────
    void StartText(){if(_parser==null)return;_addTextMode=true;_annPanel.Visible=true;_pb.Cursor=Cursors.Cross;St("Кликните по странице…");}
    void EndTextMode(){_addTextMode=false;_annPanel.Visible=false;_pb.Cursor=Cursors.Default;St("Готово.");}
    void PlaceText(Point click){
        if(_pageBitmap==null||string.IsNullOrWhiteSpace(_annTxt.Text))return;
        float sx=(float)(_pageW/_pageBitmap.Width),sy=(float)(_pageH/_pageBitmap.Height);
        float px=click.X*sx, py=(float)(_pageH-click.Y*sy)-(float)_annSz.Value;
        float r=_annColor.R/255f,g=_annColor.G/255f,b=_annColor.B/255f;
        try{_fileBytes=PdfEditOperations.AddText(_fileBytes,_currentPage,px,py,_annTxt.Text,(float)_annSz.Value,r,g,b);
            EndTextMode();Reload();Render();St($"Текст добавлен на стр.{_currentPage+1}");}
        catch(Exception ex){MessageBox.Show(ex.Message);EndTextMode();}
    }

    // ── Text search / replace ─────────────────────────────────────
    void LoadText(){
        if(_parser==null)return;
        try{
            var pg=_parser.GetPageDict(_currentPage);
            var cnt=_parser.GetContentBytes(pg);
            _textOccs=TextStreamEditor.FindText(cnt);
            _grid.Rows.Clear();
            foreach(var o in _textOccs){if(!string.IsNullOrWhiteSpace(o.Text))_grid.Rows.Add(o.Text,o.Text);}
            _tabs.SelectedIndex=0; St($"Найдено {_textOccs.Count} блоков на стр.{_currentPage+1}");
        }catch(Exception ex){MessageBox.Show(ex.Message);}
    }

    void ApplyTextEdits(object? s,EventArgs e){
        if(_parser==null)return;
        try{
            var pg=_parser.GetPageDict(_currentPage);
            var cnt=_parser.GetContentBytes(pg);
            bool ch=false;
            for(int i=0;i<_grid.Rows.Count;i++){
                string orig=(_grid.Rows[i].Cells["orig"].Value as string)??"";
                string neo =(_grid.Rows[i].Cells["edit"].Value as string)??"";
                if(orig==neo||string.IsNullOrEmpty(orig))continue;
                cnt=TextStreamEditor.ReplaceAll(cnt,orig,neo);ch=true;
            }
            if(!ch){St("Нет изменений.");return;}
            _fileBytes=PdfEditOperations.ModifyPageContentStream(_fileBytes,_currentPage,cnt);
            Reload();Render();LoadText();St("Текст обновлён.");
        }catch(Exception ex){MessageBox.Show($"Ошибка замены: {ex.Message}");}
    }

    // ── Image editor ─────────────────────────────────────────────
    void LoadImgs(){
        if(_parser==null)return;
        try{
            _pageImgs=ImageEditor.GetPageImages(_parser,_currentPage);
            _iList.Images.Clear(); _lvImg.Items.Clear();
            foreach(var img in _pageImgs){
                var th=new Bitmap(img.Bitmap,new Size(96,96));
                _iList.Images.Add(img.XObjectName,th);
                _lvImg.Items.Add(new ListViewItem($"{img.XObjectName}\n{img.Width}×{img.Height} {img.Filter}",img.XObjectName));
            }
            _tabs.SelectedIndex=1; St($"Найдено {_pageImgs.Count} изображений на стр.{_currentPage+1}");
        }catch(Exception ex){MessageBox.Show(ex.Message);}
    }

    void ReplaceImg(object? s,EventArgs e){
        if(_lvImg.SelectedItems.Count==0){MessageBox.Show("Выберите изображение.");return;}
        int idx=_lvImg.SelectedItems[0].Index;
        if(idx<0||idx>=_pageImgs.Count)return;
        var img=_pageImgs[idx];
        using var dlg=new OpenFileDialog{Filter="Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Все|*.*"};
        if(dlg.ShowDialog()!=DialogResult.OK)return;
        try{
            using var bmp=new Bitmap(dlg.FileName);
            using var res=new Bitmap(bmp,new Size(img.Width,img.Height));
            if(img.ObjNum<0){MessageBox.Show("Встроенное изображение — замена не поддерживается.");return;}
            _fileBytes=ImageEditor.ReplaceImage(_fileBytes,img.ObjNum,res);
            Reload();Render();LoadImgs();St($"{img.XObjectName} заменено.");
        }catch(Exception ex){MessageBox.Show(ex.Message);}
    }

    // ── Merge / extract ───────────────────────────────────────────
    void Merge(){
        using var dlg=new OpenFileDialog{Filter="PDF|*.pdf",Multiselect=true};
        if(dlg.ShowDialog()!=DialogResult.OK||dlg.FileNames.Length<2)return;
        using var sdlg=new SaveFileDialog{Filter="PDF|*.pdf",FileName="merged.pdf"};
        if(sdlg.ShowDialog()!=DialogResult.OK)return;
        try{var m=new PdfMerger();foreach(var f in dlg.FileNames)m.AddPages(File.ReadAllBytes(f));
            File.WriteAllBytes(sdlg.FileName,m.Build());St($"Слито → {Path.GetFileName(sdlg.FileName)}");
            if(MessageBox.Show("Открыть?","Готово",MessageBoxButtons.YesNo)==DialogResult.Yes)LoadFile(sdlg.FileName);}
        catch(Exception ex){MessageBox.Show(ex.Message);}
    }

    void Extract(){
        if(_filePath==null)return;
        using var dlg=new SaveFileDialog{Filter="PDF|*.pdf",FileName=$"page_{_currentPage+1}.pdf"};
        if(dlg.ShowDialog()!=DialogResult.OK)return;
        try{var m=new PdfMerger();m.AddPages(_fileBytes,new[]{_currentPage});File.WriteAllBytes(dlg.FileName,m.Build());St($"Стр.{_currentPage+1} → {Path.GetFileName(dlg.FileName)}");}
        catch(Exception ex){MessageBox.Show(ex.Message);}
    }

    void Upd(){
        bool h=_parser!=null;
        foreach(var b in new[]{_bSave,_bSaveAs,_bZI,_bZO,_bZR,_bCW,_bCCW,_bAddTxt,_bFindTxt,_bImgs,_bExtr})b.Enabled=h;
        _bPrev.Enabled=h&&_currentPage>0; _bNext.Enabled=h&&_currentPage<_pageCount-1;
    }

    void St(string m)=>_lblSt.Text=m;
    protected override void OnFormClosing(FormClosingEventArgs e){_pageBitmap?.Dispose();base.OnFormClosing(e);}
}
