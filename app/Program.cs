using System;
using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Roblox Product Manager")]
[assembly: AssemblyProduct("Roblox Product Manager")]
[assembly: AssemblyDescription("Create and edit Roblox game passes, developer products and badges.")]
[assembly: AssemblyVersion("2.2.0.0")]
[assembly: AssemblyFileVersion("2.2.0.0")]
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace RobloxProductManager
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // .NET Framework 4.8 uses the OS defaults (TLS 1.2+). Only force TLS 1.2 if an older default is in place.
            if ((int)ServicePointManager.SecurityProtocol != 0)
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
                MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, "Roblox Product Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);

            bool firstInstance;
#if TESTHOOKS
            const string instanceName = "RobloxProductManager.SingleInstance.Test";
#else
            const string instanceName = "RobloxProductManager.SingleInstance";
#endif
            using (var mutex = new Mutex(true, instanceName, out firstInstance))
            {
                if (!firstInstance)
                {
                    MessageBox.Show("Roblox Product Manager is already open.", "Roblox Product Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.Run(new MainForm());
            }
        }
    }
}
