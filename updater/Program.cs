using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FarmProgressUpdater
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "Local\\FarmProgressSetup", out created))
            {
                if (!created) { MessageBox.Show("Farm Progress 更新程序已经打开。"); return; }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new OneClickForm());
            }
        }
    }
}
