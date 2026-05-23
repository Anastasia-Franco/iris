using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Drawing;
using System.Text.RegularExpressions;
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

        private string _currentSessionId = Guid.NewGuid().ToString();
        private string _currentProjectId = "";
        private StringBuilder _streamBuffer = new();
        private string _preambleRtf = "";

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

            // ── Research pre-flight: decompose + confirm before drawing anything ──
            if (selectedMode == "research")
            {
                btnSend.Enabled = false;
                bool proceed = await ResearchPreviewAsync(prompt, selectedModel, projectId);
                if (!proceed)
                {
                    btnSend.Enabled = true;
                    return;
                }

                // User confirmed — arm cancel token and start animated spinner
                // before any network activity so the UI reacts instantly.
                _researchCts   = new CancellationTokenSource();
                _isResearching = true;
                StartSpinner(); // enables button as Cancel and begins animation
            }
            else
            {
                btnSend.Enabled = false;
            }

            // Styled user message
            txtStream.SelectionFont  = _mdBold;
            txtStream.SelectionColor = Color.SteelBlue;
            txtStream.AppendText("You: ");
            txtStream.SelectionFont  = _mdDefault;
            txtStream.SelectionColor = SystemColors.WindowText;
            txtStream.AppendText(prompt + Environment.NewLine + Environment.NewLine);

            // IRIS label — mark where the response body begins
            txtStream.SelectionFont  = _mdBold;
            txtStream.SelectionColor = Color.MediumSlateBlue;
            txtStream.AppendText("IRIS: ");
            txtStream.SelectionFont  = _mdDefault;
            txtStream.SelectionColor = SystemColors.WindowText;
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
                txtStream.SelectionStart = txtStream.TextLength;
                txtStream.ScrollToCaret();
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

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "chat")
                {
                    Content = jsonContent
                };
                using var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct
                );
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                var readBuffer = new byte[512];
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(
                            readBuffer, 0, readBuffer.Length, ct)) > 0)
                {
                    var chunk = Encoding.UTF8.GetString(readBuffer, 0, bytesRead);
                    txtStream.AppendText(chunk);
                    _streamBuffer.Append(chunk);
                    // Auto-scroll after every chunk so progress lines are always visible
                    txtStream.SelectionStart = txtStream.TextLength;
                    txtStream.ScrollToCaret();
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

            // Restore pre-stream RTF snapshot and render the full response as markdown.
            txtStream.Rtf = _preambleRtf;
            AppendMarkdown(txtStream, PreprocessMarkdown(_streamBuffer.ToString().TrimEnd()));
            txtStream.AppendText(Environment.NewLine + Environment.NewLine);
            txtStream.SelectionStart = txtStream.TextLength;
            txtStream.ScrollToCaret();

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

        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Calls /research/preview to decompose the intent, surfaces entity
        /// interpretation warnings, and asks the user to confirm before committing
        /// to a full Tavily run.
        /// Returns true to proceed, false to cancel.
        /// </summary>
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
            if (cmbProject.SelectedItem is ProjectItem proj)
                _currentProjectId = proj.Id;
            else
                _currentProjectId = "";
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
            foreach (var rawLine in markdown.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');

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

                if (line == "---" || line == "***" || line == "___")
                {
                    rtb.SelectionFont = _mdDefault;
                    rtb.SelectionColor = Color.DimGray;
                    rtb.AppendText("─────────────────────────────────────" + Environment.NewLine);
                    continue;
                }

                if (line.StartsWith("### ")) { rtb.SelectionFont = _mdH3; rtb.SelectionColor = Color.MediumSlateBlue; rtb.AppendText(line[4..] + Environment.NewLine); continue; }
                if (line.StartsWith("## "))  { rtb.SelectionFont = _mdH2; rtb.SelectionColor = Color.MediumSlateBlue; rtb.AppendText(line[3..] + Environment.NewLine); continue; }
                if (line.StartsWith("# "))   { rtb.SelectionFont = _mdH1; rtb.SelectionColor = Color.MediumSlateBlue; rtb.AppendText(line[2..] + Environment.NewLine); continue; }

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
        }

        private void AppendInlineMarkdown(RichTextBox rtb, string text)
        {
            int i = 0;
            while (i < text.Length)
            {
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
                if (text[i] == '*' || text[i] == '`') return i;
            return text.Length;
        }

        private void BtnNewSession_Click(object sender, EventArgs e)
        {
            _currentSessionId = Guid.NewGuid().ToString();
            _streamBuffer.Clear();
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