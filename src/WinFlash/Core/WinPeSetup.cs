using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace WinFlash.Core
{
    /// <summary>
    /// Режим WinPE: подключает выбранный ISO с флешки и запускает установку Windows
    /// с нашим файлом автоустановки.
    /// </summary>
    public static class WinPeSetup
    {
        /// <summary>Полный путь к образу по пути в стиле Ventoy (/ISO/Windows11.iso).</summary>
        public static string ResolveImage(Deployer deployer, string ventoyPath)
            => Path.Combine(deployer.VentoyRoot ?? Path.GetPathRoot(deployer.KitRoot), ventoyPath.TrimStart('/').Replace('/', '\\'));

        /// <summary>Возвращает текст ошибки или null, если установка запущена.</summary>
        public static string Start(string isoPath, string unattendPath)
        {
            if (!File.Exists(isoPath)) return "Образ не найден: " + isoPath;

            var before = SetupDrives();
            string log = MountWithPowerShell(isoPath);
            var media = SetupDrives().Except(before).FirstOrDefault();
            if (media == null)
            {
                log += "\n" + MountWithDiskpart(isoPath);
                media = SetupDrives().Except(before).FirstOrDefault();
            }
            if (media == null)
                return "Не удалось подключить образ " + Path.GetFileName(isoPath) + ".\n" + log.Trim();

            var setup = Path.Combine(media, "setup.exe");
            if (!File.Exists(setup)) setup = Path.Combine(media, "sources", "setup.exe");
            var args = unattendPath != null && File.Exists(unattendPath) ? $"/unattend:\"{unattendPath}\"" : "";
            Process.Start(new ProcessStartInfo(setup, args) { UseShellExecute = false, WorkingDirectory = media });
            return null;
        }

        /// <summary>Диски, на которых лежит установка Windows (sources\install.wim/esd).</summary>
        static List<string> SetupDrives()
        {
            var list = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    var src = Path.Combine(d.RootDirectory.FullName, "sources");
                    if (File.Exists(Path.Combine(src, "install.wim")) || File.Exists(Path.Combine(src, "install.esd")) ||
                        File.Exists(Path.Combine(src, "install.swm")))
                        list.Add(d.RootDirectory.FullName);
                }
                catch { }
            }
            return list;
        }

        static string MountWithPowerShell(string iso)
            => Run(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                   $"-NoProfile -ExecutionPolicy Bypass -Command \"Mount-DiskImage -ImagePath '{iso.Replace("'", "''")}' | Out-Null\"");

        static string MountWithDiskpart(string iso)
        {
            var script = Path.Combine(Path.GetTempPath(), "rk-mount.txt");
            File.WriteAllText(script, $"select vdisk file=\"{iso}\"\nattach vdisk readonly\n", Encoding.ASCII);
            var r = Run("diskpart.exe", $"/s \"{script}\"");
            System.Threading.Thread.Sleep(3000);
            return r;
        }

        static string Run(string exe, string args)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                });
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(60000);
                return output;
            }
            catch (Exception ex) { return exe + ": " + ex.Message; }
        }
    }
}
