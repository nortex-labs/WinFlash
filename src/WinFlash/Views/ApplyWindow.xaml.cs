using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using WinFlash.Core;

namespace WinFlash.Views
{
    /// <summary>
    /// Окно «Настраиваем Windows»: запускает scripts\Apply-Profile.ps1 и показывает его прогресс.
    /// </summary>
    public partial class ApplyWindow : Window
    {
        readonly string kitRoot;
        readonly string profilePath;
        bool running;

        public ApplyWindow(string kitRoot, string profilePath)
        {
            InitializeComponent();
            this.kitRoot = kitRoot;
            this.profilePath = profilePath;
            try { ProfileText.Text = "Пресет: " + KitProfile.Load(profilePath).Name; } catch { }
            Loaded += (s, e) => Start();
            Closing += OnClosing;
        }

        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint flags);
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

        void Start()
        {
            var script = Path.Combine(kitRoot, "scripts", "Apply-Profile.ps1");
            if (!File.Exists(script)) { Finish(-1, $"Не найден скрипт {script}"); return; }

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -ProfilePath \"{profilePath}\" -KitRoot \"{kitRoot}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = kitRoot,
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) Dispatcher.BeginInvoke(new Action(() => OnLine(e.Data))); };
            p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Dispatcher.BeginInvoke(new Action(() => AppendLog("! " + e.Data))); };
            p.Exited += (s, e) => Dispatcher.BeginInvoke(new Action(() => { if (running) Finish(p.ExitCode == 0 ? 0 : -1, null); }));

            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);   // не засыпать во время установки
            running = true;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }

        int errors;
        string logFile;

        void OnLine(string line)
        {
            if (line.StartsWith("@@STEP|"))
            {
                var parts = line.Split(new[] { '|' }, 4);
                if (parts.Length == 4 && int.TryParse(parts[1], out var i) && int.TryParse(parts[2], out var n) && n > 0)
                {
                    var pct = Math.Min(100, (i - 1) * 100.0 / n);
                    Progress.Value = pct;
                    PercentText.Text = $"{pct:0}%";
                    StepText.Text = parts[3];
                }
                return;
            }
            if (line.StartsWith("@@DONE|"))
            {
                var parts = line.Split(new[] { '|' }, 3);
                int.TryParse(parts.Length > 1 ? parts[1] : "0", out errors);
                logFile = parts.Length > 2 ? parts[2] : null;
                Finish(errors, null);
                return;
            }
            AppendLog(line);
        }

        void AppendLog(string line)
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        void Finish(int errorCount, string fatal)
        {
            if (!running && fatal == null) return;
            running = false;
            SetThreadExecutionState(ES_CONTINUOUS);
            Topmost = false;

            try
            {
                File.WriteAllText(Path.Combine(kitRoot, "config", "applied.txt"),
                                  DateTime.Now.ToString("s") + " errors=" + errorCount, Encoding.UTF8);
            }
            catch { }

            Progress.Value = 100;
            PercentText.Text = "100%";
            if (fatal != null)
            {
                TitleText.Text = "Не удалось запустить настройку";
                StepText.Text = fatal;
                HintText.Text = "Windows установлена, но выбранные настройки не применены.";
            }
            else if (errorCount == 0)
            {
                TitleText.Text = "Готово!";
                StepText.Text = "Всё настроено";
                HintText.Text = "Чтобы все настройки точно вступили в силу, перезагрузите компьютер.";
            }
            else
            {
                TitleText.Text = "Готово, но не всё получилось";
                StepText.Text = $"Проблем: {errorCount}. Подробности — в журнале ниже.";
                HintText.Text = "Пропущенные программы можно установить вручную. Журнал: " + (logFile ?? @"C:\Windows\Setup\WinFlash\Logs");
            }
            CloseButton.Visibility = Visibility.Visible;
            RebootButton.Visibility = Visibility.Visible;
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (!running) return;
            var r = MessageBox.Show(this, "Настройка ещё идёт. Если закрыть окно, она продолжится в фоне без отображения прогресса.\n\nЗакрыть?",
                                    "WinFlash", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) e.Cancel = true;
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();

        void Reboot_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true });
            Close();
        }
    }
}
