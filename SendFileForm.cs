using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Fortodesk
{
    public partial class SendFileForm : Form
    {
        private static readonly string[] PathLabels = {
            "当前用户桌面", "当前用户主目录", "当前用户下载", "当前用户文档",
            "公共桌面", "公共用户目录", "公共下载", "公共文档",
            "当前用户开机自启动", "Windows系统目录",
        };

        private static readonly string[] PathIds = {
            "USER_DESKTOP", "USER_HOME", "USER_DOWNLOADS", "USER_DOCUMENTS",
            "PUBLIC_DESKTOP", "PUBLIC_HOME", "PUBLIC_DOWNLOADS", "PUBLIC_DOCUMENTS",
            "USER_STARTUP", "WINDOWS_DIR",
        };

        private readonly List<MainForm.HostInfo> _hosts;

        public SendFileForm(List<MainForm.HostInfo> hosts)
        {
            InitializeComponent();

            _hosts = hosts;

            comboBox1.Items.Clear();
            comboBox1.Items.Add("-- 手动输入路径 --");
            foreach (var label in PathLabels)
                comboBox1.Items.Add(label);

            comboBox1.SelectedIndex = 0;
            comboBox1.SelectedIndexChanged += ComboBox1_SelectedIndexChanged;

            // ===== 新增：支持拖拽文件到 textBox1 =====
            textBox1.AllowDrop = true;
            textBox1.DragEnter += TextBox1_DragEnter;
            textBox1.DragDrop += TextBox1_DragDrop;
        }

        private void ComboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            int idx = comboBox1.SelectedIndex;

            if (idx <= 0)
            {
                textBox2.ReadOnly = false;
                return;
            }

            textBox2.Text = PathIds[idx - 1];
            textBox2.ReadOnly = true;
        }

        // button1:开始发送
        private void button1_Click(object sender, EventArgs e)
        {
            string localPath = textBox1.Text.Trim();
            string remoteSavePath = textBox2.Text.Trim();

            if (string.IsNullOrEmpty(localPath))
            {
                MessageBox.Show("请选择或输入本地文件路径", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!File.Exists(localPath))
            {
                MessageBox.Show("本地文件不存在: " + localPath, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (string.IsNullOrEmpty(remoteSavePath))
            {
                MessageBox.Show("请选择或输入被控端保存路径", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string fileName = Path.GetFileName(localPath);

            foreach (var host in _hosts)
            {
                if (host.Client == null || !host.Client.Connected) continue;

                MainForm.HostInfo capturedHost = host;
                string capturedPath = localPath;
                string capturedSavePath = remoteSavePath;

                Thread t = new Thread(() =>
                    MainForm.mainform.SendFileToBgentToHost(
                        capturedPath,
                        capturedHost,
                        capturedSavePath));

                t.IsBackground = true;
                t.Start();
            }

            this.Close();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要发送的文件";
                dlg.Filter = "所有文件|*.*";

                if (dlg.ShowDialog() == DialogResult.OK)
                    textBox1.Text = dlg.FileName;
            }
        }

        // ================= 新增拖拽功能 =================

        private void TextBox1_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        private void TextBox1_DragDrop(object sender, DragEventArgs e)
        {
            try
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

                if (files != null && files.Length > 0)
                {
                    textBox1.Text = files[0];
                }
            }
            catch
            {
                MessageBox.Show("拖拽文件解析失败", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}