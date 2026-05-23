using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Drawing;
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
        private StringBuilder _streamBuffer = new();
        private string _preambleRtf = "";

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
            cmbModel.SelectedIndex = 0;
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
        }

        private async void BtnSend_Click(object sender, EventArgs e)
        {
            string prompt = txtPrompt.Text.Trim();

            if (string.IsNullOrWhiteSpace(prompt))
                return;

            // Styled user message
            txtStream.SelectionFont = _mdBold;
            txtStream.SelectionColor = Color.SteelBlue;
            txtStream.AppendText("You: ");
            txtStream.SelectionFont = _mdDefault;
            txtStream.SelectionColor = SystemColors.WindowText;
            txtStream.AppendText(prompt + Environment.NewLine + Environment.NewLine);

            // IRIS label — mark where the response body begins
            txtStream.SelectionFont = _mdBold;
            txtStream.SelectionColor = Color.MediumSlateBlue;
            txtStream.AppendText("IRIS: ");
            txtStream.SelectionFont = _mdDefault;
            txtStream.SelectionColor = SystemColors.WindowText;
            // Snapshot RTF here — raw stream tokens will be written during streaming,
            // then this snapshot is restored and replaced with clean markdown.
            _preambleRtf = txtStream.Rtf;
            _streamBuffer.Clear();

            btnSend.Enabled = false;
            string selectedModel = cmbModel.SelectedItem?.ToString() ?? "qwen3:30b";
            string selectedTask = cmbTask.SelectedItem?.ToString() ?? "Chat";
            string selectedMode = selectedTask switch
            {
                "Research" => "research",
                _ => "prompt"
            };
            var requestBody = new
            {
                session_id = _currentSessionId,
                prompt,
                model = selectedModel,
                mode = selectedMode
            };
            var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "chat")
                {
                    Content = jsonContent
                };
                using var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead
                );
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                var readBuffer = new byte[512];
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(readBuffer)) > 0)
                {
                    var chunk = Encoding.UTF8.GetString(readBuffer, 0, bytesRead);
                    txtStream.AppendText(chunk);
                    _streamBuffer.Append(chunk);
                }
            }
            catch (Exception ex)
            {
                txtStream.SelectionFont = _mdDefault;
                txtStream.SelectionColor = Color.OrangeRed;
                txtStream.AppendText(Environment.NewLine + "[ERR] " + ex.Message);
                txtStream.SelectionColor = SystemColors.WindowText;
            }

            // Restore the pre-stream RTF snapshot and render the full response as markdown.
            // This is more reliable than Select+SelectedText across async continuations.
            txtStream.Rtf = _preambleRtf;
            AppendMarkdown(txtStream, _streamBuffer.ToString().TrimEnd());
            txtStream.AppendText(Environment.NewLine + Environment.NewLine);
            txtStream.SelectionStart = txtStream.TextLength;
            txtStream.ScrollToCaret();

            txtPrompt.Clear();
            btnSend.Enabled = true;
        }

        private void TxtPrompt_TextChanged(object sender, EventArgs e)
        {
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
}