using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Fortodesk
{
    static class Program
    {
        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static void Main()
        {
            // ===== 单实例检测 =====
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, "Global\\FortodeskControllerMutex_UniqueKey", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("程序已经在运行中，不能重复启动！", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            } // using结束时自动释放mutex
        }
    }
}
