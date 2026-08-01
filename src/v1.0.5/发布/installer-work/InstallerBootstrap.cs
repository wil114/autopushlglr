using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class InstallerBootstrap
{
    private const string AppVersion = "v1.0.5";
    private const string InstallMarker = ".qqmonitor-install";

    [STAThread]
    private static void Main()
    {
        try
        {
            Application.EnableVisualStyles();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string packageDir = Path.Combine(Path.GetTempPath(), "QQMonitorSetupFiles");
            Directory.CreateDirectory(packageDir);
            string zip = GetPackageFile(baseDir, packageDir, "QQMonitorPayload.zip");
            string msi = GetPackageFile(baseDir, packageDir, "NapCatQQ-Desktop-3.1.6-x64.msi");
            string shellZip = Path.Combine(baseDir, "NapCat.Shell-4.18.13.zip");
            string workDir = Path.Combine(Path.GetTempPath(), "QQMonitorPayload");
            string appDir = SelectInstallDir();
            if (String.IsNullOrEmpty(appDir)) return;

            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            Directory.CreateDirectory(workDir);
            Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"Expand-Archive -LiteralPath '" + zip + "' -DestinationPath '" + workDir + "' -Force\"", true);
            if (!File.Exists(shellZip))
                shellZip = Path.Combine(workDir, "dependencies", "NapCat.Shell-4.18.13.zip");

            CopyDirectory(Path.Combine(workDir, "app"), appDir);
            Directory.CreateDirectory(Path.Combine(appDir, "日志"));
            Directory.CreateDirectory(Path.Combine(appDir, "已清除记录"));
            WriteInstallMarker(appDir);

            CopyIfMissing(Path.Combine(appDir, "config.template.json"), Path.Combine(appDir, "config.json"));

            if (!File.Exists(@"C:\Program Files\NapCatQQ Desktop\NapCatQQ-Desktop.exe") && File.Exists(msi))
                Run("msiexec.exe", "/i \"" + msi + "\" /passive /norestart", true);
            if (File.Exists(shellZip))
                InstallNapCatShell(shellZip);

            CreateShortcut(appDir);
            RegisterUninstaller(appDir);
            Process.Start(new ProcessStartInfo(Path.Combine(appDir, "qq-monitor-ui.exe")) { WorkingDirectory = appDir, UseShellExecute = true });
            MessageBox.Show("QQ抓取监控已安装完成。\r\n安装位置：" + appDir, "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("安装失败：" + ex.Message, "QQ抓取监控", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string SelectInstallDir()
    {
        string defaultAppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QQMonitor");
        Directory.CreateDirectory(defaultAppDir);
        using (var dialog = new FolderBrowserDialog())
        {
            dialog.Description = "请选择安装位置。程序会安装到所选目录下的 QQMonitor 文件夹；如果直接选择 QQMonitor 文件夹，则使用该文件夹。";
            dialog.SelectedPath = defaultAppDir;
            dialog.ShowNewFolderButton = true;
            if (dialog.ShowDialog() != DialogResult.OK) return null;

            string appDir = NormalizeInstallDir(dialog.SelectedPath);
            return MessageBox.Show("将安装到：\r\n" + appDir, "确认安装位置", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK
                ? appDir
                : null;
        }
    }

    private static string NormalizeInstallDir(string selectedPath)
    {
        string path = TrimTrailingSeparators(Path.GetFullPath(selectedPath));
        string name = Path.GetFileName(path);
        if (String.Equals(name, "QQMonitor", StringComparison.OrdinalIgnoreCase))
            return path;
        return Path.Combine(path, "QQMonitor");
    }

    private static string GetPackageFile(string baseDir, string tempDir, string fileName)
    {
        string path = Path.Combine(baseDir, fileName);
        if (File.Exists(path)) return path;

        Stream input = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(fileName);
        if (input == null) throw new FileNotFoundException("安装包缺少资源：" + fileName);
        path = Path.Combine(tempDir, fileName);
        using (input)
        using (FileStream output = File.Create(path))
        {
            byte[] buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);
        }
        return path;
    }

    private static string TrimTrailingSeparators(string path)
    {
        string root = Path.GetPathRoot(path);
        while (path.Length > root.Length && (path[path.Length - 1] == Path.DirectorySeparatorChar || path[path.Length - 1] == Path.AltDirectorySeparatorChar))
            path = path.Substring(0, path.Length - 1);
        return path;
    }

    private static void WriteInstallMarker(string appDir)
    {
        File.WriteAllText(Path.Combine(appDir, InstallMarker),
            "QQ抓取监控\r\nVersion=" + AppVersion + "\r\nInstallLocation=" + appDir + "\r\n",
            new UTF8Encoding(false));
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
            key.SetValue("DisplayVersion", AppVersion);
            key.SetValue("Publisher", "绿头君");
            key.SetValue("InstallLocation", appDir);
            key.SetValue("UninstallString", "\"" + Path.Combine(appDir, "UninstallQQMonitor.exe") + "\"");
            key.SetValue("DisplayIcon", Path.Combine(appDir, "qq-monitor-ui.exe"));
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }

    private static void InstallNapCatShell(string shellZip)
    {
        string target = @"C:\ProgramData\NapCatQQ Desktop\components\NapCatQQ";
        Directory.CreateDirectory(target);
        Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"Expand-Archive -LiteralPath '" + shellZip + "' -DestinationPath '" + target + "' -Force\"", true);
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
