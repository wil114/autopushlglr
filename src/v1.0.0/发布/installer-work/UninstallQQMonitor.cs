using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class UninstallQQMonitor
{
    [STAThread]
    private static void Main(string[] args)
    {
        string appDir = args.Length > 0 && Directory.Exists(args[0])
            ? Path.GetFullPath(args[0])
            : AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QQMonitor");
        if (!String.Equals(appDir.TrimEnd(Path.DirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("卸载目标不在预期目录，已取消。\r\n目标：" + appDir, "QQ抓取监控", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (MessageBox.Show("确认卸载 QQ抓取监控？\r\n会删除程序、配置、日志和快捷方式；不会删除 QQ、游戏或 NapCat。",
            "确认卸载", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        try
        {
            StopProcesses();
            DeleteShortcut(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "QQ抓取监控.lnk");
            DeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "QQ抓取监控"), "QQ抓取监控.lnk");
            DeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "QQ抓取监控"), "卸载QQ抓取监控.lnk");
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\QQMonitor", false);

            string cmd = Path.Combine(Path.GetTempPath(), "QQMonitorUninstall-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".cmd");
            File.WriteAllText(cmd,
                "@echo off\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                "rmdir /s /q \"" + appDir + "\"\r\n" +
                "del /q \"" + cmd + "\"\r\n",
                System.Text.Encoding.ASCII);
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\"") { CreateNoWindow = true, UseShellExecute = false });
            MessageBox.Show("卸载已开始，程序目录会在几秒内删除。", "QQ抓取监控", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("卸载失败：" + ex.Message, "QQ抓取监控", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void StopProcesses()
    {
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (process.ProcessName.Equals("qq-monitor-ui", StringComparison.OrdinalIgnoreCase) ||
                    process.ProcessName.StartsWith("napcat-monitor-v", StringComparison.OrdinalIgnoreCase))
                    process.Kill();
            }
            catch { }
        }
    }

    private static void DeleteShortcut(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        if (File.Exists(path)) File.Delete(path);
        if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
            Directory.Delete(directory);
    }
}
