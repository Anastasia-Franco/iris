using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WindowsFormsApp
{
    public partial class IRIS_UI : Form
    {
        private static readonly HttpClient _httpClient = new()
        {
            BaseAddress = new Uri("http://10.10.10.2:8000/"),
            Timeout = TimeSpan.FromMinutes(10)
        };

        // ── P/Invoke for suspend-redraw and reliable scroll ──────────────────────
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int WM_SETREDRAW = 0x000B;
        private const int WM_VSCROLL   = 0x0115;
        private const int SB_BOTTOM    = 7;

        private static void SuspendDrawing(Control c) =>
            SendMessage(c.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        private static void ResumeDrawing(Control c)
        {
            SendMessage(c.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            c.Refresh();
        }
        private void ScrollToBottom() =>
            SendMessage(txtStream.Handle, WM_VSCROLL, (IntPtr)SB_BOTTOM, IntPtr.Zero);

        // ── Core state ──────────────────────────────────────────────────────────
        private string _currentSessionId = Guid.NewGuid().ToString();
        private string _currentProjectId = "";
        private StringBuilder _streamBuffer = new();
        private string _preambleRtf = "";
        private bool _inThinkBlock = false;  // tracks <think> block state across stream chunks
        private string _lastCleanResponse = "";  // last response, think-blocks stripped, for export

        // ── Telemetry ────────────────────────────────────────────────────────────
        private long _t_requestStart;   // Environment.TickCount64 (ms)
        private long _t_networkSend;    // time Send() is called
        private long _t_firstToken;     // time first content byte arrives
        private long _t_finalToken;     // time stream closes
        private long _t_renderDone;     // time AppendMarkdown completes
        private int  _tokenCount;
        private long _serverContextMs;  // from server telemetry packet
        private long _serverOllamaFirstTokenMs;
        private double _serverTps;

        // ── Token buffer / 75 ms flush timer ────────────────────────────────────
        private readonly StringBuilder _pendingDisplay = new();
        private System.Windows.Forms.Timer? _flushTimer;
        private bool _receivedFirstToken;

        // ── Spinner / cancel state ──────────────────────────────────────────────
        private bool _isResearching = false;
        private CancellationTokenSource? _researchCts;
        private System.Windows.Forms.Timer? _spinnerTimer;
        private int _spinnerFrame = 0;
        private static readonly string[] SpinnerFrames =
            { "/ Cancel", "- Cancel", "\\ Cancel", "| Cancel" };

        private readonly Font _mdDefault;
        private readonly Font _mdBold;
        private readonly Font _mdItalic;
        private readonly Font _mdBoldItalic;
        private readonly Font _mdCode;
        private readonly Font _mdH1;
        private readonly Font _mdH2;
        private readonly Font _mdH3;

        public IRIS_UI()
        {
            InitializeComponent();
            // Default to qwen3:30b if it's in the list; otherwise use the first item
            int preferredModel = cmbModel.FindStringExact("qwen3:30b");
            cmbModel.SelectedIndex = preferredModel >= 0 ? preferredModel : 0;
            cmbTask.SelectedItem = "Chat";
            cmbTask.SelectedIndex = 0;

            _mdDefault    = new Font("Segoe UI Emoji", 12f);
            _mdBold       = new Font("Segoe UI Emoji", 12f, FontStyle.Bold);
            _mdItalic     = new Font("Segoe UI Emoji", 12f, FontStyle.Italic);
            _mdBoldItalic = new Font("Segoe UI Emoji", 12f, FontStyle.Bold | FontStyle.Italic);
            _mdCode       = new Font("Consolas", 11f);
            _mdH1         = new Font("Segoe UI Emoji", 16f, FontStyle.Bold);
            _mdH2         = new Font("Segoe UI Emoji", 14f, FontStyle.Bold);
            _mdH3         = new Font("Segoe UI Emoji", 13f, FontStyle.Bold);

            _ = LoadProjectsAsync();
            SetupExportStrip();

            // URL hyperlinking — let the RichTextBox detect URLs automatically.
            txtStream.DetectUrls = true;
            txtStream.LinkClicked += (_, e) =>
            {
                if (e.LinkText is { Length: > 0 } url)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                    }
                    catch { /* best-effort */ }
                }
            };
        }

        private async System.Threading.Tasks.Task LoadProjectsAsync()
        {
            try
            {
                using var resp = await _httpClient.GetAsync("projects");
                if (!resp.IsSuccessStatusCode) return;
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                string previousId = _currentProjectId; // preserve selection across reload

                cmbProject.Items.Clear();
                cmbProject.Items.Add("(no project)");
                foreach (var proj in doc.RootElement.EnumerateArray())
                {
                    string name = proj.GetProperty("name").GetString() ?? "";
                    string id   = proj.GetProperty("id").GetString() ?? "";
                    cmbProject.Items.Add(new ProjectItem(id, name));
                }

                // Restore previous selection if it still exists (not archived/deleted)
                if (!string.IsNullOrEmpty(previousId))
                {
                    for (int i = 1; i < cmbProject.Items.Count; i++)
                    {
                        if (cmbProject.Items[i] is ProjectItem p && p.Id == previousId)
                        {
                            cmbProject.SelectedIndex = i;
                            return;
                        }
                    }
                }
                cmbProject.SelectedIndex = 0; // fall back to (no project)
            }
            catch { /* server may be unreachable at startup */ }
        }

        private async void BtnSend_Click(object sender, EventArgs e)
        {
            // If the spinner is running this is a Cancel click
            if (_isResearching)
            {
                _researchCts?.Cancel();
                return;
            }

            string prompt = txtPrompt.Text.Trim();
            if (string.IsNullOrWhiteSpace(prompt))
                return;

            string selectedModel = cmbModel.SelectedItem?.ToString() ?? "qwen3:30b";
            string selectedTask  = cmbTask.SelectedItem?.ToString() ?? "Chat";
            string selectedMode  = selectedTask switch
            {
                "Research" => "research",
                _ => "prompt"
            };
            string? projectId = string.IsNullOrEmpty(_currentProjectId) ? null : _currentProjectId;

            // ── Research pre-flight: show immediate progress, then decompose + confirm ──
            if (selectedMode == "research")
            {
                // Require a project — research results need somewhere to be promoted to.
                if (string.IsNullOrEmpty(_currentProjectId))
                {
                    var chosenId = PromptProjectSelection();
                    if (chosenId == null) return;
                    projectId = chosenId;
                }

                btnSend.Enabled = false;

                // Show progress immediately so there is no dead time while the
                // subquery decomposition LLM call runs on the server.
                string preFlightRtf = txtStream.Rtf;   // snapshot to restore on cancel
                txtStream.SelectionFont  = _mdBold;
                txtStream.SelectionColor = Color.SteelBlue;
                txtStream.AppendText("You: ");
                txtStream.SelectionFont  = _mdDefault;
                txtStream.SelectionColor = SystemColors.WindowText;
                txtStream.AppendText(prompt + Environment.NewLine + Environment.NewLine);
                txtStream.SelectionFont  = _mdBold;
                txtStream.SelectionColor = Color.MediumSlateBlue;
                txtStream.AppendText("IRIS: ");
                txtStream.SelectionFont  = _mdCode;
                txtStream.SelectionColor = Color.DimGray;
                txtStream.AppendText("\u27f3 Generating research subqueries\u2026\n");
                ScrollToBottom();
                txtStream.Refresh();    // force repaint NOW — paint must land before PostAsync starts

                bool proceed = await ResearchPreviewAsync(prompt, selectedModel, projectId);
                if (!proceed)
                {
                    // User cancelled preview — restore chat to pre-flight state
                    txtStream.Rtf    = preFlightRtf;
                    btnSend.Enabled  = true;
                    return;
                }

                // User confirmed — arm cancel token and start animated spinner
                _researchCts   = new CancellationTokenSource();
                _isResearching = true;
                StartSpinner();
            }
            else
            {
                btnSend.Enabled = false;
            }

            // ── Write user prompt header (research already wrote it above) ─────────
            if (selectedMode != "research")
            {
                txtStream.SelectionFont  = _mdBold;
                txtStream.SelectionColor = Color.SteelBlue;
                txtStream.AppendText("You: ");
                txtStream.SelectionFont  = _mdDefault;
                txtStream.SelectionColor = SystemColors.WindowText;
                txtStream.AppendText(prompt + Environment.NewLine + Environment.NewLine);

                txtStream.SelectionFont  = _mdBold;
                txtStream.SelectionColor = Color.MediumSlateBlue;
                txtStream.AppendText("IRIS: ");
                txtStream.SelectionFont  = _mdDefault;
                txtStream.SelectionColor = SystemColors.WindowText;
            }
            else
            {
                // Research: replace the "Generating subqueries…" line with proper IRIS label
                // by restoring back to just after "IRIS: " preamble
                txtStream.SelectionFont  = _mdBold;
                txtStream.SelectionColor = Color.MediumSlateBlue;
                // Trim the subquery progress line — go back to just after "IRIS: "
                // by searching backwards for the last "IRIS: " in the RTF.
                // Simplest: just append new IRIS label after the progress text stays.
                // The progress line is part of the record — leave it.
                txtStream.SelectionFont  = _mdDefault;
                txtStream.SelectionColor = SystemColors.WindowText;
            }
            // Snapshot RTF — raw stream tokens appended here; restored + re-rendered at end.
            _preambleRtf = txtStream.Rtf;
            _streamBuffer.Clear();

            // For research: inject an immediate local progress line so there is
            // visible motion before the first server-streamed byte arrives.
            if (selectedMode == "research")
            {
                const string localStart = "[\u27f3 Research] Starting orchestration\u2026\n";
                _streamBuffer.Append(localStart);
                txtStream.SelectionFont  = _mdCode;
                txtStream.SelectionColor = Color.DimGray;
                txtStream.AppendText(localStart);
                ScrollToBottom();
            }
            else
            {
                // Chat: temporary indicator — not buffered; cleared when preambleRtf is restored
                txtStream.SelectionFont  = _mdCode;
                txtStream.SelectionColor = Color.DimGray;
                txtStream.AppendText("\u231b Thinking\u2026");
                ScrollToBottom();
            }

            var requestBody = new
            {
                session_id = _currentSessionId,
                project_id = projectId,
                prompt,
                model = selectedModel,
                mode  = selectedMode
            };
            var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            var ct = _researchCts?.Token ?? CancellationToken.None;

            // ── Telemetry / buffer-timer setup ──────────────────────────────────────
            _t_requestStart        = Environment.TickCount64;
            _t_networkSend         = 0; _t_firstToken = 0; _t_finalToken = 0; _t_renderDone = 0;
            _tokenCount            = 0;
            _serverContextMs       = 0; _serverOllamaFirstTokenMs = 0; _serverTps = 0;
            _receivedFirstToken    = false;
            _pendingDisplay.Clear();
            _inThinkBlock          = false;

            // 75 ms flush timer — accumulates tokens and updates UI off the hot path
            _flushTimer = new System.Windows.Forms.Timer { Interval = 75 };
            _flushTimer.Tick += FlushDisplayBuffer;
            _flushTimer.Start();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "chat")
                {
                    Content = jsonContent
                };
                _t_networkSend = Environment.TickCount64;
                using var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct
                );
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                txtStream.SelectionFont  = _mdDefault;
                txtStream.SelectionColor = SystemColors.WindowText;

                var readBuffer = new byte[4096];
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(
                            readBuffer, 0, readBuffer.Length, ct)) > 0)
                {
                    var chunk = Encoding.UTF8.GetString(readBuffer, 0, bytesRead);

                    // ── Intercept server telemetry packets (never shown in chat) ──
                    if (chunk.StartsWith("\x01TLM:"))
                    {
                        ParseServerTelemetry(chunk);
                        continue;
                    }
                    if (chunk.StartsWith("\x01END:"))
                    {
                        ParseServerEndTelemetry(chunk);
                        continue;
                    }

                    _streamBuffer.Append(chunk);
                    _tokenCount++;

                    if (!_receivedFirstToken)
                    {
                        _t_firstToken       = Environment.TickCount64;
                        _receivedFirstToken = true;
                    }

                    // Filter think blocks; accumulate stripped text for the flush timer
                    var visible = FilterThinkBlocks(chunk, ref _inThinkBlock);
                    if (visible.Length > 0)
                        _pendingDisplay.Append(StripMarkdownLive(visible));
                }
            }
            catch (OperationCanceledException)
            {
                const string cancelMsg = "\n[Research cancelled]";
                txtStream.SelectionFont  = _mdDefault;
                txtStream.SelectionColor = Color.DimGray;
                txtStream.AppendText(cancelMsg);
                txtStream.SelectionColor = SystemColors.WindowText;
                _streamBuffer.Append(cancelMsg);
            }
            catch (Exception ex)
            {
                txtStream.SelectionFont  = _mdDefault;
                txtStream.SelectionColor = Color.OrangeRed;
                txtStream.AppendText(Environment.NewLine + "[ERR] " + ex.Message);
                txtStream.SelectionColor = SystemColors.WindowText;
            }
            finally
            {
                _flushTimer?.Stop();
                _flushTimer?.Dispose();
                _flushTimer = null;
            }

            _t_finalToken = Environment.TickCount64;

            // Final flush of any buffered text before the full markdown render
            if (_pendingDisplay.Length > 0)
            {
                txtStream.AppendText(_pendingDisplay.ToString());
                _pendingDisplay.Clear();
            }

            // ── Full RTF render: restore preamble and re-render as formatted RTF ──
            _t_renderDone = 0;
            txtStream.Rtf = _preambleRtf;
            var finalText = StripThinkBlocks(_streamBuffer.ToString().TrimEnd());
            _lastCleanResponse = finalText;

            AppendMarkdown(txtStream, PreprocessMarkdown(finalText));
            txtStream.AppendText(Environment.NewLine + Environment.NewLine);
            _t_renderDone = Environment.TickCount64;

            // ── Telemetry footer ──────────────────────────────────────────────────
            AppendTelemetryFooter();

            ScrollToBottom();
            txtPrompt.Clear();

            if (_isResearching)
                StopSpinner();
            else
            {
                btnSend.Enabled = true;
                btnSend.Text    = "Send";
            }
        }

        // ── Spinner helpers ──────────────────────────────────────────────────

        private void StartSpinner()
        {
            _spinnerFrame  = 0;
            _spinnerTimer  = new System.Windows.Forms.Timer { Interval = 150 };
            _spinnerTimer.Tick += (_, _) =>
            {
                _spinnerFrame   = (_spinnerFrame + 1) % SpinnerFrames.Length;
                btnSend.Text    = SpinnerFrames[_spinnerFrame];
            };
            btnSend.Text    = SpinnerFrames[0];
            btnSend.Enabled = true; // clickable as a Cancel button
            _spinnerTimer.Start();
        }

        private void StopSpinner()
        {
            _spinnerTimer?.Stop();
            _spinnerTimer?.Dispose();
            _spinnerTimer  = null;
            _researchCts?.Dispose();
            _researchCts   = null;
            _isResearching = false;
            btnSend.Text    = "Send";
            btnSend.Enabled = true;
        }

        // ── 75 ms token-buffer flush ─────────────────────────────────────────────

        /// <summary>Timer tick: drains the pending display buffer into the RichTextBox.</summary>
        private void FlushDisplayBuffer(object? sender, EventArgs e)
        {
            if (_pendingDisplay.Length == 0) return;
            var text = _pendingDisplay.ToString();
            _pendingDisplay.Clear();
            txtStream.AppendText(text);
            ScrollToBottom();
        }

        // ── Server telemetry packet parsers ──────────────────────────────────────

        private void ParseServerTelemetry(string packet)
        {
            // packet: \x01TLM:{"context_ms":N,"ollama_first_token_ms":N}
            try
            {
                var json = packet.Substring(5).Trim(); // strip \x01TLM:
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("context_ms", out var c)) _serverContextMs = c.GetInt64();
                if (root.TryGetProperty("ollama_first_token_ms", out var o)) _serverOllamaFirstTokenMs = o.GetInt64();
            }
            catch { /* best-effort */ }
        }

        private void ParseServerEndTelemetry(string packet)
        {
            // packet: \x01END:{"total_tokens":N,"elapsed_ms":N,"tps":F}
            try
            {
                var json = packet.Substring(5).Trim(); // strip \x01END:
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("tps", out var t)) _serverTps = t.GetDouble();
            }
            catch { /* best-effort */ }
        }

        private void AppendTelemetryFooter()
        {
            long netSendMs        = _t_firstToken > 0 && _t_networkSend > 0 ? _t_firstToken - _t_networkSend : 0;
            long renderMs         = _t_renderDone > 0 && _t_finalToken > 0 ? _t_renderDone - _t_finalToken : 0;
            long totalElapsedMs   = _t_renderDone > 0 ? _t_renderDone - _t_requestStart : 0;

            var sb = new StringBuilder();
            sb.Append($"\u23f1 Request: {_t_requestStart - _t_requestStart}ms");   // anchor = 0
            if (netSendMs > 0)         sb.Append($"  |  Net\u2192first-token: {netSendMs}ms");
            if (_serverContextMs > 0)  sb.Append($"  |  IRIS context: {_serverContextMs}ms");
            if (_serverOllamaFirstTokenMs > 0) sb.Append($"  |  Ollama first-token: {_serverOllamaFirstTokenMs}ms");
            if (_serverTps > 0)        sb.Append($"  |  {_serverTps:F1} tok/s");
            if (renderMs > 0)          sb.Append($"  |  UI render: {renderMs}ms");
            if (totalElapsedMs > 0)    sb.Append($"  |  Total: {totalElapsedMs}ms");
            sb.Append($"  |  Tokens: {_tokenCount}");

            txtStream.SelectionFont  = new Font("Consolas", 8.5f, FontStyle.Regular);
            txtStream.SelectionColor = Color.FromArgb(80, 80, 80);
            txtStream.AppendText(Environment.NewLine + sb.ToString() + Environment.NewLine);
            txtStream.SelectionColor = SystemColors.WindowText;
            txtStream.SelectionFont  = _mdDefault;
        }

        /// <summary>Strip obvious markdown syntax characters for the live streaming preview.
        /// The final full-RTF render via AppendMarkdown replaces this when the stream ends.</summary>
        private static string StripMarkdownLive(string text)
        {
            // Remove heading markers at line start
            text = Regex.Replace(text, @"^#{1,6} ", "", RegexOptions.Multiline);
            // Collapse bold/italic asterisks (keep the text inside)
            text = Regex.Replace(text, @"\*{1,3}([^*\n]*)\*{1,3}", "$1");
            // Remove lone asterisks (list bullets become spaces)
            text = text.Replace("**", "").Replace("*", " ");
            // Remove backtick code markers
            text = text.Replace("`", "");
            return text;
        }

        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Calls /research/preview to decompose the intent, surfaces entity
        /// interpretation warnings, and asks the user to confirm before committing
        /// to a full Tavily run.
        /// Returns true to proceed, false to cancel.
        /// </summary>

        /// <summary>
        /// Shows a dialog asking the user to pick a project for research results.
        /// Updates <see cref="_currentProjectId"/> and the project ComboBox on success.
        /// Returns the chosen project ID, or null if the user cancelled.
        /// </summary>
        private string? PromptProjectSelection()
        {
            var projects = new List<ProjectItem>();
            for (int i = 1; i < cmbProject.Items.Count; i++)
                if (cmbProject.Items[i] is ProjectItem p) projects.Add(p);

            if (projects.Count == 0)
            {
                MessageBox.Show(
                    "No projects exist yet.\nCreate a project first so research results have somewhere to be promoted to.",
                    "No Projects", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

            using var dlg = new Form
            {
                Text            = "Select Project for Research",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition   = FormStartPosition.CenterParent,
                Width           = 400,
                Height          = 150,
                MaximizeBox     = false,
                MinimizeBox     = false,
            };

            var lbl    = new Label   { Text = "Research results will be promoted to:", Left = 12, Top = 14, AutoSize = true };
            var cmb    = new ComboBox
            {
                Left = 12, Top = 36, Width = 360,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            foreach (var p in projects) cmb.Items.Add(p);
            cmb.SelectedIndex = 0;

            var btnOk     = new Button { Text = "Start Research", DialogResult = DialogResult.OK,     Left = 198, Top = 76, Width = 120 };
            var btnCancel = new Button { Text = "Cancel",         DialogResult = DialogResult.Cancel, Left =  72, Top = 76, Width =  80 };
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;
            dlg.Controls.AddRange(new Control[] { lbl, cmb, btnOk, btnCancel });

            if (dlg.ShowDialog(this) != DialogResult.OK) return null;

            var selected = (ProjectItem)cmb.SelectedItem!;
            _currentProjectId = selected.Id;

            // Sync the main project ComboBox without triggering the session-clear handler.
            cmbProject.SelectedIndexChanged -= CmbProject_SelectedIndexChanged;
            for (int i = 1; i < cmbProject.Items.Count; i++)
            {
                if (cmbProject.Items[i] is ProjectItem p && p.Id == selected.Id)
                { cmbProject.SelectedIndex = i; break; }
            }
            cmbProject.SelectedIndexChanged += CmbProject_SelectedIndexChanged;

            return selected.Id;
        }

        private async Task<bool> ResearchPreviewAsync(string prompt, string model, string? projectId)
        {
            try
            {
                var body = JsonSerializer.Serialize(new { prompt, model, project_id = projectId });
                var resp = await _httpClient.PostAsync(
                    "research/preview",
                    new StringContent(body, Encoding.UTF8, "application/json")
                );
                resp.EnsureSuccessStatusCode();

                using var doc  = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root       = doc.RootElement;
                string topic   = root.TryGetProperty("topic",   out var tv) ? tv.GetString() ?? "" : "";
                var queriesEl  = root.TryGetProperty("queries", out var qv) && qv.ValueKind == JsonValueKind.Array
                                 ? qv : (JsonElement?)null;
                var warningsEl = root.TryGetProperty("warnings", out var wv) && wv.ValueKind == JsonValueKind.Array
                                 ? wv : (JsonElement?)null;

                var sb = new StringBuilder();
                sb.AppendLine($"Topic:  {topic}");
                sb.AppendLine();

                if (queriesEl.HasValue)
                {
                    sb.AppendLine("Generated search queries:");
                    foreach (var q in queriesEl.Value.EnumerateArray())
                        sb.AppendLine($"  • {q.GetString()}");
                }

                bool hasWarnings = false;
                if (warningsEl.HasValue)
                {
                    var list = new System.Collections.Generic.List<string>();
                    foreach (var w in warningsEl.Value.EnumerateArray())
                    {
                        string? s = w.GetString();
                        if (!string.IsNullOrEmpty(s)) list.Add(s);
                    }
                    if (list.Count > 0)
                    {
                        hasWarnings = true;
                        sb.AppendLine();
                        sb.AppendLine("INTERPRETATION WARNINGS:");
                        foreach (var w in list)
                            sb.AppendLine($"  {w}");
                        sb.AppendLine();
                        sb.AppendLine("Correct your intent and re-send if the interpretation is wrong.");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("Proceed with full research?");

                var result = MessageBox.Show(
                    sb.ToString(),
                    hasWarnings ? "Research Preview — Warnings" : "Research Preview",
                    MessageBoxButtons.YesNo,
                    hasWarnings ? MessageBoxIcon.Warning : MessageBoxIcon.Information
                );
                return result == DialogResult.Yes;
            }
            catch (Exception ex)
            {
                var result = MessageBox.Show(
                    $"Research preview failed: {ex.Message}\n\nProceed with research anyway?",
                    "Research Preview Error",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning
                );
                return result == DialogResult.Yes;
            }
        }

        private void CmbProject_SelectedIndexChanged(object sender, EventArgs e)
        {
            string newId = cmbProject.SelectedItem is ProjectItem proj ? proj.Id : "";
            if (newId == _currentProjectId) return;

            // Warn if there is any active conversation content
            if (txtStream.TextLength > 0)
            {
                var dlgResult = MessageBox.Show(
                    "Switching projects will start a new session.\n\n" +
                    "Unsaved conversation history will be lost.\n\nContinue?",
                    "Switch Project",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning
                );
                if (dlgResult == DialogResult.Cancel)
                {
                    // Revert combo selection without re-triggering this handler
                    cmbProject.SelectedIndexChanged -= CmbProject_SelectedIndexChanged;
                    try
                    {
                        if (string.IsNullOrEmpty(_currentProjectId))
                        {
                            cmbProject.SelectedIndex = 0;
                        }
                        else
                        {
                            for (int i = 0; i < cmbProject.Items.Count; i++)
                            {
                                if (cmbProject.Items[i] is ProjectItem p && p.Id == _currentProjectId)
                                { cmbProject.SelectedIndex = i; break; }
                            }
                        }
                    }
                    finally { cmbProject.SelectedIndexChanged += CmbProject_SelectedIndexChanged; }
                    return;
                }
            }

            _currentProjectId  = newId;
            _currentSessionId  = Guid.NewGuid().ToString();
            _streamBuffer.Clear();
            _lastCleanResponse = "";
            txtStream.Clear();

            txtStream.SelectionFont  = _mdDefault;
            txtStream.SelectionColor = Color.DimGray;
            string name = cmbProject.SelectedItem is ProjectItem pi ? pi.Name : "(no project)";
            txtStream.AppendText($"[Switched to project: {name} — new session started]"
                + Environment.NewLine + Environment.NewLine);
            txtStream.SelectionColor = SystemColors.WindowText;
        }

        private void BtnMemory_Click(object sender, EventArgs e)
        {
            var form = new MemoryAdminForm(_currentSessionId);
            form.ProjectsChanged += async (_, _) => await LoadProjectsAsync();
            form.FormClosed      += async (_, _) => await LoadProjectsAsync();
            form.Show(this);
        }

        private void TxtPrompt_TextChanged(object sender, EventArgs e)
        {
        }

        /// <summary>Normalize HTML fragments emitted by the model before markdown rendering.
        /// Converts &lt;br&gt; variants to newlines and decodes common HTML entities.</summary>
        /// <summary>
        /// Strips &lt;think&gt;…&lt;/think&gt; blocks from the completed buffer before
        /// markdown rendering so qwen3 chain-of-thought never leaks into the output.
        /// </summary>
        private static string StripThinkBlocks(string text) =>
            Regex.Replace(text, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase);

        /// <summary>
        /// Filters &lt;think&gt; blocks out of a single stream chunk for live display.
        /// State is preserved across chunks via <paramref name="inThink"/>.
        /// </summary>
        private static string FilterThinkBlocks(string chunk, ref bool inThink)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < chunk.Length)
            {
                if (!inThink)
                {
                    int start = chunk.IndexOf("<think>", i, StringComparison.OrdinalIgnoreCase);
                    if (start < 0) { sb.Append(chunk, i, chunk.Length - i); break; }
                    if (start > i) sb.Append(chunk, i, start - i);
                    inThink = true;
                    i = start + 7;
                }
                else
                {
                    int end = chunk.IndexOf("</think>", i, StringComparison.OrdinalIgnoreCase);
                    if (end < 0) break; // still inside — discard rest of chunk
                    inThink = false;
                    i = end + 8;
                }
            }
            return sb.ToString();
        }

        private static string PreprocessMarkdown(string text)
        {
            // <br>, <br/>, <br /> → newline
            text = Regex.Replace(text, @"<br\s*/?>" , "\n", RegexOptions.IgnoreCase);
            // Common HTML entities
            text = text
                .Replace("&amp;",  "&")
                .Replace("&lt;",   "<")
                .Replace("&gt;",   ">")
                .Replace("&nbsp;", " ")
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'");
            return text;
        }

        private void AppendMarkdown(RichTextBox rtb, string markdown)
        {
            bool inCodeBlock = false;
            var tableBuffer  = new List<string>();

            void FlushTable()
            {
                if (tableBuffer.Count == 0) return;
                RenderMarkdownTable(rtb, tableBuffer);
                tableBuffer.Clear();
            }

            foreach (var rawLine in markdown.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');

                // ── Table row detection (pipe-delimited, not inside code blocks) ──
                if (!inCodeBlock && line.TrimStart().StartsWith("|") && line.Count(c => c == '|') >= 2)
                {
                    tableBuffer.Add(line);
                    continue;
                }
                FlushTable(); // emit any buffered table before processing next line

                if (line.TrimStart().StartsWith("```"))
                {
                    inCodeBlock = !inCodeBlock;
                    rtb.SelectionFont = _mdCode;
                    rtb.SelectionColor = Color.DimGray;
                    rtb.AppendText(Environment.NewLine);
                    continue;
                }

                if (inCodeBlock)
                {
                    rtb.SelectionFont = _mdCode;
                    rtb.SelectionColor = Color.LightCyan;
                    rtb.AppendText(line + Environment.NewLine);
                    continue;
                }

                if (Regex.IsMatch(line.Trim(), @"^[-*_]{3,}$"))
                {
                    rtb.SelectionFont = _mdDefault;
                    rtb.SelectionColor = Color.DimGray;
                    rtb.AppendText("─────────────────────────────────────" + Environment.NewLine);
                    continue;
                }

                if (line.StartsWith("###### ")) { rtb.SelectionFont = _mdBold;   rtb.SelectionColor = Color.SlateBlue;       rtb.AppendText(line[7..] + Environment.NewLine); continue; }
                if (line.StartsWith("##### "))  { rtb.SelectionFont = _mdBold;   rtb.SelectionColor = Color.SlateBlue;       rtb.AppendText(line[6..] + Environment.NewLine); continue; }
                if (line.StartsWith("#### "))   { rtb.SelectionFont = _mdH3;    rtb.SelectionColor = Color.SlateBlue;       rtb.AppendText(line[5..] + Environment.NewLine); continue; }
                if (line.StartsWith("### "))    { rtb.SelectionFont = _mdH3;    rtb.SelectionColor = Color.MediumSlateBlue; rtb.AppendText(line[4..] + Environment.NewLine); continue; }
                if (line.StartsWith("## "))     { rtb.SelectionFont = _mdH2;    rtb.SelectionColor = Color.MediumSlateBlue; rtb.AppendText(line[3..] + Environment.NewLine); continue; }
                if (line.StartsWith("# "))      { rtb.SelectionFont = _mdH1;    rtb.SelectionColor = Color.MediumSlateBlue; rtb.AppendText(line[2..] + Environment.NewLine); continue; }

                if (line.StartsWith("> ") || line == ">")
                {
                    string bqText = line.Length > 2 ? line[2..] : "";
                    rtb.SelectionFont  = _mdItalic;
                    rtb.SelectionColor = Color.DarkGray;
                    rtb.AppendText("│ " + bqText + Environment.NewLine);
                    continue;
                }

                string prefix = "";
                string content = line;
                if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    prefix = "  \u2022 "; content = line[2..];
                }
                else if (line.Length > 2 && char.IsDigit(line[0]) && line[1] == '.' && line[2] == ' ')
                {
                    prefix = $"  {line[0]}. "; content = line[3..];
                }

                if (prefix.Length > 0)
                {
                    rtb.SelectionFont = _mdDefault;
                    rtb.SelectionColor = SystemColors.WindowText;
                    rtb.AppendText(prefix);
                }

                AppendInlineMarkdown(rtb, content);
                rtb.SelectionFont = _mdDefault;
                rtb.SelectionColor = SystemColors.WindowText;
                rtb.AppendText(Environment.NewLine);
            }

            FlushTable(); // flush any trailing table block
        }

        private void RenderMarkdownTable(RichTextBox rtb, List<string> lines)
        {
            // Parse rows, skip separator rows (|---|---|)
            var rows = new List<string[]>();
            foreach (var line in lines)
            {
                // Parse cells — handle rows with or without a trailing pipe
                var parts = line.Split('|');
                // Drop leading empty element (line starts with |)
                if (parts.Length > 0 && parts[0].Trim().Length == 0)
                    parts = parts.Skip(1).ToArray();
                // Drop trailing empty element (line ends with |)
                if (parts.Length > 0 && parts[^1].Trim().Length == 0)
                    parts = parts.SkipLast(1).ToArray();
                var cells = parts.Select(c => c.Trim()).ToArray();
                if (cells.Length == 0) continue;
                // Skip separator rows: |---|---|, |:---:|---|, etc.
                if (cells.All(c => Regex.IsMatch(c, @"^[-:= ]+$"))) continue;
                rows.Add(cells);
            }
            if (rows.Count == 0) return;

            int cols     = rows.Max(r => r.Length);
            var widths   = new int[cols];
            foreach (var row in rows)
                for (int i = 0; i < row.Length; i++)
                    widths[i] = Math.Max(widths[i], row[i].Length);

            for (int ri = 0; ri < rows.Count; ri++)
            {
                var row = rows[ri];
                var sb  = new StringBuilder("  ");
                for (int ci = 0; ci < cols; ci++)
                {
                    string cell = ci < row.Length ? row[ci] : "";
                    sb.Append(cell.PadRight(widths[ci]));
                    if (ci < cols - 1) sb.Append("  \u2502  ");
                }

                bool isHeader = (ri == 0);
                rtb.SelectionFont  = isHeader ? _mdBold : _mdDefault;
                rtb.SelectionColor = isHeader ? Color.MediumSlateBlue : SystemColors.WindowText;
                rtb.AppendText(sb.ToString() + Environment.NewLine);

                if (isHeader)
                {
                    var sep = new StringBuilder("  ");
                    for (int ci = 0; ci < cols; ci++)
                    {
                        sep.Append(new string('\u2500', widths[ci]));
                        if (ci < cols - 1) sep.Append("\u2500\u2500\u253c\u2500\u2500");
                    }
                    rtb.SelectionFont  = _mdDefault;
                    rtb.SelectionColor = Color.DimGray;
                    rtb.AppendText(sep.ToString() + Environment.NewLine);
                }
            }

            rtb.SelectionFont  = _mdDefault;
            rtb.SelectionColor = SystemColors.WindowText;
            rtb.AppendText(Environment.NewLine);
        }

        private void AppendInlineMarkdown(RichTextBox rtb, string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                // ── Markdown link [label](url) — emit label as a plain hyperlink
                if (text[i] == '[')
                {
                    int labelEnd = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                    if (labelEnd > i)
                    {
                        int urlEnd = text.IndexOf(')', labelEnd + 2);
                        if (urlEnd > labelEnd)
                        {
                            string label = text.Substring(i + 1, labelEnd - i - 1);
                            string url   = text.Substring(labelEnd + 2, urlEnd - labelEnd - 2);
                            // Append the label styled as a link; DetectUrls handles bare https://
                            // For [text](url) we emit "label (url)" so the RTB can auto-detect
                            rtb.SelectionFont  = _mdDefault;
                            rtb.SelectionColor = Color.CornflowerBlue;
                            rtb.AppendText(label.Length > 0 ? $"{label} ({url})" : url);
                            rtb.SelectionColor = SystemColors.WindowText;
                            i = urlEnd + 1;
                            continue;
                        }
                    }
                }
                if (i + 2 < text.Length && text[i] == '*' && text[i + 1] == '*' && text[i + 2] == '*')
                {
                    int end = text.IndexOf("***", i + 3, StringComparison.Ordinal);
                    if (end >= 0) { rtb.SelectionFont = _mdBoldItalic; rtb.SelectionColor = SystemColors.WindowText; rtb.AppendText(text.Substring(i + 3, end - i - 3)); i = end + 3; continue; }
                }
                if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
                {
                    int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end >= 0) { rtb.SelectionFont = _mdBold; rtb.SelectionColor = SystemColors.WindowText; rtb.AppendText(text.Substring(i + 2, end - i - 2)); i = end + 2; continue; }
                }
                if (text[i] == '*')
                {
                    int end = text.IndexOf('*', i + 1);
                    if (end >= 0) { rtb.SelectionFont = _mdItalic; rtb.SelectionColor = SystemColors.WindowText; rtb.AppendText(text.Substring(i + 1, end - i - 1)); i = end + 1; continue; }
                }
                if (text[i] == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end >= 0) { rtb.SelectionFont = _mdCode; rtb.SelectionColor = Color.LightSeaGreen; rtb.AppendText(text.Substring(i + 1, end - i - 1)); i = end + 1; continue; }
                }
                int next = FindNextSpecial(text, i + 1);
                rtb.SelectionFont = _mdDefault;
                rtb.SelectionColor = SystemColors.WindowText;
                rtb.AppendText(text.Substring(i, next - i));
                i = next;
            }
        }

        private static int FindNextSpecial(string text, int start)
        {
            for (int i = start; i < text.Length; i++)
                if (text[i] == '*' || text[i] == '`' || text[i] == '[') return i;
            return text.Length;
        }

        // ── Export strip ─────────────────────────────────────────────────────────

        private void SetupExportStrip()
        {
            // Add export buttons as a third row inside pnlControls so they
            // never overlap the response area. Designer already sets Height=96.
            Button MkBtn(string text, int x, int w = 90) => new Button
            {
                Text      = text,
                Location  = new Point(x, 66),   // third row inside the panel
                Size      = new Size(w, 24),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 8.5f),
                Cursor    = Cursors.Hand,
            };

            var btnCopy = MkBtn("📋 Copy",       4,   86);
            var btnTxt  = MkBtn("💾 Save .txt",  94,  90);
            var btnRtf  = MkBtn("📄 Save .rtf", 188,  90);
            var btnCsv  = MkBtn("⊞ Export .csv",282, 100);

            btnCopy.Click += (_, _) => ExportCopy();
            btnTxt.Click  += (_, _) => ExportSaveTxt();
            btnRtf.Click  += (_, _) => ExportSaveRtf();
            btnCsv.Click  += (_, _) => ExportSaveCsv();

            pnlControls.Controls.AddRange(new Control[] { btnCopy, btnTxt, btnRtf, btnCsv });
        }

        private void ExportCopy()
        {
            if (string.IsNullOrEmpty(_lastCleanResponse)) return;
            Clipboard.SetText(_lastCleanResponse, TextDataFormat.UnicodeText);
        }

        private void ExportSaveTxt()
        {
            if (string.IsNullOrEmpty(_lastCleanResponse)) return;
            using var dlg = new SaveFileDialog
            {
                Title      = "Save response as plain text",
                Filter     = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = "txt",
                FileName   = $"IRIS_{DateTime.Now:yyyyMMdd_HHmm}",
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            File.WriteAllText(dlg.FileName, _lastCleanResponse, Encoding.UTF8);
            OpenExportedFile(dlg.FileName);
        }

        private void ExportSaveRtf()
        {
            using var dlg = new SaveFileDialog
            {
                Title      = "Save session as RTF (Word-compatible)",
                Filter     = "Rich Text Format (*.rtf)|*.rtf|All files (*.*)|*.*",
                DefaultExt = "rtf",
                FileName   = $"IRIS_{DateTime.Now:yyyyMMdd_HHmm}",
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            txtStream.SaveFile(dlg.FileName, RichTextBoxStreamType.RichText);
            OpenExportedFile(dlg.FileName);
        }

        /// <summary>
        /// CSV export: if the last response contains a markdown table, each row
        /// becomes a CSV record. Otherwise the full text is exported as a single cell.
        /// External callers (MemoryAdmin, Research Trace) can pass rows directly via
        /// <see cref="ExportService.SaveCsv"/>.
        /// </summary>
        private void ExportSaveCsv()
        {
            var rows = ExtractTableRows(_lastCleanResponse);
            if (rows == null)
            {
                if (string.IsNullOrEmpty(_lastCleanResponse)) return;
                rows = new List<string[]> { new[] { _lastCleanResponse } };
            }

            using var dlg = new SaveFileDialog
            {
                Title      = "Export data as CSV",
                Filter     = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                DefaultExt = "csv",
                FileName   = $"IRIS_export_{DateTime.Now:yyyyMMdd_HHmm}",
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            using var sw = new StreamWriter(dlg.FileName, false, Encoding.UTF8);
            foreach (var row in rows)
                sw.WriteLine(string.Join(",", row.Select(c => $"\"{c.Replace("\"", "\"\"")}\" ")));
            OpenExportedFile(dlg.FileName);
        }

        /// <summary>
        /// Extracts all markdown table rows from clean text, skipping separator rows.
        /// Returns null if no table is found. Intended for future reuse by Research
        /// Trace and Memory Admin export buttons.
        /// </summary>
        internal static List<string[]>? ExtractTableRows(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var tableLines = text.Split('\n')
                                 .Where(l => l.TrimStart().StartsWith("|") && l.Count(c => c == '|') >= 2)
                                 .ToList();
            if (tableLines.Count == 0) return null;

            var rows = new List<string[]>();
            foreach (var line in tableLines)
            {
                var cells = line.Split('|').Skip(1).SkipLast(1)
                                .Select(c => c.Trim()).ToArray();
                if (cells.All(c => Regex.IsMatch(c, @"^[-: ]+$"))) continue; // separator
                rows.Add(cells);
            }
            return rows.Count > 0 ? rows : null;
        }

        private static void OpenExportedFile(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { /* best-effort; silently skip if no handler registered */ }
        }

        // ── Session reset ────────────────────────────────────────────────────────

        private void BtnNewSession_Click(object sender, EventArgs e)
        {
            _currentSessionId = Guid.NewGuid().ToString();
            _streamBuffer.Clear();
            _lastCleanResponse = "";
            txtStream.Clear();
            txtStream.SelectionFont = _mdDefault;
            txtStream.SelectionColor = Color.DimGray;
            txtStream.AppendText("[New session started]" + Environment.NewLine + Environment.NewLine);
            txtStream.SelectionColor = SystemColors.WindowText;
        }

        private void label3_Click(object sender, EventArgs e)
        {

        }
    }

    /// <summary>Holds a project id + display name for cmbProject items.</summary>
    internal sealed class ProjectItem
    {
        public string Id   { get; }
        public string Name { get; }
        public ProjectItem(string id, string name) { Id = id; Name = name; }
        public override string ToString() => Name;
    }
}