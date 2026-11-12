using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;
using WinFlash.Core;
using WinFlash.Views;

namespace WinFlash
{
    /// <summary>
    /// Запуск:
    ///   WinFlash.exe                 — конфигуратор (выбор программ, настроек, кода пресета);
    ///   WinFlash.exe --apply         — применение config\selected.json (при первом входе после установки);
    ///   WinFlash.exe --apply --force — применить ещё раз, даже если уже применялось;
    ///   WinFlash.exe --kit &lt;папка&gt;  — взять набор (catalog, profiles, scripts) из другой папки.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>Запущены ли мы в WinPE (среде установки Windows).</summary>
        public static bool IsWinPE { get; } = DetectWinPE();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, ex) =>
            {
                MessageBox.Show(ex.Exception.ToString(), "WinFlash — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };

            var args = e.Args.Select(a => a.Trim()).ToList();
            bool apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
            bool force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
            int kitIdx = args.FindIndex(a => a.Equals("--kit", StringComparison.OrdinalIgnoreCase));
            string kitRoot = kitIdx >= 0 && kitIdx + 1 < args.Count ? Path.GetFullPath(args[kitIdx + 1]) : FindKitRoot();

            if (kitRoot == null)
            {
                MessageBox.Show("Не найдена папка набора (catalog\\tweaks.json). Запускайте программу из папки WinFlash на флешке.",
                                "WinFlash", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(2);
                return;
            }

            if (apply)
            {
                var profile = Path.Combine(kitRoot, "config", "selected.json");
                if (!File.Exists(profile)) { Shutdown(0); return; }
                if (!force && File.Exists(Path.Combine(kitRoot, "config", "applied.txt"))) { Shutdown(0); return; }
                if (!IsAdmin())
                {
                    // Перезапуск с правами администратора (UAC).
                    try
                    {
                        Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                            string.Join(" ", e.Args.Select(a => "\"" + a + "\""))) { UseShellExecute = true, Verb = "runas" });
                    }
                    catch { /* пользователь отказался */ }
                    Shutdown(0);
                    return;
                }
                MainWindow = new ApplyWindow(kitRoot, profile);
            }
            else
            {
                Catalog catalog;
                try { catalog = Catalog.Load(kitRoot); }
                catch (Exception ex)
                {
                    MessageBox.Show("Не удалось прочитать каталог:\n" + ex.Message, "WinFlash", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(3);
                    return;
                }
                MainWindow = new MainWindow(kitRoot, catalog);
            }
            MainWindow.Show();
        }

        /// <summary>
        /// Папка рядом с exe; копия exe в корне флешки берёт набор из WinFlash\;
        /// при запуске из папки сборки — kit\ в корне проекта.
        /// </summary>
        static string FindKitRoot()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            if (IsKit(dir)) return dir.TrimEnd('\\');
            var sub = Path.Combine(dir, "WinFlash");
            if (IsKit(sub)) return sub;
            for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "kit");
                if (IsKit(candidate)) return candidate;
            }
            return null;
        }

        static bool IsKit(string dir) => File.Exists(Path.Combine(dir, "catalog", "tweaks.json"));

        static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        static bool DetectWinPE()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT"))
                    return k != null;
            }
            catch { return false; }
        }
    }
}
