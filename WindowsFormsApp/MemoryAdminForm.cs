using System;
using System.Collections.Generic;
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

        // Research tab
        private TabPage tabResearch;
        private DataGridView gridResearch;
        private TextBox txtResearchRaw;
        private CheckedListBox lstCandidates;
        private Button btnPromoteResearch;
        private Button btnDiscardResearch;
        private Button btnRefreshResearch;

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
            Controls.Add(tabMain);

            BuildMemoryTab();
            BuildDocumentsTab();
            BuildResearchTab();

            tabMain.TabPages.AddRange(new[] { tabMemory, tabDocs, tabResearch });
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

            leftPanel.Controls.AddRange(new Control[] { lblDocProj, lstDocProjects, btnRefreshDocs, btnIngestFile, btnDeleteDoc });

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
            gridDocs.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDocId",      HeaderText = "ID",       FillWeight = 10 });
            gridDocs.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDocFile",     HeaderText = "Filename", FillWeight = 35 });
            gridDocs.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDocType",     HeaderText = "Type",     FillWeight = 10 });
            gridDocs.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDocChunks",   HeaderText = "Chunks",   FillWeight = 10 });
            gridDocs.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDocStatus",   HeaderText = "Status",   FillWeight = 15 });
            gridDocs.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDocIngested", HeaderText = "Ingested", FillWeight = 20 });
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

            // Top: research items grid
            gridResearch = new DataGridView
            {
                Location = new Point(6, 6), Height = 200, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackgroundColor = Color.FromArgb(40, 40, 40), ForeColor = Color.White,
                ColumnHeadersDefaultCellStyle = { BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White },
                DefaultCellStyle = { BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, SelectionBackColor = Color.SteelBlue },
                ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            gridResearch.Columns.Add(new DataGridViewTextBoxColumn { Name = "colResId",   HeaderText = "ID",    FillWeight = 10 });
            gridResearch.Columns.Add(new DataGridViewTextBoxColumn { Name = "colResQuery", HeaderText = "Query", FillWeight = 50 });
            gridResearch.Columns.Add(new DataGridViewTextBoxColumn { Name = "colResDate",  HeaderText = "Date",  FillWeight = 20 });
            gridResearch.Columns.Add(new DataGridViewTextBoxColumn { Name = "colResCands", HeaderText = "Candidates", FillWeight = 10 });
            gridResearch.SelectionChanged += GridResearch_SelectionChanged;

            // Middle: raw result viewer
            var lblRaw = new Label { Text = "Raw Result", ForeColor = Color.Silver, AutoSize = true, Location = new Point(6, 214) };
            txtResearchRaw = new TextBox
            {
                Location = new Point(6, 232), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Height = 100, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(40, 40, 40), ForeColor = Color.LightGray, Font = new Font("Consolas", 9f)
            };

            // Bottom: candidate notes checklist
            var lblCands = new Label { Text = "Candidate Notes — check to promote:", ForeColor = Color.Silver, AutoSize = true, Location = new Point(6, 340) };
            lstCandidates = new CheckedListBox
            {
                Location = new Point(6, 358), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                Height = 180, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, Font = new Font("Consolas", 9f)
            };

            int btnY = 546;
            btnRefreshResearch = MakeButton("Refresh",          new Point(6, btnY),   new Size(110, 26)); btnRefreshResearch.Click += (_, _) => _ = RefreshResearchAsync();
            btnPromoteResearch = MakeButton("Promote Selected", new Point(124, btnY), new Size(140, 26)); btnPromoteResearch.BackColor = Color.FromArgb(0, 120, 60); btnPromoteResearch.Click += (_, _) => _ = PromoteResearchAsync();
            btnDiscardResearch = MakeButton("Discard",          new Point(272, btnY), new Size(100, 26)); btnDiscardResearch.BackColor = Color.DarkRed; btnDiscardResearch.Click += (_, _) => _ = DiscardResearchAsync();

            tabResearch.Controls.AddRange(new Control[]
            {
                gridResearch, lblRaw, txtResearchRaw, lblCands, lstCandidates,
                btnRefreshResearch, btnPromoteResearch, btnDiscardResearch
            });

            // Wire resize for controls that need it
            tabResearch.Resize += (_, _) =>
            {
                int w = tabResearch.ClientSize.Width - 12;
                gridResearch.Width = w;
                txtResearchRaw.Width = w;
                lstCandidates.Width = w;
            };
        }

        // ── Tab switching ─────────────────────────────────────────────────

        private void OnTabChanged()
        {
            if (tabMain.SelectedTab == tabDocs)
                _ = RefreshDocProjectsAsync();
            else if (tabMain.SelectedTab == tabResearch)
                _ = RefreshResearchAsync();
        }

        // ── Projects ──────────────────────────────────────────────────────

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
                var sb = new StringBuilder();
                sb.AppendLine($"Context debug for session: {_callerSessionId}");
                sb.AppendLine(new string('─', 60));
                sb.AppendLine(doc.RootElement.ToString());
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
                Filter = "Markdown / Text|*.md;*.txt|All files|*.*",
                Title = "Select document to ingest"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            try
            {
                string content  = System.IO.File.ReadAllText(dlg.FileName);
                string filename = System.IO.Path.GetFileName(dlg.FileName);
                string ext      = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
                string docType  = ext == ".md" ? "markdown" : "text";

                var body = JsonSerializer.Serialize(new
                {
                    project_id = proj.Id,
                    filename,
                    content,
                    doc_type = docType
                });
                var resp = await _http.PostAsync("documents/ingest",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                resp.EnsureSuccessStatusCode();
                var result = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                string jobId = result.RootElement.GetProperty("job_id").GetString() ?? "";
                MessageBox.Show($"Ingestion job queued.\nJob ID: {jobId}\n\nChunking and embedding will complete in the background.",
                    "Ingestion Queued", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await RefreshDocsAsync();
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
                    gridResearch.Rows[row].Tag = item.GetRawText();
                }
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void GridResearch_SelectionChanged(object? sender, EventArgs e)
        {
            if (gridResearch.SelectedRows.Count == 0) return;
            string rawJson = gridResearch.SelectedRows[0].Tag as string ?? "";
            _selectedResearchId = gridResearch.SelectedRows[0].Cells["colResId"].Value?.ToString() ?? "";
            _candidateNotes.Clear();
            lstCandidates.Items.Clear();
            txtResearchRaw.Clear();
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                txtResearchRaw.Text = doc.RootElement.TryGetProperty("raw_result", out var rr)
                    ? rr.GetString() ?? "" : "";

                if (doc.RootElement.TryGetProperty("candidate_notes", out var cands)
                    && cands.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in cands.EnumerateArray())
                    {
                        string content = c.TryGetProperty("content", out var cv) ? cv.GetString() ?? "" : "";
                        string scope   = c.TryGetProperty("scope",   out var sv) ? sv.GetString() ?? "global" : "global";
                        string tags    = c.TryGetProperty("tags",    out var tv) ? tv.GetString() ?? "" : "";
                        _candidateNotes.Add(new Dictionary<string, object>
                        {
                            ["content"] = content, ["scope"] = scope, ["tags"] = tags, ["state"] = "durable"
                        });
                        lstCandidates.Items.Add($"[{scope}] {content}", true);
                    }
                }
            }
            catch { /* malformed JSON — leave empty */ }
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
    }
}
