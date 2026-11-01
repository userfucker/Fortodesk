using System.Drawing;
using System.Windows.Forms;

namespace Fortodesk
{
    public partial class RichTextBoxPlus : UserControl
    {
        public RichTextBoxPlus()
        {
            InitializeComponent();
            // ★ 创建右键菜单并绑定到内部richTextBox
            var menu = new System.Windows.Forms.ContextMenuStrip();

            var copyItem = new System.Windows.Forms.ToolStripMenuItem("复制");
            var selectAllItem = new System.Windows.Forms.ToolStripMenuItem("全选");
            var clearItem = new System.Windows.Forms.ToolStripMenuItem("清空");

            copyItem.Click += (s, e) => richTextBox.Copy();
            selectAllItem.Click += (s, e) => richTextBox.SelectAll();
            clearItem.Click += (s, e) => richTextBox.Clear();

            menu.Items.AddRange(new System.Windows.Forms.ToolStripItem[]
            {
        copyItem,
        selectAllItem,
        new System.Windows.Forms.ToolStripSeparator(),
        clearItem
            });

            richTextBox.ContextMenuStrip = menu;
        }

        public bool ReadOnly
        {
            get => richTextBox.ReadOnly;
            set => richTextBox.ReadOnly = value;
        }

        public new Font Font
        {
            get => richTextBox.Font;
            set => richTextBox.Font = value;
        }

        public new Color BackColor
        {
            get => richTextBox.BackColor;
            set => richTextBox.BackColor = value;
        }

        public new Color ForeColor
        {
            get => richTextBox.ForeColor;
            set => richTextBox.ForeColor = value;
        }

        public Color BorderColor
        {
            get => panelBorder.BackColor;
            set => panelBorder.BackColor = value;
        }

        public void AppendText(string text)
        {
            richTextBox.AppendText(text);
            richTextBox.ScrollToCaret();
        }

        public void AppendText(string text, Color color)
        {
            richTextBox.SelectionStart = richTextBox.TextLength;
            richTextBox.SelectionLength = 0;
            richTextBox.SelectionColor = color;
            richTextBox.SelectionFont = new Font(richTextBox.Font, FontStyle.Bold); // ★ 加粗
            richTextBox.AppendText(text);
            richTextBox.SelectionFont = richTextBox.Font; // 恢复正常
            richTextBox.SelectionColor = richTextBox.ForeColor;
            richTextBox.ScrollToCaret();
        }

        public void Clear()
        {
            richTextBox.Clear();
        }

        public new string Text
        {
            get => richTextBox.Text;
            set => richTextBox.Text = value;
        }

        public int TextLength => richTextBox.TextLength;
    }
}