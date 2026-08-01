using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class InstallerBootstrap
{
    [STAThread]
    private static void Main()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string zip = Path.Combine(baseDir, "QQMonitorPayload.zip");
            string msi = Path.Combine(baseDir, "NapCatQQ-Desktop-3.1.6-x64.msi");
            string workDir = Path.Combine(Path.GetTempPath(), "QQMonitorPayload");
            string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QQMonitor");

            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            Directory.CreateDirectory(workDir);
            Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"Expand-Archive -LiteralPath '" + zip + "' -DestinationPath '" + workDir + "' -Force\"", true);

            CopyDirectory(Path.Combine(workDir, "app"), appDir);
            Directory.CreateDirectory(Path.Combine(appDir, "日志"));
            Directory.CreateDirectory(Path.Combine(appDir, "已清除记录"));

            CopyIfMissing(Path.Combine(appDir, "config.template.json"), Path.Combine(appDir, "config.json"));
            CopyIfMissing(Path.Combine(appDir, "点击位置模板.json"), Path.Combine(appDir, "点击位置待确认.json"));

            if (!File.Exists(@"C:\Program Files\NapCatQQ Desktop\NapCatQQ-Desktop.exe") && File.Exists(msi))
                Run("msiexec.exe", "/i \"" + msi + "\" /passive /norestart", true);

            CreateShortcut(appDir);
            RegisterUninstaller(appDir);
            Process.Start(new ProcessStartInfo(Path.Combine(appDir, "qq-monitor-ui.exe")) { WorkingDirectory = appDir, UseShellExecute = true });
            MessageBox.Show("QQ抓取监控已安装完成。", "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("安装失败：" + ex.Message, "QQ抓取监控", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void CopyIfMissing(string source, string destination)
    {
        if (!File.Exists(destination) && File.Exists(source))
            File.Copy(source, destination, false);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, destination));
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, destination), true);
    }

    private static void CreateShortcut(string appDir)
    {
        string ps = "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop') + '\\QQ抓取监控.lnk');" +
            "$s.TargetPath='" + Path.Combine(appDir, "qq-monitor-ui.exe").Replace("'", "''") + "';" +
            "$s.WorkingDirectory='" + appDir.Replace("'", "''") + "';$s.Save();" +
            "$dir=[Environment]::GetFolderPath('Programs') + '\\QQ抓取监控'; New-Item -ItemType Directory -Force -Path $dir | Out-Null;" +
            "$s=(New-Object -ComObject WScript.Shell).CreateShortcut($dir + '\\QQ抓取监控.lnk');" +
            "$s.TargetPath='" + Path.Combine(appDir, "qq-monitor-ui.exe").Replace("'", "''") + "';" +
            "$s.WorkingDirectory='" + appDir.Replace("'", "''") + "';$s.Save();" +
            "$s=(New-Object -ComObject WScript.Shell).CreateShortcut($dir + '\\卸载QQ抓取监控.lnk');" +
            "$s.TargetPath='" + Path.Combine(appDir, "UninstallQQMonitor.exe").Replace("'", "''") + "';" +
            "$s.WorkingDirectory='" + appDir.Replace("'", "''") + "';$s.Save();";
        Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps + "\"", true);
    }

    private static void RegisterUninstaller(string appDir)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\QQMonitor"))
        {
            key.SetValue("DisplayName", "QQ抓取监控");
            key.SetValue("DisplayVersion", "v1.0.0");
            key.SetValue("Publisher", "绿头君");
            key.SetValue("InstallLocation", appDir);
            key.SetValue("UninstallString", "\"" + Path.Combine(appDir, "UninstallQQMonitor.exe") + "\"");
            key.SetValue("DisplayIcon", Path.Combine(appDir, "qq-monitor-ui.exe"));
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }

    private static void Run(string fileName, string arguments, bool wait)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using (Process p = Process.Start(start))
        {
            if (wait) p.WaitForExit();
        }
    }
}
