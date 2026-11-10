using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ZedZoneSaveEditor
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string selftestReport = null;
            bool selftest = false;
            var extra = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--root":
                        if (i + 1 < args.Length) GameFiles.DataRoot = args[++i];
                        break;
                    case "--selftest":
                        selftest = true;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) selftestReport = args[++i];
                        break;
                    default:
                        if (selftest) extra.Add(args[i]);
                        break;
                }
            }

            if (selftest) return SelfTest.Run(selftestReport, extra);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            int ui = Array.IndexOf(args, "--uitest");
            if (ui >= 0 && ui + 1 < args.Length) return new MainForm().UiTest(args[ui + 1]);
            int snapAdd = Array.IndexOf(args, "--snapshot-add");
            if (snapAdd >= 0 && snapAdd + 2 < args.Length)
            {
                AddItemDialog.Snapshot(args[snapAdd + 1], args[snapAdd + 2]);
                return 0;
            }
            int snap = Array.IndexOf(args, "--snapshot");
            if (snap >= 0 && snap + 1 < args.Length)
            {
                int tab = Array.IndexOf(args, "--multi") >= 0 ? 1 : 0;
                int eqd = Array.IndexOf(args, "--equipdlg");
                int right = Array.IndexOf(args, "--equip") >= 0 ? 2 : Array.IndexOf(args, "--state") >= 0 ? 3 : Array.IndexOf(args, "--inv") >= 0 ? 1 : 0;
                new MainForm().Snapshot(args[snap + 1], tab, Array.IndexOf(args, "--heal") >= 0, right,
                    eqd >= 0 && eqd + 1 < args.Length ? args[eqd + 1] : null);
                return 0;
            }
            Application.ThreadException += (s, e) =>
                MessageBox.Show("程序出错了：\n" + e.Exception.Message, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
