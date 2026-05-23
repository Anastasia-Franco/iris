namespace WindowsFormsApp
{
    public partial class IRIS_UI : Form
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (this.components != null))
            {
                this.components.Dispose();
            }
            if (disposing)
            {
                _mdDefault?.Dispose();
                _mdBold?.Dispose();
                _mdItalic?.Dispose();
                _mdBoldItalic?.Dispose();
                _mdCode?.Dispose();
                _mdH1?.Dispose();
                _mdH2?.Dispose();
                _mdH3?.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            txtPrompt = new TextBox();
            btnSend = new Button();
            btnNewSession = new Button();
            txtStream = new RichTextBox();
            cmbModel = new ComboBox();
            pnlControls = new Panel();
            label2 = new Label();
            cmbTask = new ComboBox();
            label1 = new Label();
            label3 = new Label();
            pnlStreamArea = new Panel();
            pnlControls.SuspendLayout();
            SuspendLayout();
            // 
            // txtPrompt
            // 
            txtPrompt.Dock = DockStyle.Bottom;
            txtPrompt.Font = new Font("Segoe UI Emoji", 12F, FontStyle.Regular, GraphicsUnit.Point, 0);
            txtPrompt.Location = new Point(0, 349);
            txtPrompt.Margin = new Padding(4, 3, 4, 3);
            txtPrompt.Multiline = true;
            txtPrompt.Name = "txtPrompt";
            txtPrompt.Size = new Size(933, 100);
            txtPrompt.TabIndex = 0;
            txtPrompt.TextChanged += TxtPrompt_TextChanged;
            // 
            // btnNewSession
            // 
            btnNewSession.Location = new Point(10, 26);
            btnNewSession.Name = "btnNewSession";
            btnNewSession.Size = new Size(110, 26);
            btnNewSession.TabIndex = 8;
            btnNewSession.Text = "New Session";
            btnNewSession.UseVisualStyleBackColor = true;
            btnNewSession.Click += BtnNewSession_Click;
            // 
            // btnSend
            // 
            btnSend.BackColor = Color.FromArgb(128, 255, 128);
            btnSend.ForeColor = SystemColors.Desktop;
            btnSend.Location = new Point(820, 26);
            btnSend.Margin = new Padding(4, 3, 4, 3);
            btnSend.Name = "btnSend";
            btnSend.Size = new Size(100, 26);
            btnSend.TabIndex = 1;
            btnSend.Text = "Send";
            btnSend.UseVisualStyleBackColor = false;
            btnSend.Click += BtnSend_Click;
            // 
            // txtStream
            // 
            txtStream.Dock = DockStyle.Fill;
            txtStream.DetectUrls = false;
            txtStream.Font = new Font("Segoe UI Emoji", 12F, FontStyle.Regular, GraphicsUnit.Point, 0);
            txtStream.Location = new Point(0, 55);
            txtStream.Margin = new Padding(4, 3, 4, 3);
            txtStream.Name = "txtStream";
            txtStream.ReadOnly = true;
            txtStream.ScrollBars = RichTextBoxScrollBars.Vertical;
            txtStream.Size = new Size(933, 294);
            txtStream.TabIndex = 2;
            // 
            // cmbModel
            // 
            cmbModel.FormattingEnabled = true;
            cmbModel.Items.AddRange(new object[] { "llama3.3:latest", "qwen3:30b", "qwen3-coder-next:latest", "deepseek-coder-v2:lite", "qwen2.5-coder:1.5b", "deepseek-r1:32b" });
            cmbModel.Location = new Point(644, 29);
            cmbModel.Name = "cmbModel";
            cmbModel.Size = new Size(169, 23);
            cmbModel.TabIndex = 3;
            // 
            // pnlControls
            // 
            pnlControls.Controls.Add(label2);
            pnlControls.Controls.Add(cmbTask);
            pnlControls.Controls.Add(label1);
            pnlControls.Controls.Add(cmbModel);
            pnlControls.Controls.Add(btnSend);
            pnlControls.Controls.Add(btnNewSession);
            pnlControls.Dock = DockStyle.Bottom;
            pnlControls.Location = new Point(0, 449);
            pnlControls.Name = "pnlControls";
            pnlControls.Size = new Size(933, 70);
            pnlControls.TabIndex = 4;
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Font = new Font("Segoe UI", 12F, FontStyle.Bold, GraphicsUnit.Point, 0);
            label2.Location = new Point(700, 5);
            label2.Name = "label2";
            label2.Size = new Size(59, 21);
            label2.TabIndex = 6;
            label2.Text = "Model";
            // 
            // cmbTask
            // 
            cmbTask.FormattingEnabled = true;
            cmbTask.Items.AddRange(new object[] { "Chat", "Research", "Code", "Creative", "System" });
            cmbTask.Location = new Point(529, 29);
            cmbTask.Name = "cmbTask";
            cmbTask.Size = new Size(109, 23);
            cmbTask.TabIndex = 4;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Font = new Font("Segoe UI", 12F, FontStyle.Bold, GraphicsUnit.Point, 0);
            label1.Location = new Point(556, 5);
            label1.Name = "label1";
            label1.Size = new Size(43, 21);
            label1.TabIndex = 5;
            label1.Text = "Task";
            // 
            // label3
            // 
            label3.Dock = DockStyle.Top;
            label3.Font = new Font("Yu Gothic UI", 20.25F, FontStyle.Bold, GraphicsUnit.Point, 0);
            label3.ForeColor = Color.MediumSlateBlue;
            label3.Location = new Point(0, 0);
            label3.Name = "label3";
            label3.Size = new Size(933, 55);
            label3.TabIndex = 5;
            label3.Text = "lndependent Resilient Intelligence System";
            label3.TextAlign = ContentAlignment.TopCenter;
            label3.Click += label3_Click;
            // 
            // pnlStreamArea
            // 
            pnlStreamArea.Dock = DockStyle.Fill;
            pnlStreamArea.Location = new Point(0, 0);
            pnlStreamArea.Name = "pnlStreamArea";
            pnlStreamArea.Padding = new Padding(20, 10, 20, 10);
            pnlStreamArea.Size = new Size(933, 519);
            pnlStreamArea.TabIndex = 6;
            // 
            // IRIS_UI
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(933, 519);
            Controls.Add(txtStream);
            Controls.Add(label3);
            Controls.Add(txtPrompt);
            Controls.Add(pnlControls);
            Controls.Add(pnlStreamArea);
            Margin = new Padding(4, 3, 4, 3);
            Name = "IRIS_UI";
            Text = "IRIS_UI";
            pnlControls.ResumeLayout(false);
            pnlControls.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private System.Windows.Forms.Button btnNewSession;
        private System.Windows.Forms.RichTextBox txtStream;
        private System.Windows.Forms.TextBox txtPrompt;
        private System.Windows.Forms.Button btnSend;
        private ComboBox cmbModel;
        private Panel pnlControls;
        private Label label2;
        private ComboBox cmbTask;
        private Label label1;
        private Label label3;
        private Panel pnlStreamArea;
    }
}