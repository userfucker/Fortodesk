namespace Fortodesk
{
    partial class RichTextBoxPlus
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.panelBorder = new System.Windows.Forms.Panel();
            this.richTextBox = new System.Windows.Forms.RichTextBox();
            this.panelBorder.SuspendLayout();
            this.SuspendLayout();

            this.panelBorder.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.panelBorder.Dock = System.Windows.Forms.DockStyle.Fill;
            this.panelBorder.Padding = new System.Windows.Forms.Padding(1);
            this.panelBorder.Controls.Add(this.richTextBox);

            this.richTextBox.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.richTextBox.Dock = System.Windows.Forms.DockStyle.Fill;
            this.richTextBox.ScrollBars = System.Windows.Forms.RichTextBoxScrollBars.Vertical;
            this.richTextBox.Font = new System.Drawing.Font("Consolas", 12F);
            this.richTextBox.ReadOnly = true;
            this.richTextBox.BackColor = System.Drawing.Color.White;

            this.Controls.Add(this.panelBorder);
            this.Size = new System.Drawing.Size(400, 300);

            this.panelBorder.ResumeLayout(false);
            this.ResumeLayout(false);
        }

        private System.Windows.Forms.Panel panelBorder;
        private System.Windows.Forms.RichTextBox richTextBox;
    }
}