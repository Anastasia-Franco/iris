using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WindowsFormsApp
{
    /// <summary>
    /// Memory governance admin panel — view, filter, promote, archive, and delete
    /// memory notes across all projects and lifecycle tiers.
    /// Also provides document ingestion, research review, and context inspection.
    /// </summary>
    public partial class MemoryAdminForm : Form
    {
        private static readonly HttpClient _http = new()
        {
            BaseAddress = new Uri("http://10.10.10.2:8000/"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly string _callerSessionId;

        /// <summary>Fired after any project create / edit / archive operation.</summary>
        public event EventHandler? ProjectsChanged;

        // ── Controls ──────────────────────────────────────────────────────
        private TabControl tabMain;

        // Memory tab
        private TabPage tabMemory;
        private ListBox lstProjects;
        private ComboBox cmbScopeFilter;
        private ComboBox cmbStateFilter;
        private Button btnRefreshMemory;
        private DataGridView gridNotes;
        private TextBox txtNoteContent;
        private Button btnArchiveNote;
        private Button btnDeleteNote;
        private Button btnSaveNote;
        private Button btnNewNote;
        private ComboBox cmbNoteScope;
        private ComboBox cmbNoteState;
        private Label lblNoteScope;
        private Label lblNoteState;
        private Button btnInspectContext;
        private Button btnTokenBudget;
        private Label lblScopeFilter;
        private Label lblStateFilter;
        private Label lblProjects;

        // Documents tab
        private TabPage tabDocs;
        private ListBox lstDocProjects;
        private DataGridView gridDocs;
        private Button btnIngestFile;
        private Button btnDeleteDoc;
        private Button btnRefreshDocs;
        private Button btnEditDocMeta;
        private Button btnMoveDoc;
        private ComboBox cmbDocAuthority;
        private ComboBox cmbDocType;
        private ComboBox cmbDocFinality;

        // Projects tab
        private TabPage tabProjects;
        private DataGridView gridProjects;
        private TextBox txtProjName;
        private TextBox txtProjDesc;
        private ComboBox cmbProjStatus;
        private Button btnNewProject;
        private Button btnSaveProject;
        private Button btnArchiveProject;
        private string _selectedProjectId = "";

        // Research tab
        private TabPage tabResearch;
        private DataGridView gridResearch;
        private TextBox txtResearchRaw;
        private CheckedListBox lstCandidates;
        private Button btnPromoteResearch;
        private Button btnDiscardResearch;
        private Button btnRefreshResearch;

        // Research Trace tab
        private TabPage tabTrace;
        private DataGridView gridTrace;
        private TextBox txtTraceDetail;
        private Button btnRefreshTrace;
        private string _selectedTraceId = "";

        private string _selectedNoteId = "";
        private string _selectedDocId  = "";
        private string _selectedResearchId = "";
        private List<Dictionary<string, object>> _candidateNotes = new();

        public MemoryAdminForm(string callerSessionId)
        {
            _callerSessionId = callerSessionId;
            InitializeMemoryAdminForm();
            _ = RefreshProjectsAsync();
        }

        // ── UI Construction ───────────────────────────────────────────────

        private void InitializeMemoryAdminForm()
        {
            Text = "IRIS Memory Administration";
            Size = new Size(1100, 700);
            MinimumSize = new Size(900, 600);
            BackColor = Color.FromArgb(30, 30, 30);
            ForeColor = Color.White;

            // Tab control
            tabMain = new TabControl { Dock = DockStyle.Fill };
            tabMain.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabMain.DrawItem += TabMain_DrawItem;
            Controls.Add(tabMain);

            BuildMemoryTab();
            BuildDocumentsTab();
            BuildResearchTab();
            BuildTraceTab();
            BuildProjectsTab();

            tabMain.TabPages.AddRange(new[] { tabMemory, tabDocs, tabResearch, tabTrace, tabProjects });
            tabMain.SelectedIndexChanged += (_, _) => OnTabChanged();
        }

        private void BuildMemoryTab()
        {
            tabMemory = new TabPage("Memory Notes") { BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };

            // ── Left panel: project list + filters ──
            var leftPanel = new Panel { Width = 200, Dock = DockStyle.Left, Padding = new Padding(6) };

            lblProjects = new Label { Text = "Project", AutoSize = true, ForeColor = Color.Silver, Location = new Point(6, 6) };
            lstProjects = new ListBox
            {
                Location = new Point(6, 24), Size = new Size(188, 180),
                BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White
            };
            lstProjects.SelectedIndexChanged += (_, _) => _ = RefreshNotesAsync();

            lblScopeFilter = new Label { Text = "Scope", AutoSize = true, ForeColor = Color.Silver, Location = new Point(6, 212) };
            cmbScopeFilter = new ComboBox
            {
                Location = new Point(6, 229), Size = new Size(188, 23),
                BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White, DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbScopeFilter.Items.AddRange(new object[] { "(all scopes)", "operator", "project", "global", "research", "session" });
            cmbScopeFilter.SelectedIndex = 0;
            cmbScopeFilter.SelectedIndexChanged += (_, _) => _ = RefreshNotesAsync();

            lblStateFilter = new Label { Text = "State", AutoSize = true, ForeColor = Color.Silver, Location = new Point(6, 260) };
            cmbStateFilter = new ComboBox
            {
                Location = new Point(6, 277), Size = new Size(188, 23),
                BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White, DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbStateFilter.Items.AddRange(new object[] { "(all states)", "ephemeral", "session", "durable", "pinned", "archived", "deleted" });
            cmbStateFilter.SelectedIndex = 0;
            cmbStateFilter.SelectedIndexChanged += (_, _) => _ = RefreshNotesAsync();

            btnRefreshMemory = MakeButton("Refresh", new Point(6, 310), new Size(188, 28));
            btnRefreshMemory.Click += (_, _) => _ = RefreshProjectsAsync();

            btnInspectContext = MakeButton("Inspect Last Context", new Point(6, 348), new Size(188, 28));
            btnInspectContext.Click += (_, _) => _ = InspectContextAsync();

            btnTokenBudget = MakeButton("Token Budget", new Point(6, 384), new Size(188, 28));
            btnTokenBudget.Click += (_, _) => _ = ShowTokenBudgetAsync();

            leftPanel.Controls.AddRange(new Control[]
            {
                lblProjects, lstProjects, lblScopeFilter, cmbScopeFilter,
                lblStateFilter, cmbStateFilter, btnRefreshMemory,
                btnInspectContext, btnTokenBudget
            });

            // ── Right panel: note grid + editor ──
            var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };

            gridNotes = new DataGridView
            {
                Location = new Point(6, 6), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Height = 280, Width = 870,
                BackgroundColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White, ColumnHeadersDefaultCellStyle = { BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White },
                DefaultCellStyle = { BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, SelectionBackColor = Color.SteelBlue },
                ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            gridNotes.Columns.Add(new DataGridViewTextBoxColumn { Name = "colId",      HeaderText = "ID",         Width = 80,  FillWeight = 8  });
            gridNotes.Columns.Add(new DataGridViewTextBoxColumn { Name = "colState",    HeaderText = "State",      Width = 75,  FillWeight = 8  });
            gridNotes.Columns.Add(new DataGridViewTextBoxColumn { Name = "colScope",    HeaderText = "Scope",      Width = 75,  FillWeight = 8  });
            gridNotes.Columns.Add(new DataGridViewTextBoxColumn { Name = "colContent",  HeaderText = "Content",    FillWeight = 48 });
            gridNotes.Columns.Add(new DataGridViewTextBoxColumn { Name = "colConf",     HeaderText = "Conf",       Width = 45,  FillWeight = 5  });
            gridNotes.Columns.Add(new DataGridViewTextBoxColumn { Name = "colSource",   HeaderText = "Source",     Width = 80,  FillWeight = 8  });
            gridNotes.Columns.Add(new DataGridViewTextBoxColumn { Name = "colUsage",    HeaderText = "Used",       Width = 45,  FillWeight = 5  });
            gridNotes.Columns.Add(new DataGridViewTextBoxColumn { Name = "colCreated",  HeaderText = "Created",    Width = 100, FillWeight = 10 });
            gridNotes.SelectionChanged += GridNotes_SelectionChanged;

            int editorTop = 294;

            lblNoteScope = new Label { Text = "Scope", AutoSize = true, ForeColor = Color.Silver, Location = new Point(6, editorTop) };
            cmbNoteScope = new ComboBox
            {
                Location = new Point(6, editorTop + 17), Size = new Size(120, 23),
                BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White, DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbNoteScope.Items.AddRange(new object[] { "global", "operator", "project", "research", "session" });
            cmbNoteScope.SelectedIndex = 0;

            lblNoteState = new Label { Text = "State", AutoSize = true, ForeColor = Color.Silver, Location = new Point(134, editorTop) };
            cmbNoteState = new ComboBox
            {
                Location = new Point(134, editorTop + 17), Size = new Size(120, 23),
                BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White, DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbNoteState.Items.AddRange(new object[] { "durable", "pinned", "ephemeral", "session", "archived", "deleted" });
            cmbNoteState.SelectedIndex = 0;

            txtNoteContent = new TextBox
            {
                Location = new Point(6, editorTop + 47), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                Height = 130, Width = 870,
                Multiline = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, Font = new Font("Consolas", 10f)
            };

            int btnTop = editorTop + 185;
            btnSaveNote    = MakeButton("Save Changes",  new Point(6,   btnTop), new Size(120, 26)); btnSaveNote.Click    += (_, _) => _ = SaveNoteChangesAsync();
            btnNewNote     = MakeButton("New Note",      new Point(134, btnTop), new Size(100, 26)); btnNewNote.Click     += (_, _) => _ = CreateNewNoteAsync();
            btnArchiveNote = MakeButton("Archive",       new Point(242, btnTop), new Size(90,  26)); btnArchiveNote.BackColor = Color.DarkGoldenrod; btnArchiveNote.Click += (_, _) => _ = ArchiveNoteAsync();
            btnDeleteNote  = MakeButton("Delete",        new Point(340, btnTop), new Size(90,  26)); btnDeleteNote.BackColor  = Color.DarkRed;       btnDeleteNote.Click  += (_, _) => _ = DeleteNoteAsync();

            rightPanel.Controls.AddRange(new Control[]
            {
                gridNotes, lblNoteScope, cmbNoteScope, lblNoteState, cmbNoteState,
                txtNoteContent, btnSaveNote, btnNewNote, btnArchiveNote, btnDeleteNote
            });

            tabMemory.Controls.Add(rightPanel);
            tabMemory.Controls.Add(leftPanel);
        }

        private void BuildDocumentsTab()
        {
            tabDocs = new TabPage("Documents") { BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };

            var leftPanel = new Panel { Width = 200, Dock = DockStyle.Left, Padding = new Padding(6) };

            var lblDocProj = new Label { Text = "Project", AutoSize = true, ForeColor = Color.Silver, Location = new Point(6, 6) };
            lstDocProjects = new ListBox
            {
                Location = new Point(6, 24), Size = new Size(188, 300),
                BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White
            };
            lstDocProjects.SelectedIndexChanged += (_, _) => _ = RefreshDocsAsync();

            btnRefreshDocs = MakeButton("Refresh", new Point(6, 334), new Size(188, 28));
            btnRefreshDocs.Click += (_, _) => _ = RefreshDocsAsync();

            btnIngestFile = MakeButton("Ingest File…", new Point(6, 370), new Size(188, 28));
            btnIngestFile.BackColor = Color.FromArgb(0, 100, 150);
            btnIngestFile.Click += BtnIngestFile_Click;

            btnDeleteDoc = MakeButton("Delete Document", new Point(6, 406), new Size(188, 28));
            btnDeleteDoc.BackColor = Color.DarkRed;
            btnDeleteDoc.Click += (_, _) => _ = DeleteDocAsync();

            // Authority metadata controls
            var lblAuth = new Label { Text = "Authority Level", AutoSize = true, ForeColor = Color.Silver, Location = new Point(6, 442) };
            cmbDocAuthority = new ComboBox
            {
                Location = new Point(6, 458), Size = new Size(188, 24),
                BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White,
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat
            };
            cmbDocAuthority.Items.AddRange(new object[] { "Definitive", "Authoritative", "Informational", "Contextual", "Anecdotal" });
            cmbDocAuthority.SelectedIndex = 2; // default: Informational

            var lblDocTypeLabel = new Label { Text = "Document Type", AutoSize = true, ForeColor = Color.Silver, Location = new Point(6, 488) };
            cmbDocType = new ComboBox
            {
                Location = new Point(6, 504), Size = new Size(188, 24),
                BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White,
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat
            };
            cmbDocType.Items.AddRange(new object[] { "published_framework", "operational_guide", "strategic_draft", "meeting_notes", "planning_discussion", "other" });
            cmbDocType.SelectedIndex = 5; // default: other

            var lblFinality = new Label { Text = "Finality", AutoSize = true, ForeColor = Color.Silver, Location = new Point(6, 534) };
            cmbDocFinality = new ComboBox
            {
                Location = new Point(6, 550), Size = new Size(188, 24),
                BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White,
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat
            };
            cmbDocFinality.Items.AddRange(new object[] { "final", "draft", "provisional" });
            cmbDocFinality.SelectedIndex = 0; // default: final

            btnEditDocMeta = MakeButton("Edit Metadata…", new Point(6, 582), new Size(188, 28));
            btnEditDocMeta.BackColor = Color.FromArgb(60, 80, 120);
            btnEditDocMeta.Click += BtnEditDocMeta_Click;

            btnMoveDoc = MakeButton("Move to Project…", new Point(6, 618), new Size(188, 28));
            btnMoveDoc.BackColor = Color.FromArgb(50, 100, 80);
            btnMoveDoc.Click += BtnMoveDoc_Click;

            leftPanel.Controls.AddRange(new Control[] {
                lblDocProj, lstDocProjects, btnRefreshDocs, btnIngestFile, btnDeleteDoc,
                lblAuth, cmbDocAuthority, lblDocTypeLabel, cmbDocType, lblFinality, cmbDocFinality,
                btnEditDocMeta, btnMoveDoc
            });

            gridDocs = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                ColumnHeadersDefaultCellStyle = { BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White },
                DefaultCellStyle = { BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, SelectionBackColor = Color.SteelBlue },
                ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            gridDocs.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDocId",        HeaderText = "ID",        FillWeight = 8  });
            gridDocs.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDocFile",       HeaderText = "Filename",  FillWeight = 30 });
            gridDocs.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDocType",       HeaderText = "Doc Type",  FillWeight = 8  });
            gridDocs.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDocAuthority",  HeaderText = "Authority", FillWeight = 12 });
            gridDocs.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDocDocType",    HeaderText = "Purpose",   FillWeight = 14 });
            gridDocs.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDocChunks",     HeaderText = "Chunks",    FillWeight = 7  });
            gridDocs.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDocStatus",     HeaderText = "Status",    FillWeight = 11 });
            gridDocs.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDocIngested",   HeaderText = "Ingested",  FillWeight = 10 });
            gridDocs.SelectionChanged += (_, _) =>
            {
                if (gridDocs.SelectedRows.Count > 0)
                    _selectedDocId = gridDocs.SelectedRows[0].Cells["colDocId"].Value?.ToString() ?? "";
            };

            tabDocs.Controls.Add(gridDocs);
            tabDocs.Controls.Add(leftPanel);
        }

        private void BuildResearchTab()
        {
            tabResearch = new TabPage("Research Review") { BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };

            // ── Button strip (Dock=Bottom, always visible) ─────────────────
            var btnPanel = new Panel
            {
                Dock = DockStyle.Bottom, Height = 36,
                BackColor = Color.FromArgb(30, 30, 30), Padding = new Padding(6, 5, 6, 5)
            };
            btnRefreshResearch = MakeButton("Refresh",          new Point(0, 4),   new Size(110, 26));
            btnRefreshResearch.Click += (_, _) => _ = RefreshResearchAsync();
            btnPromoteResearch = MakeButton("Promote Selected", new Point(118, 4), new Size(140, 26));
            btnPromoteResearch.BackColor = Color.FromArgb(0, 120, 60);
            btnPromoteResearch.Click += (_, _) => _ = PromoteResearchAsync();
            btnDiscardResearch = MakeButton("Discard",          new Point(266, 4), new Size(100, 26));
            btnDiscardResearch.BackColor = Color.DarkRed;
            btnDiscardResearch.Click += (_, _) => _ = DiscardResearchAsync();
            btnPanel.Controls.AddRange(new Control[] { btnRefreshResearch, btnPromoteResearch, btnDiscardResearch });

            // ── Candidate checklist (Dock=Bottom, grows upward from button strip) ──
            lstCandidates = new CheckedListBox
            {
                Dock = DockStyle.Bottom, Height = 180,
                BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, Font = new Font("Consolas", 9f)
            };
            var lblCands = new Label
            {
                Text = "Candidate Notes — check to promote:", ForeColor = Color.Silver,
                Dock = DockStyle.Bottom, Height = 18, TextAlign = ContentAlignment.BottomLeft,
                Padding = new Padding(6, 0, 0, 0)
            };

            // ── Raw result box (Dock=Bottom, above candidates) ─────────────
            txtResearchRaw = new TextBox
            {
                Dock = DockStyle.Bottom, Height = 100,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(40, 40, 40), ForeColor = Color.LightGray, Font = new Font("Consolas", 9f)
            };
            var lblRaw = new Label
            {
                Text = "Raw Result:", ForeColor = Color.Silver,
                Dock = DockStyle.Bottom, Height = 18, TextAlign = ContentAlignment.BottomLeft,
                Padding = new Padding(6, 0, 0, 0)
            };

            // ── Research grid (Dock=Fill — takes all remaining top space) ──
            gridResearch = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(40, 40, 40), ForeColor = Color.White,
                ColumnHeadersDefaultCellStyle = { BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White },
                DefaultCellStyle = { BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, SelectionBackColor = Color.SteelBlue },
                ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            gridResearch.Columns.Add(new DataGridViewTextBoxColumn { Name = "colResId",   HeaderText = "ID",         FillWeight = 10 });
            gridResearch.Columns.Add(new DataGridViewTextBoxColumn { Name = "colResQuery", HeaderText = "Query",      FillWeight = 55 });
            gridResearch.Columns.Add(new DataGridViewTextBoxColumn { Name = "colResDate",  HeaderText = "Date",       FillWeight = 20 });
            gridResearch.Columns.Add(new DataGridViewTextBoxColumn { Name = "colResCands", HeaderText = "Candidates", FillWeight = 15 });
            // CellClick fires on every click regardless of whether selection changed —
            // this is the reliable fallback when SelectionChanged doesn't fire.
            gridResearch.CellClick += GridResearch_CellClick;
            // NOTE: SelectionChanged is NOT registered here — it is registered
            // inside RefreshResearchAsync after all Tags are set, to avoid the
            // race where Rows.Add() fires the handler before Tag is populated.

            // Add bottom-docked controls first (stack upward), then Fill grid last
            tabResearch.Controls.Add(btnPanel);
            tabResearch.Controls.Add(lstCandidates);
            tabResearch.Controls.Add(lblCands);
            tabResearch.Controls.Add(txtResearchRaw);
            tabResearch.Controls.Add(lblRaw);
            tabResearch.Controls.Add(gridResearch);
        }

        // ── Tab switching ─────────────────────────────────────────────────

        private void OnTabChanged()
        {
            if (tabMain.SelectedTab == tabDocs)
                _ = RefreshDocProjectsAsync();
            else if (tabMain.SelectedTab == tabResearch)
                _ = RefreshResearchAsync();
            else if (tabMain.SelectedTab == tabTrace)
                _ = RefreshTraceAsync();
            else if (tabMain.SelectedTab == tabProjects)
                _ = RefreshProjectGridAsync();
        }

        // ── Projects tab ──────────────────────────────────────────────────

        private void BuildProjectsTab()
        {
            tabProjects = new TabPage("Projects") { BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };

            // ── Bottom editor panel (always visible, docked Bottom) ──────────
            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 112,
                BackColor = Color.FromArgb(30, 30, 30),
                Padding = new Padding(6, 4, 6, 4)
            };

            var lblName = new Label { Text = "Name",        ForeColor = Color.Silver, AutoSize = true, Location = new Point(6,   6) };
            var lblDesc = new Label { Text = "Description", ForeColor = Color.Silver, AutoSize = true, Location = new Point(260, 6) };
            var lblStat = new Label { Text = "Status",      ForeColor = Color.Silver, AutoSize = true, Location = new Point(740, 6) };

            txtProjName = new TextBox
            {
                Location = new Point(6, 23), Size = new Size(246, 23),
                BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White
            };
            txtProjDesc = new TextBox
            {
                Location = new Point(260, 23), Size = new Size(472, 23),
                BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White
            };
            cmbProjStatus = new ComboBox
            {
                Location = new Point(740, 23), Size = new Size(120, 23),
                BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbProjStatus.Items.AddRange(new object[] { "active", "archived" });
            cmbProjStatus.SelectedIndex = 0;

            var btnRefreshProj = MakeButton("Refresh",      new Point(6,   58), new Size(110, 26));
            btnRefreshProj.Click += (_, _) => _ = RefreshProjectGridAsync();

            btnNewProject = MakeButton("New Project",    new Point(124, 58), new Size(120, 26));
            btnNewProject.BackColor = Color.FromArgb(0, 100, 60);
            btnNewProject.Click += (_, _) => _ = CreateProjectAsync();

            btnSaveProject = MakeButton("Save Changes",  new Point(252, 58), new Size(120, 26));
            btnSaveProject.Click += (_, _) => _ = SaveProjectChangesAsync();

            btnArchiveProject = MakeButton("Archive",    new Point(380, 58), new Size(100, 26));
            btnArchiveProject.BackColor = Color.DarkGoldenrod;
            btnArchiveProject.Click += (_, _) => _ = ArchiveProjectAsync();

            bottomPanel.Controls.AddRange(new Control[]
            {
                lblName, txtProjName, lblDesc, txtProjDesc, lblStat, cmbProjStatus,
                btnRefreshProj, btnNewProject, btnSaveProject, btnArchiveProject
            });

            // ── Grid (docked Fill — takes all remaining space above the panel) ──
            gridProjects = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                ColumnHeadersDefaultCellStyle = { BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White },
                DefaultCellStyle = { BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, SelectionBackColor = Color.SteelBlue },
                ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            gridProjects.Columns.Add(new DataGridViewTextBoxColumn { Name = "colProjId",      HeaderText = "ID",          FillWeight = 10 });
            gridProjects.Columns.Add(new DataGridViewTextBoxColumn { Name = "colProjName",    HeaderText = "Name",        FillWeight = 25 });
            gridProjects.Columns.Add(new DataGridViewTextBoxColumn { Name = "colProjDesc",    HeaderText = "Description", FillWeight = 45 });
            gridProjects.Columns.Add(new DataGridViewTextBoxColumn { Name = "colProjStatus",  HeaderText = "Status",      FillWeight = 10 });
            gridProjects.Columns.Add(new DataGridViewTextBoxColumn { Name = "colProjCreated", HeaderText = "Created",     FillWeight = 10 });
            gridProjects.Columns.Add(new DataGridViewTextBoxColumn { Name = "colProjUsed",    HeaderText = "Last Used",   FillWeight = 10 });
            gridProjects.SelectionChanged += GridProjects_SelectionChanged;

            // Add bottomPanel before gridProjects so docking resolves correctly
            tabProjects.Controls.Add(bottomPanel);
            tabProjects.Controls.Add(gridProjects);
        }

        private void TabMain_DrawItem(object? sender, DrawItemEventArgs e)
        {
            var tab = tabMain.TabPages[e.Index];
            bool selected = e.Index == tabMain.SelectedIndex;
            var bgColor = selected ? Color.FromArgb(55, 55, 55) : Color.FromArgb(35, 35, 35);
            using var bg = new SolidBrush(bgColor);
            e.Graphics.FillRectangle(bg, e.Bounds);
            // Draw a subtle top accent line on the selected tab
            if (selected)
            {
                using var accent = new Pen(Color.SteelBlue, 2);
                e.Graphics.DrawLine(accent, e.Bounds.Left, e.Bounds.Top, e.Bounds.Right, e.Bounds.Top);
            }
            using var fg = new SolidBrush(selected ? Color.White : Color.Silver);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            e.Graphics.DrawString(tab.Text, e.Font ?? Font, fg,
                RectangleF.FromLTRB(e.Bounds.Left, e.Bounds.Top, e.Bounds.Right, e.Bounds.Bottom), sf);
        }

        private void GridProjects_SelectionChanged(object? sender, EventArgs e)
        {
            if (gridProjects.SelectedRows.Count == 0) return;
            var row = gridProjects.SelectedRows[0];
            _selectedProjectId     = row.Cells["colProjId"].Value?.ToString()    ?? "";
            txtProjName.Text       = row.Cells["colProjName"].Value?.ToString()  ?? "";
            txtProjDesc.Text       = row.Cells["colProjDesc"].Value?.ToString()  ?? "";
            cmbProjStatus.SelectedItem = row.Cells["colProjStatus"].Value?.ToString() ?? "active";
        }

        private async Task RefreshProjectGridAsync()
        {
            try
            {
                var json = await _http.GetStringAsync("projects");
                using var doc = JsonDocument.Parse(json);
                gridProjects.Rows.Clear();
                foreach (var p in doc.RootElement.EnumerateArray())
                {
                    string id     = p.GetProperty("id").GetString()         ?? "";
                    string name   = p.GetProperty("name").GetString()       ?? "";
                    string desc   = p.TryGetProperty("description", out var dv) ? dv.GetString() ?? "" : "";
                    string status = p.GetProperty("status").GetString()     ?? "";
                    string ca     = p.GetProperty("created_at").GetString() ?? "";
                    string lu     = p.TryGetProperty("last_used_at", out var luv) && luv.ValueKind != JsonValueKind.Null
                                    ? luv.GetString() ?? "" : "";
                    gridProjects.Rows.Add(id, name, desc, status,
                        ca.Length >= 10 ? ca[..10] : ca,
                        lu.Length >= 10 ? lu[..10] : lu);
                }
                await RefreshProjectsAsync(); // keep Memory + Documents sidebars in sync
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private async Task CreateProjectAsync()
        {
            string name = txtProjName.Text.Trim();
            if (string.IsNullOrEmpty(name)) { ShowError("Enter a project name."); return; }
            try
            {
                var body = JsonSerializer.Serialize(new { name, description = txtProjDesc.Text.Trim() });
                var resp = await _http.PostAsync("projects",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                resp.EnsureSuccessStatusCode();
                txtProjName.Clear();
                txtProjDesc.Clear();
                await RefreshProjectGridAsync();
                ProjectsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private async Task SaveProjectChangesAsync()
        {
            if (string.IsNullOrEmpty(_selectedProjectId)) return;
            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    name        = txtProjName.Text.Trim(),
                    description = txtProjDesc.Text.Trim(),
                    status      = cmbProjStatus.SelectedItem?.ToString() ?? "active",
                });
                var resp = await _http.PatchAsync($"projects/{_selectedProjectId}",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                resp.EnsureSuccessStatusCode();
                await RefreshProjectGridAsync();
                ProjectsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private async Task ArchiveProjectAsync()
        {
            if (string.IsNullOrEmpty(_selectedProjectId)) return;
            string name = gridProjects.SelectedRows.Count > 0
                ? gridProjects.SelectedRows[0].Cells["colProjName"].Value?.ToString() ?? _selectedProjectId
                : _selectedProjectId;
            if (MessageBox.Show($"Archive project '{name}'?\n\nData is preserved; it will no longer appear in client dropdowns.",
                    "Confirm Archive", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                var body = JsonSerializer.Serialize(new { status = "archived" });
                var resp = await _http.PatchAsync($"projects/{_selectedProjectId}",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                resp.EnsureSuccessStatusCode();
                await RefreshProjectGridAsync();
                ProjectsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        // ── Sidebar project lists (Memory + Documents tabs) ────────────────

        private async Task RefreshProjectsAsync()
        {
            try
            {
                var json = await _http.GetStringAsync("projects");
                using var doc = JsonDocument.Parse(json);
                lstProjects.Items.Clear();
                lstProjects.Items.Add(new ProjectFilterItem("", "(all projects)"));
                foreach (var p in doc.RootElement.EnumerateArray())
                {
                    string id   = p.GetProperty("id").GetString() ?? "";
                    string name = p.GetProperty("name").GetString() ?? "";
                    lstProjects.Items.Add(new ProjectFilterItem(id, name));
                }
                if (lstProjects.Items.Count > 0) lstProjects.SelectedIndex = 0;
                await RefreshNotesAsync();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private async Task RefreshDocProjectsAsync()
        {
            try
            {
                var json = await _http.GetStringAsync("projects");
                using var doc = JsonDocument.Parse(json);
                lstDocProjects.Items.Clear();
                foreach (var p in doc.RootElement.EnumerateArray())
                {
                    string id   = p.GetProperty("id").GetString() ?? "";
                    string name = p.GetProperty("name").GetString() ?? "";
                    lstDocProjects.Items.Add(new ProjectFilterItem(id, name));
                }
                if (lstDocProjects.Items.Count > 0) lstDocProjects.SelectedIndex = 0;
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        // ── Memory Notes ──────────────────────────────────────────────────

        private async Task RefreshNotesAsync()
        {
            try
            {
                var qs = new StringBuilder("memory?");
                if (cmbScopeFilter.SelectedIndex > 0)
                    qs.Append($"scope={Uri.EscapeDataString(cmbScopeFilter.SelectedItem!.ToString()!)}&");
                if (cmbStateFilter.SelectedIndex > 0)
                    qs.Append($"state={Uri.EscapeDataString(cmbStateFilter.SelectedItem!.ToString()!)}&");
                if (lstProjects.SelectedItem is ProjectFilterItem { Id: var projId } && projId != "")
                    qs.Append($"project_id={Uri.EscapeDataString(projId)}&");

                var json = await _http.GetStringAsync(qs.ToString().TrimEnd('&', '?'));
                using var doc = JsonDocument.Parse(json);

                gridNotes.Rows.Clear();
                foreach (var n in doc.RootElement.EnumerateArray())
                {
                    string id      = n.GetProperty("id").GetString() ?? "";
                    string state   = n.GetProperty("state").GetString() ?? "";
                    string scope   = n.GetProperty("scope").GetString() ?? "";
                    string content = n.GetProperty("content").GetString() ?? "";
                    double conf    = n.TryGetProperty("confidence",  out var cv)  ? cv.GetDouble() : 1.0;
                    string source  = n.TryGetProperty("source",      out var sv)  ? sv.GetString() ?? "" : "";
                    int    usage   = n.TryGetProperty("usage_count", out var uv)  ? uv.GetInt32()  : 0;
                    string created = n.GetProperty("created_at").GetString() ?? "";
                    string preview = content.Length > 80 ? content[..80] + "…" : content;
                    int row = gridNotes.Rows.Add(id, state, scope, preview, $"{conf:F2}", source, usage, created[..10]);
                    gridNotes.Rows[row].Tag = new NoteRecord(id, content, scope, state);
                }
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void GridNotes_SelectionChanged(object? sender, EventArgs e)
        {
            if (gridNotes.SelectedRows.Count == 0) return;
            if (gridNotes.SelectedRows[0].Tag is NoteRecord rec)
            {
                _selectedNoteId = rec.Id;
                txtNoteContent.Text = rec.Content;
                cmbNoteScope.SelectedItem = rec.Scope;
                cmbNoteState.SelectedItem = rec.State;
            }
        }

        private async Task SaveNoteChangesAsync()
        {
            if (string.IsNullOrEmpty(_selectedNoteId)) return;
            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    content   = txtNoteContent.Text,
                    scope     = cmbNoteScope.SelectedItem?.ToString(),
                    state     = cmbNoteState.SelectedItem?.ToString(),
                });
                await _http.PatchAsync($"memory/{_selectedNoteId}",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                await RefreshNotesAsync();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private async Task CreateNewNoteAsync()
        {
            string content = txtNoteContent.Text.Trim();
            if (string.IsNullOrEmpty(content)) { ShowError("Enter note content first."); return; }
            try
            {
                string? projectId = null;
                if (lstProjects.SelectedItem is ProjectFilterItem { Id: var pid } && pid != "")
                    projectId = pid;

                var body = JsonSerializer.Serialize(new
                {
                    content,
                    scope      = cmbNoteScope.SelectedItem?.ToString() ?? "global",
                    state      = cmbNoteState.SelectedItem?.ToString() ?? "durable",
                    project_id = projectId,
                });
                await _http.PostAsync("memory/promote",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                txtNoteContent.Clear();
                await RefreshNotesAsync();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private async Task ArchiveNoteAsync()
        {
            if (string.IsNullOrEmpty(_selectedNoteId)) return;
            if (MessageBox.Show("Archive this note?", "Confirm", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try
            {
                await _http.PostAsync($"memory/{_selectedNoteId}/archive", null);
                await RefreshNotesAsync();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private async Task DeleteNoteAsync()
        {
            if (string.IsNullOrEmpty(_selectedNoteId)) return;
            if (MessageBox.Show("Permanently delete this note? This cannot be undone.", "Confirm Delete",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                await _http.DeleteAsync($"memory/{_selectedNoteId}");
                await RefreshNotesAsync();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        // ── Context Inspector ─────────────────────────────────────────────

        private async Task InspectContextAsync()
        {
            try
            {
                var json = await _http.GetStringAsync($"debug/last-context?session_id={Uri.EscapeDataString(_callerSessionId)}");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var sb   = new StringBuilder();

                sb.AppendLine($"Context debug  session: {_callerSessionId}");
                sb.AppendLine(new string('═', 64));
                sb.AppendLine();

                // Budget summary row
                if (root.TryGetProperty("max_context_tokens", out var maxTok))
                    sb.AppendLine($"  max_context_tokens : {maxTok}");
                if (root.TryGetProperty("budget", out var bgt))
                {
                    sb.Append("  budgets            :");
                    foreach (var kv in bgt.EnumerateObject())
                        sb.Append($"  {kv.Name}={kv.Value}");
                    sb.AppendLine();
                }
                sb.AppendLine();

                // Render a note list embedded within a layer
                void RenderNotes(JsonElement layer)
                {
                    if (!layer.TryGetProperty("notes", out var notes) ||
                        notes.ValueKind != JsonValueKind.Array) return;
                    foreach (var note in notes.EnumerateArray())
                    {
                        var id      = note.TryGetProperty("id",      out var vi) ? vi.GetString() ?? "" : "";
                        var scope   = note.TryGetProperty("scope",   out var vs) ? vs.GetString() ?? "" : "";
                        var preview = note.TryGetProperty("preview", out var vp) ? vp.GetString() ?? "" : "";
                        var shortId = id.Length >= 8 ? id[..8] : id;
                        sb.AppendLine($"  [{scope}] {shortId}…  {preview}");
                    }
                }

                void RenderLayer(string key, string header)
                {
                    if (!root.TryGetProperty(key, out var layer)) return;
                    sb.AppendLine($"━━ {header}");

                    // Token + count summary on one line
                    if (layer.TryGetProperty("tokens", out var tok))
                        sb.Append($"  tokens: {tok}");
                    if (layer.TryGetProperty("count", out var cnt))
                    {
                        sb.Append($"    notes: {cnt}");
                        if (layer.TryGetProperty("injected_count", out var ic) &&
                            ic.GetInt32() != cnt.GetInt32())
                        {
                            var fa = layer.TryGetProperty("filter_applied", out var fav)
                                     ? fav.GetString() : "?";
                            sb.Append($" → {ic} injected ({fa} filter)");
                        }
                    }
                    if (layer.TryGetProperty("messages", out var msgs))
                        sb.Append($"    messages: {msgs}");
                    sb.AppendLine();

                    if (layer.TryGetProperty("name", out var pname))
                        sb.AppendLine($"  project: {pname}");

                    RenderNotes(layer);

                    // Document-chunk retrieval hits
                    if (layer.TryGetProperty("hits", out var hits) &&
                        hits.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var hit in hits.EnumerateArray())
                        {
                            var src   = hit.TryGetProperty("source", out var sv) ? sv.GetString() ?? "" : "";
                            var score = hit.TryGetProperty("score",  out var sc)
                                        ? sc.GetDouble().ToString("F3") : "?";
                            sb.AppendLine($"  score={score}  {src}");
                        }
                    }

                    // Grounding block meta
                    if (layer.TryGetProperty("injected", out var inj))
                        sb.AppendLine($"  injected: {inj}");
                    if (layer.TryGetProperty("project", out var gproj) &&
                        !layer.TryGetProperty("name", out _))
                        sb.AppendLine($"  project:  {gproj}");

                    sb.AppendLine();
                }

                RenderLayer("system",       "[1] SYSTEM IDENTITY");
                RenderLayer("operator",     "[2] OPERATOR NOTES");
                RenderLayer("project",      "[3] PROJECT NOTES + RESEARCH");
                RenderLayer("grounding",    "[GROUNDING RULES]");
                RenderLayer("global",       "[4] GLOBAL MEMORY");
                RenderLayer("retrieval",    "[5] RETRIEVED CHUNKS (doc search)");
                RenderLayer("summary",      "[6] SESSION SUMMARY");
                RenderLayer("conversation", "[7] RECENT CONVERSATION");
                RenderLayer("prompt",       "[8] CURRENT PROMPT");

                ShowTextViewer("Last Context — Layer Breakdown", sb.ToString());
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                MessageBox.Show("No context debug found for this session.\nSend at least one chat message first.", "Not found");
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private async Task ShowTokenBudgetAsync()
        {
            try
            {
                var json = await _http.GetStringAsync("debug/token-budget");
                using var doc = JsonDocument.Parse(json);
                var sb = new StringBuilder();
                sb.AppendLine("Token Budget Allocation");
                sb.AppendLine(new string('─', 40));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        sb.AppendLine($"\n{prop.Name}:");
                        foreach (var sub in prop.Value.EnumerateObject())
                            sb.AppendLine($"  {sub.Name}: {sub.Value}");
                    }
                    else
                    {
                        sb.AppendLine($"{prop.Name}: {prop.Value}");
                    }
                }
                ShowTextViewer("Token Budget", sb.ToString());
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        // ── Documents ─────────────────────────────────────────────────────

        private async Task RefreshDocsAsync()
        {
            if (lstDocProjects.SelectedItem is not ProjectFilterItem proj || proj.Id == "") return;
            try
            {
                var json = await _http.GetStringAsync($"projects/{proj.Id}/documents");
                using var doc = JsonDocument.Parse(json);
                gridDocs.Rows.Clear();
                foreach (var d in doc.RootElement.EnumerateArray())
                {
                    string id      = d.GetProperty("id").GetString() ?? "";
                    string fn      = d.GetProperty("filename").GetString() ?? "";
                    string dt      = d.GetProperty("doc_type").GetString() ?? "";
                    int    chunks  = d.TryGetProperty("chunk_count", out var cc) ? cc.GetInt32() : 0;
                    string status  = d.TryGetProperty("job_status",  out var js) ? js.GetString() ?? "" : "";
                    string date    = (d.GetProperty("ingested_at").GetString() ?? "")["".Length..Math.Min(10, d.GetProperty("ingested_at").GetString()?.Length ?? 0)];
                    int row = gridDocs.Rows.Add(id, fn, dt, chunks, status, date);
                    gridDocs.Rows[row].Tag = id;
                }
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private async void BtnIngestFile_Click(object? sender, EventArgs e)
        {
            if (lstDocProjects.SelectedItem is not ProjectFilterItem proj || proj.Id == "")
            { MessageBox.Show("Select a project first."); return; }

            using var dlg = new OpenFileDialog
            {
                Filter = "Documents|*.md;*.txt;*.pdf|Markdown|*.md|Text|*.txt|PDF|*.pdf|All files|*.*",
                Title  = "Select document to ingest"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            try
            {
                string filename = System.IO.Path.GetFileName(dlg.FileName);
                string ext      = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();

                if (ext == ".pdf")
                {
                    await IngestPdfAsync(dlg.FileName, filename, proj.Id);
                }
                else
                {
                    string content = System.IO.File.ReadAllText(dlg.FileName);
                    string docType = ext == ".md" ? "markdown" : "text";
                    string authority = cmbDocAuthority.SelectedItem?.ToString() ?? "Informational";
                    string purpose   = cmbDocType.SelectedItem?.ToString()      ?? "other";
                    string finality  = cmbDocFinality.SelectedItem?.ToString()  ?? "final";

                    var body = JsonSerializer.Serialize(new
                    {
                        project_id      = proj.Id,
                        filename,
                        content,
                        doc_type        = docType,
                        authority_level = authority,
                        document_type   = purpose,
                        finality
                    });
                    var resp = await _http.PostAsync("documents/ingest",
                        new StringContent(body, Encoding.UTF8, "application/json"));
                    resp.EnsureSuccessStatusCode();
                    var result = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    string jobId = result.RootElement.GetProperty("job_id").GetString() ?? "";
                    MessageBox.Show(
                        $"Ingestion job queued.\nJob ID: {jobId}\n\nChunking and embedding will complete in the background.",
                        "Ingestion Queued", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                await RefreshDocsAsync();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private async Task IngestPdfAsync(string filePath, string filename, string projectId)
        {
            // Stream the PDF as multipart/form-data to /documents/ingest-pdf.
            // The server extracts text via PyMuPDF and feeds the result through
            // the standard markdown ingestion pipeline.
            await using var stream  = System.IO.File.OpenRead(filePath);
            using var multipart     = new MultipartFormDataContent();
            using var fileContent   = new StreamContent(stream);
            fileContent.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            multipart.Add(fileContent,  "file",       filename);
            multipart.Add(new StringContent(projectId), "project_id");
            multipart.Add(new StringContent(cmbDocAuthority.SelectedItem?.ToString() ?? "Informational"), "authority_level");
            multipart.Add(new StringContent(cmbDocType.SelectedItem?.ToString()      ?? "other"),          "document_type");
            multipart.Add(new StringContent(cmbDocFinality.SelectedItem?.ToString()  ?? "final"),           "finality");

            var resp = await _http.PostAsync("documents/ingest-pdf", multipart);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                // Surface the server's detail message (e.g. "PDF may be scanned/image-only")
                string detail = err;
                try
                {
                    using var errDoc = JsonDocument.Parse(err);
                    if (errDoc.RootElement.TryGetProperty("detail", out var d))
                        detail = d.GetString() ?? err;
                }
                catch { /* use raw body */ }
                MessageBox.Show($"PDF ingestion failed:\n\n{detail}",
                    "Ingestion Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var result = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root  = result.RootElement;
            string jobId  = root.GetProperty("job_id").GetString() ?? "";
            int    pages  = root.TryGetProperty("pages",  out var pv) ? pv.GetInt32()  : 0;
            int    chars  = root.TryGetProperty("chars",  out var cv) ? cv.GetInt32()  : 0;
            string? sidecar = root.TryGetProperty("sidecar", out var sv) && sv.ValueKind == JsonValueKind.String
                              ? sv.GetString() : null;

            string msg = $"PDF ingested successfully.\n\nFile:   {filename}\nPages:  {pages}\nChars:  {chars:N0}\nJob ID: {jobId}\n\n" +
                         "Chunking and embedding will complete in the background.";
            if (!string.IsNullOrEmpty(sidecar))
                msg += $"\n\nMarkdown sidecar saved to:\n{sidecar}";

            MessageBox.Show(msg, "PDF Ingestion Queued", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async void BtnMoveDoc_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedDocId))
            { MessageBox.Show("Select a document first."); return; }

            // Fetch all projects to populate the picker
            List<(string Id, string Name)> projects;
            try
            {
                var json = await _http.GetStringAsync("projects");
                using var doc = JsonDocument.Parse(json);
                projects = doc.RootElement.EnumerateArray()
                    .Where(p => p.TryGetProperty("status", out var s) && s.GetString() == "active")
                    .Select(p => (
                        Id:   p.GetProperty("id").GetString()   ?? "",
                        Name: p.GetProperty("name").GetString() ?? ""))
                    .Where(p => p.Id.Length > 0)
                    .ToList();
            }
            catch (Exception ex) { ShowError($"Could not load projects: {ex.Message}"); return; }

            if (projects.Count == 0)
            { MessageBox.Show("No active projects found."); return; }

            var dlg = new Form
            {
                Text = "Move Document to Project",
                Size = new System.Drawing.Size(320, 150),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false,
                BackColor = Color.FromArgb(35, 35, 35), ForeColor = Color.White
            };
            var lbl = new Label { Text = "Select target project:", Location = new Point(12, 14), AutoSize = true, ForeColor = Color.Silver };
            var cmb = new ComboBox
            {
                Location = new Point(12, 34), Size = new Size(278, 24),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (var (_, name) in projects) cmb.Items.Add(name);
            cmb.SelectedIndex = 0;

            var btnOk     = new Button { Text = "Move",   DialogResult = DialogResult.OK,     Location = new Point(130, 70), Size = new Size(75, 28) };
            var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(215, 70), Size = new Size(75, 28) };
            dlg.Controls.AddRange(new Control[] { lbl, cmb, btnOk, btnCancel });
            dlg.AcceptButton = btnOk; dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            var (targetId, targetName) = projects[cmb.SelectedIndex];
            await MoveDocAsync(_selectedDocId, targetId, targetName);
        }

        private async Task MoveDocAsync(string docId, string targetProjectId, string targetProjectName)
        {
            try
            {
                var body = JsonSerializer.Serialize(new { project_id = targetProjectId });
                var resp = await _http.PatchAsync(
                    $"documents/{docId}/project",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                resp.EnsureSuccessStatusCode();
                await RefreshDocsAsync();
                MessageBox.Show(
                    $"Document moved to \"{targetProjectName}\".\n\nNote: promoted memory notes are preserved in their original project.",
                    "Moved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void BtnEditDocMeta_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedDocId))
            { MessageBox.Show("Select a document first."); return; }

            // Tiny editor dialog
            var dlg = new Form
            {
                Text = "Edit Document Metadata",
                Size = new System.Drawing.Size(340, 230),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false,
                BackColor = Color.FromArgb(35, 35, 35), ForeColor = Color.White
            };
            var lblA = new Label { Text = "Authority Level", Location = new Point(12,  14), AutoSize = true, ForeColor = Color.Silver };
            var cmbA = new ComboBox { Location = new Point(12, 32), Size = new Size(290, 24), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbA.Items.AddRange(new object[] { "Definitive", "Authoritative", "Informational", "Contextual", "Anecdotal" });
            cmbA.SelectedItem = cmbDocAuthority.SelectedItem ?? "Informational";

            var lblT = new Label { Text = "Document Type", Location = new Point(12, 62), AutoSize = true, ForeColor = Color.Silver };
            var cmbT = new ComboBox { Location = new Point(12, 80), Size = new Size(290, 24), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbT.Items.AddRange(new object[] { "published_framework", "operational_guide", "strategic_draft", "meeting_notes", "planning_discussion", "other" });
            cmbT.SelectedItem = cmbDocType.SelectedItem ?? "other";

            var lblF = new Label { Text = "Finality", Location = new Point(12, 110), AutoSize = true, ForeColor = Color.Silver };
            var cmbF = new ComboBox { Location = new Point(12, 128), Size = new Size(290, 24), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbF.Items.AddRange(new object[] { "final", "draft", "provisional" });
            cmbF.SelectedItem = cmbDocFinality.SelectedItem ?? "final";

            var btnOk     = new Button { Text = "Save",   DialogResult = DialogResult.OK,     Location = new Point(140, 160), Size = new Size(75, 28) };
            var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(225, 160), Size = new Size(75, 28) };
            dlg.Controls.AddRange(new Control[] { lblA, cmbA, lblT, cmbT, lblF, cmbF, btnOk, btnCancel });
            dlg.AcceptButton = btnOk; dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string auth     = cmbA.SelectedItem?.ToString() ?? "Informational";
            string purpose  = cmbT.SelectedItem?.ToString() ?? "other";
            string finality = cmbF.SelectedItem?.ToString() ?? "final";
            _ = PatchDocMetaAsync(_selectedDocId, auth, purpose, finality);
        }

        private async Task PatchDocMetaAsync(string docId, string auth, string docType, string finality)
        {
            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    authority_level = auth,
                    document_type   = docType,
                    finality
                });
                var resp = await _http.PatchAsync(
                    $"documents/{docId}/metadata",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                resp.EnsureSuccessStatusCode();
                await RefreshDocsAsync();
                MessageBox.Show("Metadata updated.", "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private async Task DeleteDocAsync()
        {
            if (string.IsNullOrEmpty(_selectedDocId)) return;
            if (MessageBox.Show("Delete this document and all its chunks?", "Confirm Delete",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                await _http.DeleteAsync($"documents/{_selectedDocId}");
                await RefreshDocsAsync();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        // ── Research Review ───────────────────────────────────────────────

        private async Task RefreshResearchAsync()
        {
            try
            {
                var json = await _http.GetStringAsync("research/pending");
                using var doc = JsonDocument.Parse(json);

                // Detach handler before bulk Add — Rows.Add() fires SelectionChanged
                // before we can set Tag on the next line, leaving Tag=null in the handler.
                gridResearch.SelectionChanged -= GridResearch_SelectionChanged;
                gridResearch.Rows.Clear();
                _candidateNotes.Clear();
                lstCandidates.Items.Clear();
                txtResearchRaw.Clear();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string id    = item.GetProperty("id").GetString() ?? "";
                    string query = item.GetProperty("query").GetString() ?? "";
                    string date  = (item.GetProperty("created_at").GetString() ?? "")[..Math.Min(10, item.GetProperty("created_at").GetString()?.Length ?? 0)];
                    int cands    = item.TryGetProperty("candidate_notes", out var cn) && cn.ValueKind == JsonValueKind.Array
                                   ? cn.GetArrayLength() : 0;
                    int row = gridResearch.Rows.Add(id, query, date, cands);
                    gridResearch.Rows[row].Tag = item.GetRawText();   // Tag set before handler re-hooks
                }
                gridResearch.SelectionChanged += GridResearch_SelectionChanged;
                // Call directly — do NOT rely on SelectionChanged firing here.
                // After Rows.Add() the grid may have already auto-selected row 0 while
                // the handler was detached, so setting Selected=true again is a no-op.
                if (gridResearch.Rows.Count > 0)
                    LoadResearchRowDetails(gridResearch.Rows[0]);
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        /// <summary>Populate Raw Result and Candidate Notes from the given grid row's Tag.</summary>
        private void LoadResearchRowDetails(DataGridViewRow row)
        {
            Debug.WriteLine($"[ResearchTab] LoadResearchRowDetails called. Row index={row.Index}");

            string rawJson = row.Tag as string ?? "";
            Debug.WriteLine($"[ResearchTab] Tag is {(row.Tag == null ? "NULL" : $"string, length={rawJson.Length}")}");
            Debug.WriteLine($"[ResearchTab] Tag preview: {rawJson[..Math.Min(200, rawJson.Length)]}");

            _selectedResearchId = row.Cells["colResId"].Value?.ToString() ?? "";
            Debug.WriteLine($"[ResearchTab] _selectedResearchId={_selectedResearchId}");

            _candidateNotes.Clear();
            lstCandidates.Items.Clear();
            txtResearchRaw.Clear();

            if (string.IsNullOrEmpty(rawJson))
            {
                Debug.WriteLine("[ResearchTab] rawJson is empty — aborting detail load");
                txtResearchRaw.Text = "[DEBUG] Tag was null/empty when row was selected. Try clicking Refresh.";
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                Debug.WriteLine("[ResearchTab] JSON parsed OK");

                bool hasRaw = doc.RootElement.TryGetProperty("raw_result", out var rr);
                Debug.WriteLine($"[ResearchTab] has raw_result={hasRaw}, ValueKind={rr.ValueKind}");
                txtResearchRaw.Text = hasRaw ? rr.GetString() ?? "" : "";
                Debug.WriteLine($"[ResearchTab] txtResearchRaw.Text length={txtResearchRaw.Text.Length}");

                bool hasCands = doc.RootElement.TryGetProperty("candidate_notes", out var cands);
                Debug.WriteLine($"[ResearchTab] has candidate_notes={hasCands}, ValueKind={cands.ValueKind}");
                if (hasCands && cands.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var c in cands.EnumerateArray())
                    {
                        string content     = c.TryGetProperty("content",      out var cv)  ? cv.GetString()  ?? "" : "";
                        string scope       = c.TryGetProperty("scope",        out var sv)  ? sv.GetString()  ?? "global" : "global";
                        string tags        = c.TryGetProperty("tags",         out var tv)  ? tv.GetString()  ?? "" : "";
                        string game        = c.TryGetProperty("game",         out var gmv) ? gmv.GetString() ?? "" : "";
                        string entityClass = c.TryGetProperty("entity_class", out var ecv) ? ecv.GetString() ?? "" : "";
                        string buildTopic  = c.TryGetProperty("build_topic",  out var btv) ? btv.GetString() ?? "" : "";
                        string season      = c.TryGetProperty("season",       out var snv) ? snv.GetString() ?? "" : "";
                        string noteType    = c.TryGetProperty("note_type",    out var ntv) ? ntv.GetString() ?? "" : "";
                        bool   patchSens   = c.TryGetProperty("patch_sensitive", out var psv) && psv.GetBoolean();

                        // Preserve all fields so they survive the promote round-trip
                        var noteDict = new Dictionary<string, object>
                        {
                            ["content"]       = content,
                            ["scope"]         = scope,
                            ["tags"]          = tags,
                            ["state"]         = "durable",
                            ["game"]          = game,
                            ["entity_class"]  = entityClass,
                            ["build_topic"]   = buildTopic,
                            ["season"]        = season,
                            ["note_type"]     = noteType,
                            ["patch_sensitive"] = patchSens,
                        };
                        _candidateNotes.Add(noteDict);

                        // Build display label: [scope] [Game | Class | Build | Season] content…
                        var anchors = new[] { game, entityClass, buildTopic, season }
                            .Where(s => !string.IsNullOrEmpty(s));
                        string anchorStr = anchors.Any() ? $"[{string.Join(" | ", anchors)}] " : "";
                        string preview   = content.Length > 80 ? content[..80] + "…" : content;
                        lstCandidates.Items.Add($"[{scope}] {anchorStr}{preview}", true);
                        Debug.WriteLine($"[ResearchTab]   candidate[{i++}]: scope={scope} anchors={anchorStr} content={content[..Math.Min(60, content.Length)]}");
                    }
                    Debug.WriteLine($"[ResearchTab] Total candidates loaded: {_candidateNotes.Count}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ResearchTab] JSON parse EXCEPTION: {ex.Message}");
                txtResearchRaw.Text = $"[DEBUG] JSON parse failed: {ex.Message}\n\nRaw Tag (first 500):\n{rawJson[..Math.Min(500, rawJson.Length)]}";
            }
        }

        private void GridResearch_SelectionChanged(object? sender, EventArgs e)
        {
            Debug.WriteLine($"[ResearchTab] SelectionChanged fired. SelectedRows.Count={gridResearch.SelectedRows.Count}");
            if (gridResearch.SelectedRows.Count == 0) return;
            LoadResearchRowDetails(gridResearch.SelectedRows[0]);
        }

        private void GridResearch_CellClick(object? sender, DataGridViewCellEventArgs e)
        {
            Debug.WriteLine($"[ResearchTab] CellClick fired. RowIndex={e.RowIndex}");
            if (e.RowIndex < 0 || e.RowIndex >= gridResearch.Rows.Count) return;
            LoadResearchRowDetails(gridResearch.Rows[e.RowIndex]);
        }

        private async Task PromoteResearchAsync()
        {
            if (string.IsNullOrEmpty(_selectedResearchId)) return;
            var selected = new List<Dictionary<string, object>>();
            for (int i = 0; i < lstCandidates.CheckedIndices.Count; i++)
            {
                int idx = lstCandidates.CheckedIndices[i];
                if (idx < _candidateNotes.Count)
                    selected.Add(_candidateNotes[idx]);
            }
            if (selected.Count == 0) { MessageBox.Show("Check at least one candidate to promote."); return; }
            try
            {
                var body = JsonSerializer.Serialize(new { selected_notes = selected });
                var resp = await _http.PostAsync($"research/{_selectedResearchId}/promote",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                resp.EnsureSuccessStatusCode();
                MessageBox.Show($"{selected.Count} note(s) promoted to memory.", "Promoted");
                await RefreshResearchAsync();
                await RefreshNotesAsync();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private async Task DiscardResearchAsync()
        {
            if (string.IsNullOrEmpty(_selectedResearchId)) return;
            if (MessageBox.Show("Discard this research item?", "Confirm", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try
            {
                await _http.DeleteAsync($"research/{_selectedResearchId}");
                await RefreshResearchAsync();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        // ── Research Trace tab ────────────────────────────────────────────

        private void BuildTraceTab()
        {
            tabTrace = new TabPage("Research Trace") { BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };

            var btnPanel = new Panel
            {
                Dock = DockStyle.Bottom, Height = 36,
                BackColor = Color.FromArgb(30, 30, 30), Padding = new Padding(6, 5, 6, 5)
            };
            btnRefreshTrace = MakeButton("Refresh", new Point(0, 4), new Size(110, 26));
            btnRefreshTrace.Click += (_, _) => _ = RefreshTraceAsync();
            var btnViewRaw = MakeButton("View Full Result", new Point(118, 4), new Size(140, 26));
            btnViewRaw.Click += (_, _) => _ = ViewTraceRawAsync();
            btnPanel.Controls.AddRange(new Control[] { btnRefreshTrace, btnViewRaw });

            txtTraceDetail = new TextBox
            {
                Dock = DockStyle.Bottom, Height = 260,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(40, 40, 40), ForeColor = Color.LightGray,
                Font = new Font("Consolas", 9f), WordWrap = false
            };
            var lblDetail = new Label
            {
                Text = "Trace Detail:", ForeColor = Color.Silver,
                Dock = DockStyle.Bottom, Height = 18, TextAlign = ContentAlignment.BottomLeft,
                Padding = new Padding(6, 0, 0, 0)
            };

            gridTrace = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(40, 40, 40), ForeColor = Color.White,
                ColumnHeadersDefaultCellStyle = { BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White },
                DefaultCellStyle = { BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, SelectionBackColor = Color.SteelBlue },
                ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            gridTrace.Columns.Add(new DataGridViewTextBoxColumn { Name = "colTrDate",   HeaderText = "Date",       FillWeight = 16 });
            gridTrace.Columns.Add(new DataGridViewTextBoxColumn { Name = "colTrState",  HeaderText = "State",      FillWeight = 10 });
            gridTrace.Columns.Add(new DataGridViewTextBoxColumn { Name = "colTrModel",  HeaderText = "Model",      FillWeight = 14 });
            gridTrace.Columns.Add(new DataGridViewTextBoxColumn { Name = "colTrTopic",  HeaderText = "Topic",      FillWeight = 40 });
            gridTrace.Columns.Add(new DataGridViewTextBoxColumn { Name = "colTrQCount", HeaderText = "Queries",    FillWeight = 8  });
            gridTrace.Columns.Add(new DataGridViewTextBoxColumn { Name = "colTrSCount", HeaderText = "Sources",    FillWeight = 8  });
            gridTrace.Columns.Add(new DataGridViewTextBoxColumn { Name = "colTrCCount", HeaderText = "Candidates", FillWeight = 8  });
            gridTrace.CellClick       += GridTrace_CellClick;
            gridTrace.SelectionChanged += GridTrace_SelectionChanged;

            // Bottom-docked controls stack upward; Fill grid takes remaining space
            tabTrace.Controls.Add(btnPanel);
            tabTrace.Controls.Add(txtTraceDetail);
            tabTrace.Controls.Add(lblDetail);
            tabTrace.Controls.Add(gridTrace);
        }

        private async Task RefreshTraceAsync()
        {
            try
            {
                var json = await _http.GetStringAsync("research/recent");
                using var doc = JsonDocument.Parse(json);
                gridTrace.SelectionChanged -= GridTrace_SelectionChanged;
                gridTrace.Rows.Clear();
                txtTraceDetail.Clear();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string id    = item.TryGetProperty("id",         out var idv)  ? idv.GetString()  ?? "" : "";
                    string state = item.TryGetProperty("state",      out var sv)   ? sv.GetString()   ?? "" : "";
                    string model = item.TryGetProperty("model",      out var mv)   && mv.ValueKind != JsonValueKind.Null ? mv.GetString() ?? "" : "";
                    string ca    = item.TryGetProperty("created_at", out var cav)  ? cav.GetString()  ?? "" : "";
                    string date  = ca.Length >= 16 ? ca[..16] : ca;

                    string topic  = "";
                    int qCount = 0, sCount = 0, cCount = 0;
                    if (item.TryGetProperty("trace_json", out var tj) && tj.ValueKind == JsonValueKind.Object)
                    {
                        topic  = tj.TryGetProperty("topic",           out var tpv) ? tpv.GetString() ?? "" : "";
                        qCount = tj.TryGetProperty("query_count",     out var qcv) ? qcv.GetInt32() : 0;
                        sCount = tj.TryGetProperty("source_count",    out var scv) ? scv.GetInt32() : 0;
                        cCount = tj.TryGetProperty("candidate_count", out var ccv) ? ccv.GetInt32() : 0;
                    }
                    if (string.IsNullOrEmpty(topic) && item.TryGetProperty("query", out var qv))
                        topic = qv.GetString() ?? "";

                    int row = gridTrace.Rows.Add(date, state, model, topic, qCount, sCount, cCount);
                    gridTrace.Rows[row].Tag = new TraceRowData(id, item.GetRawText());
                }
                gridTrace.SelectionChanged += GridTrace_SelectionChanged;
                if (gridTrace.Rows.Count > 0)
                    ShowTraceDetail(gridTrace.Rows[0]);
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void ShowTraceDetail(DataGridViewRow row)
        {
            _selectedTraceId = "";
            txtTraceDetail.Clear();
            if (row.Tag is not TraceRowData entry) return;
            _selectedTraceId = entry.Id;
            try
            {
                using var doc = JsonDocument.Parse(entry.RawJson);
                var root = doc.RootElement;
                string state  = root.TryGetProperty("state",      out var sv)  ? sv.GetString()  ?? "" : "";
                string model  = root.TryGetProperty("model",      out var mv)  && mv.ValueKind != JsonValueKind.Null ? mv.GetString() ?? "(unknown)" : "(unknown)";
                string ca     = root.TryGetProperty("created_at", out var cav) ? cav.GetString() ?? "" : "";
                string prompt = root.TryGetProperty("query",      out var prv) ? prv.GetString() ?? "" : "";

                var sb = new StringBuilder();
                sb.AppendLine(new string('=', 64));
                sb.AppendLine($"RESEARCH TRACE — {ca}");
                sb.AppendLine($"State: {state}  |  Model: {model}");
                sb.AppendLine(new string('=', 64));
                sb.AppendLine();
                sb.AppendLine("ORIGINAL PROMPT:");
                sb.AppendLine($"  {prompt}");
                sb.AppendLine();

                if (root.TryGetProperty("trace_json", out var tj) && tj.ValueKind == JsonValueKind.Object)
                {
                    string topic = tj.TryGetProperty("topic", out var tpv) ? tpv.GetString() ?? "" : "";
                    sb.AppendLine("TOPIC:");
                    sb.AppendLine($"  {topic}");
                    sb.AppendLine();

                    if (tj.TryGetProperty("entity_interpretations", out var ei) && ei.ValueKind == JsonValueKind.Array)
                    {
                        var interpLines = new List<string>();
                        foreach (var interp in ei.EnumerateArray())
                        {
                            string orig = interp.TryGetProperty("original",   out var ov) ? ov.GetString() ?? "" : "";
                            string used = interp.TryGetProperty("used_as",    out var uv) ? uv.GetString() ?? "" : "";
                            double conf = interp.TryGetProperty("confidence", out var cv) && cv.ValueKind != JsonValueKind.Null ? cv.GetDouble() : 1.0;
                            if (!string.IsNullOrEmpty(orig) && !string.IsNullOrEmpty(used)
                                && !orig.Equals(used, StringComparison.OrdinalIgnoreCase))
                            {
                                string flag = conf < 0.8 ? "⚠" : "ℹ";
                                interpLines.Add($"  {flag} '{orig}' → '{used}' ({conf:P0} confidence)");
                            }
                        }
                        if (interpLines.Count > 0)
                        {
                            sb.AppendLine("ENTITY INTERPRETATIONS:");
                            foreach (var l in interpLines) sb.AppendLine(l);
                            sb.AppendLine();
                        }
                    }

                    if (tj.TryGetProperty("queries", out var qs) && qs.ValueKind == JsonValueKind.Array)
                    {
                        int qCount = tj.TryGetProperty("query_count", out var qcv) ? qcv.GetInt32() : qs.GetArrayLength();
                        sb.AppendLine($"GENERATED QUERIES ({qCount}):");
                        int i = 1;
                        foreach (var q in qs.EnumerateArray())
                            sb.AppendLine($"  {i++}. {q.GetString()}");
                        sb.AppendLine();
                    }

                    if (tj.TryGetProperty("sources", out var srcs) && srcs.ValueKind == JsonValueKind.Array)
                    {
                        int sCount = tj.TryGetProperty("source_count",  out var scv) ? scv.GetInt32() : srcs.GetArrayLength();
                        int aCount = tj.TryGetProperty("answers_count", out var acv) ? acv.GetInt32() : 0;
                        sb.AppendLine($"SOURCES ({sCount} unique{(aCount > 0 ? $", {aCount} inline answer(s)" : "")}):");
                        foreach (var s in srcs.EnumerateArray())
                        {
                            string title = s.TryGetProperty("title",    out var ttv)  ? ttv.GetString()  ?? "?" : "?";
                            string url   = s.TryGetProperty("url",      out var urlv) ? urlv.GetString() ?? ""  : "";
                            string subq  = s.TryGetProperty("subquery", out var sqv)  ? sqv.GetString()  ?? ""  : "";
                            string subqS = subq.Length > 58 ? subq[..58] + "…" : subq;
                            sb.AppendLine($"  [{title}]");
                            sb.AppendLine($"    {url}");
                            if (!string.IsNullOrEmpty(subq))
                                sb.AppendLine($"    via: \"{subqS}\"");
                        }
                        sb.AppendLine();
                    }

                    int synthLen = tj.TryGetProperty("synthesis_length", out var slv)  ? slv.GetInt32() : 0;
                    int cands    = tj.TryGetProperty("candidate_count",  out var ccv2) ? ccv2.GetInt32() : 0;
                    sb.AppendLine($"SYNTHESIS: {synthLen:#,0} chars  |  CANDIDATES: {cands}");
                }
                else
                {
                    sb.AppendLine("(No structured trace — this run predates the Research Trace feature)");
                    sb.AppendLine("Use 'View Full Result' to see the raw synthesis text.");
                }

                txtTraceDetail.Text = sb.ToString();
            }
            catch (Exception ex)
            {
                txtTraceDetail.Text = $"Failed to parse trace: {ex.Message}";
            }
        }

        private void GridTrace_SelectionChanged(object? sender, EventArgs e)
        {
            if (gridTrace.SelectedRows.Count == 0) return;
            ShowTraceDetail(gridTrace.SelectedRows[0]);
        }

        private void GridTrace_CellClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= gridTrace.Rows.Count) return;
            ShowTraceDetail(gridTrace.Rows[e.RowIndex]);
        }

        private async Task ViewTraceRawAsync()
        {
            if (string.IsNullOrEmpty(_selectedTraceId)) return;
            try
            {
                var json = await _http.GetStringAsync($"research/{_selectedTraceId}/trace");
                using var doc = JsonDocument.Parse(json);
                string raw = doc.RootElement.TryGetProperty("raw_result", out var rv)
                             ? rv.GetString() ?? "(empty)"
                             : "(no raw_result in response)";
                string shortId = _selectedTraceId.Length >= 8 ? _selectedTraceId[..8] : _selectedTraceId;
                ShowTextViewer($"Research Result — {shortId}…", raw);
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private static Button MakeButton(string text, Point loc, Size size)
        {
            return new Button
            {
                Text = text, Location = loc, Size = size,
                BackColor = Color.FromArgb(60, 60, 80), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
            };
        }

        private static void ShowTextViewer(string title, string content)
        {
            var frm = new Form
            {
                Text = title, Size = new Size(800, 600),
                BackColor = Color.FromArgb(30, 30, 30)
            };
            var tb = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Both, WordWrap = false,
                BackColor = Color.FromArgb(40, 40, 40), ForeColor = Color.LightGray,
                Font = new Font("Consolas", 10f), Text = content
            };
            frm.Controls.Add(tb);
            frm.Show();
        }

        private void ShowError(string msg) =>
            MessageBox.Show(msg, "IRIS Memory Admin Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

        // ── Helper record types ────────────────────────────────────────────

        private record NoteRecord(string Id, string Content, string Scope, string State);
        private record ProjectFilterItem(string Id, string Name)
        {
            public override string ToString() => Name;
        }
        private record TraceRowData(string Id, string RawJson);
    }
}
