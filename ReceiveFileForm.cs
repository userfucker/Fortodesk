using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Fortodesk
{
    public partial class ReceiveFileForm : Form
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

        private readonly MainForm.HostInfo _host;

        public ReceiveFileForm(MainForm.HostInfo host)
        {
            InitializeComponent();
            _host = host;
            this.Text = $"接收文件 - {host.HostName}";

            comboBox1.Items.Clear();
            comboBox1.Items.Add("-- 手动输入路径 --");
            foreach (var label in PathLabels)
                comboBox1.Items.Add(label);
            comboBox1.SelectedIndex = 0;
            comboBox1.SelectedIndexChanged += ComboBox1_SelectedIndexChanged;
        }

        private void ComboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            int idx = comboBox1.SelectedIndex;
            if (idx <= 0)
            {
                textBox2.ReadOnly = false;
                return;
            }
            // 这里是控制端本地路径，直接用系统路径
            string[] localPaths = {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            };
            textBox2.Text = localPaths[idx - 1];
            textBox2.ReadOnly = true;
        }

        // button1：开始接收
        private void button1_Click(object sender, EventArgs e)
        {
            string remotePath = textBox1.Text.Trim();
            string localSaveDir = textBox2.Text.Trim();

            if (string.IsNullOrEmpty(remotePath))
            {
                MessageBox.Show("请输入被控端文件路径", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrEmpty(localSaveDir))
            {
                MessageBox.Show("请选择或输入本地保存路径", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!Directory.Exists(localSaveDir))
            {
                try { Directory.CreateDirectory(localSaveDir); }
                catch
                {
                    MessageBox.Show("本地保存路径无效: " + localSaveDir, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            string fname = Path.GetFileName(remotePath);
            string localSavePath = Path.Combine(localSaveDir, fname);
            string partPath = localSavePath + ".part";
            long resumeOffset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

            if (resumeOffset > 0)
                MainForm.mainform.AppendLog(
                    $"[{_host.HostName}] [文件请求] 续传: {fname}  已有: {resumeOffset / 1024} KB");
            else
                MainForm.mainform.AppendLog(
                    $"[{_host.HostName}] [文件请求] 请求: {remotePath} → {localSaveDir}");

            // ★ 生成TaskID，格式：文件路径|TaskID
            string taskId = Guid.NewGuid().ToString("N").Substring(0, 16);
            string payload = remotePath + "|" + taskId;
            MainForm.mainform.SendCmdToHost(_host, 0x13 /* CMD_REQUEST_FILE */,
                Encoding.UTF8.GetBytes(payload));

            // ★ 起独立线程用TaskID去9001接文件，保存到指定目录
            string capturedSaveDir = localSaveDir;
            string capturedTaskId = taskId;
            string capturedUniqueId = _host.UniqueId;
            Thread t = new Thread(() =>
                MainForm.mainform.WaitAndRecvFileToDirWithTaskId(capturedUniqueId, capturedTaskId, capturedSaveDir));
            t.IsBackground = true;
            t.Start();
            this.Close();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择文件保存位置";
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    textBox2.Text = dlg.SelectedPath;
                    // 切换为手动输入模式
                    comboBox1.SelectedIndex = 0;
                    textBox2.ReadOnly = false;
                }
            }
        }
    }
}