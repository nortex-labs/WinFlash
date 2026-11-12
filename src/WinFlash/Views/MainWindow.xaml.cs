using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using WinFlash.Core;

namespace WinFlash.Views
{
    public partial class MainWindow : Window
    {
        const int PageWindows = 0, PagePreset = 1, PageComputer = 7;
        readonly MainVm vm;
        readonly Deployer deployer;
        readonly string kitRoot;
        PresetVm previewPreset;

        public MainWindow(string kitRoot, Catalog catalog)
        {
            InitializeComponent();
            this.kitRoot = kitRoot;
            deployer = new Deployer(kitRoot);
            vm = new MainVm(catalog, BuildWindowsOptions());
            DataContext = vm;

            var steps = new[]
            {
                ("Windows", "window"), ("Пресет", "sparkle"), ("Программы", "arrow_download"), ("Приложения Windows", "delete"),
                ("Реклама и слежка", "shield"), ("Оптимизация", "top_speed"), ("Персонализация", "paint_brush"),
                ("Компьютер", "person"), ("Готово", "checkmark_circle"),
            };
            Nav.ItemsSource = steps.Select(s => new NavStep { Title = s.Item1, Icon = IconCache.Glyph(s.Item2) }).ToList();
            Nav.SelectedIndex = 0;

            VersionText.Text = $"Версия {typeof(MainWindow).Assembly.GetName().Version.ToString(3)}" +
                               (App.IsWinPE ? "  ·  режим установки" : "") + $"\nНабор: {kitRoot}";
            if (App.IsWinPE)
            {
                // В WinPE экран часто 1024×768 — разворачиваем окно.
                WindowState = WindowState.Maximized;
                SaveButton.Visibility = Visibility.Collapsed;
                InstallButton.Visibility = Visibility.Visible;
                SaveHeader.Text = "Установка Windows";
                WindowsLead.Text = "Выберите, какую Windows установить. Пункты, которые нужны только в другой версии, скроются из списков.";
            }
            UpdateWindowsHint();
            UpdateTargetText();
            vm.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(MainVm.SelectedWindows)) { UpdateTargetText(); UpdateWindowsHint(); } };
            SourceInitialized += (s, e) => UseDarkTitleBar();
        }

        // ------------------------------------------------------------ выбор Windows

        /// <summary>Карточки «Windows 11» и «Windows 10» по образам на флешке.</summary>
        List<WindowsVm> BuildWindowsOptions()
        {
            var images = deployer.FindWindowsImages();
            var list = new List<WindowsVm>();
            foreach (var (ver, tagline) in new[]
            {
                ("11", "Новая версия: свежий интерфейс, поддержка до 2030-х годов. Нужен процессор не старше ~2018 года (проверку TPM пропускаем)."),
                ("10", "Привычная версия для старых и слабых ПК. Microsoft больше не выпускает для неё обычные обновления."),
            })
            {
                var image = images.FirstOrDefault(i => DetectVersion(i) == ver);
                var w = new WindowsVm { Version = ver, Title = "Windows " + ver, Tagline = tagline, Image = image };
                if (deployer.VentoyRoot == null)
                    w.Status = "Программа запущена не с флешки: выбор просто скроет лишние пункты.";
                else if (image != null)
                    w.Status = "Образ на флешке: " + image;
                else
                {
                    w.Status = $"Образа нет на флешке — положите Windows{ver}.iso в папку ISO\\Windows.";
                    w.IsAvailable = !App.IsWinPE;   // в WinPE без образа ставить нечего
                }
                list.Add(w);
            }
            // Образы, по имени которых версию не понять, — отдельными карточками.
            foreach (var other in images.Where(i => DetectVersion(i) == null))
                list.Add(new WindowsVm { Version = "", Title = Path.GetFileNameWithoutExtension(other), Tagline = "Другой образ Windows.", Image = other, Status = "Образ на флешке: " + other });
            return list;
        }

        static string DetectVersion(string image)
        {
            var name = Path.GetFileNameWithoutExtension(image) ?? "";
            if (name.Contains("11")) return "11";
            if (name.Contains("10")) return "10";
            return null;
        }

        // Checked, а не Click: срабатывает и от мыши, и от клавиатуры, и от экранного диктора.
        void Windows_Checked(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is WindowsVm w && w != vm.SelectedWindows) vm.SelectWindows(w);
        }

        void UpdateWindowsHint()
        {
            var missing = vm.WindowsOptions.Where(w => w.Image == null && !string.IsNullOrEmpty(w.Version)).ToList();
            WindowsHint.Text = deployer.VentoyRoot == null || missing.Count == 0 ? "" :
                "Официальные образы: microsoft.com/software-download/windows" + missing[0].Version +
                " — скачайте ISO и положите в папку ISO\\Windows на флешке.";
        }

        // ------------------------------------------------------------ навигация

        void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Pages == null || Nav.SelectedIndex < 0) return;
            Pages.SelectedIndex = Nav.SelectedIndex;
            BackButton.IsEnabled = Nav.SelectedIndex > 0;
            NextButton.Visibility = Nav.SelectedIndex < Pages.Items.Count - 1 ? Visibility.Visible : Visibility.Hidden;
            CopiedText.Visibility = Visibility.Collapsed;
        }

        void Next_Click(object sender, RoutedEventArgs e) { if (Nav.SelectedIndex < Pages.Items.Count - 1) Nav.SelectedIndex++; }
        void Back_Click(object sender, RoutedEventArgs e) { if (Nav.SelectedIndex > 0) Nav.SelectedIndex--; }
        void Mode_Click(object sender, MouseButtonEventArgs e) => Nav.SelectedIndex = PagePreset;

        void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && PreviewOverlay.Visibility == Visibility.Visible) { ClosePreview(); e.Handled = true; }
        }

        // ------------------------------------------------------------ пресеты, предпросмотр и коды

        void Preset_Checked(object sender, RoutedEventArgs e)
        {
            // Щелчок по уже выбранному пресету ничего не сбрасывает: двойной щелчок для предпросмотра
            // начинается с одиночного, а правки «Своего режима» терять нельзя. Сброс — кнопкой «Вернуть как в пресете».
            if (((FrameworkElement)sender).DataContext is PresetVm p && !p.IsSelected) vm.ApplyPreset(p);
        }

        void Preset_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is PresetVm p) { ShowPreview(p); e.Handled = true; }
        }

        void Preview_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is PresetVm p) ShowPreview(p);
            e.Handled = true;
        }

        void ShowPreview(PresetVm p)
        {
            previewPreset = p;
            PreviewCard.DataContext = vm.BuildPreview(p);
            bool current = p.IsSelected && !p.IsModified;
            PreviewChoose.Content = current ? "Этот пресет уже выбран" : p.IsModified ? $"Вернуть «{p.Name}» без изменений" : $"Выбрать «{p.Name}»";
            PreviewChoose.IsEnabled = !current;
            PreviewOverlay.Visibility = Visibility.Visible;
        }

        void ClosePreview() { PreviewOverlay.Visibility = Visibility.Collapsed; previewPreset = null; }
        void ClosePreview_Click(object sender, RoutedEventArgs e) => ClosePreview();
        void PreviewBackdrop_Click(object sender, MouseButtonEventArgs e) => ClosePreview();

        void PreviewChoose_Click(object sender, RoutedEventArgs e)
        {
            if (previewPreset != null) vm.ApplyPreset(previewPreset);
            ClosePreview();
        }

        void ResetPreset_Click(object sender, RoutedEventArgs e) => vm.ResetToBaseline();

        void Level_Click(object sender, RoutedEventArgs e)
        {
            var b = (Button)sender;
            if (b.DataContext is TabVm tab) vm.ApplyLevel(tab, (string)b.CommandParameter);
        }

        void LoadCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var p = PresetCode.Decode(CodeInput.Text, vm.Catalog);
                vm.LoadFromCode(p);
                CodeError.Foreground = (System.Windows.Media.Brush)FindResource("Good");
                CodeError.Text = "Код загружен: галочки на всех шагах расставлены.";
            }
            catch (FormatException ex)
            {
                CodeError.Foreground = (System.Windows.Media.Brush)FindResource("Bad");
                CodeError.Text = ex.Message;
            }
        }

        void CodeInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) LoadCode_Click(sender, e);
        }

        void CopyCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(vm.Code);
                CopiedText.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { MessageBox.Show(this, "Не удалось скопировать: " + ex.Message, "WinFlash"); }
        }

        void SelectAll_Click(object sender, RoutedEventArgs e) => vm.SetAll((GroupVm)((FrameworkElement)sender).Tag, true);
        void SelectNone_Click(object sender, RoutedEventArgs e) => vm.SetAll((GroupVm)((FrameworkElement)sender).Tag, false);

        void Password_Changed(object sender, RoutedEventArgs e)
            => vm.Password = PasswordInput.Password.Length > 0 ? PasswordInput.Password : null;

        // ------------------------------------------------------------ сохранение

        void UpdateTargetText()
        {
            var w = vm.SelectedWindows;
            if (App.IsWinPE)
            {
                TargetText.Text = w?.Image != null
                    ? $"Будет установлена {w.Title} (образ {w.Image}). Выбор запишется на флешку, и сразу запустится установщик Windows. " +
                      "Диск для установки вы выберете в его окне — дальше всё пойдёт само."
                    : "Не выбран образ Windows — вернитесь на первый шаг.";
                return;
            }
            if (deployer.VentoyRoot != null)
            {
                var images = deployer.FindWindowsImages();
                TargetText.Text = $"Флешка Ventoy: {deployer.VentoyRoot}\n" +
                                  (images.Count > 0
                                      ? "Автоустановка будет привязана к образам: " + string.Join(", ", images) + ".\n" +
                                        $"При загрузке с флешки выберите «{w?.Title ?? "Windows"}» и пункт «winflash.xml»."
                                      : "Образы Windows на флешке не найдены — положите ISO в папку ISO\\Windows.");
            }
            else
            {
                TargetText.Text = "Программа запущена не с флешки Ventoy. Выбор сохранится в папку набора, " +
                                  "а файл автоустановки — в config\\autounattend.xml.";
            }
        }

        string ValidateIdentity(KitProfile profile)
        {
            if (!string.IsNullOrEmpty(profile.ComputerName) && !IsValidComputerName(profile.ComputerName))
                return "Имя компьютера: только латинские буквы, цифры и дефис, до 15 символов, не только цифры.";
            if (!string.IsNullOrEmpty(profile.ComputerName) &&
                string.Equals(profile.ComputerName, profile.UserName, StringComparison.OrdinalIgnoreCase))
                return "Имя пользователя не может совпадать с именем компьютера — Windows остановит установку с ошибкой.";
            return null;
        }

        void Save_Click(object sender, RoutedEventArgs e)
        {
            var profile = vm.BuildProfile();
            var error = ValidateIdentity(profile);
            if (error != null) { ShowResult(false, error); Nav.SelectedIndex = PageComputer; return; }
            try
            {
                var r = deployer.Save(profile, vm.Catalog, vm.Password);
                var sb = new StringBuilder("Записано:\n");
                foreach (var w in r.Written) sb.Append("  • ").Append(w).Append('\n');
                foreach (var w in r.Warnings) sb.Append("⚠ ").Append(w).Append('\n');
                if (!App.IsWinPE)
                    sb.Append($"\nТеперь загрузитесь с этой флешки, выберите «{vm.WindowsTitle}» и пункт winflash.xml. Дальше всё произойдёт само.");
                ShowResult(r.Warnings.Count == 0, sb.ToString());
            }
            catch (Exception ex)
            {
                ShowResult(false, "Не удалось записать: " + ex.Message);
            }
        }

        void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            var name = PresetNameInput.Text.Trim();
            if (name.Length == 0) { ShowResult(false, "Введите название пресета."); return; }
            try
            {
                var p = vm.BuildProfile();
                p.Name = name;
                p.Description = "Свой пресет.";
                p.Order = 50;
                p.ComputerName = p.UserName = p.Windows = "";
                var file = "custom-" + new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).ToLowerInvariant() + ".json";
                var path = Path.Combine(kitRoot, "profiles", file);
                File.WriteAllText(path, p.ToJson(vm.Catalog), new UTF8Encoding(false));
                p.FileId = Path.GetFileNameWithoutExtension(path);

                var existing = vm.Presets.FirstOrDefault(x => x.Profile.FileId == p.FileId);
                if (existing != null) vm.Presets.Remove(existing);
                var preset = new PresetVm { Profile = p };
                vm.Presets.Add(preset);
                vm.ApplyPreset(preset);
                ShowResult(true, $"Пресет «{name}» сохранён: {path}. Он появится на шаге «Пресет».");
            }
            catch (Exception ex) { ShowResult(false, "Не удалось сохранить пресет: " + ex.Message); }
        }

        void Install_Click(object sender, RoutedEventArgs e)
        {
            // Режим WinPE: записываем выбор на флешку и запускаем установку Windows с нашим autounattend.
            var image = vm.SelectedWindows?.Image;
            if (image == null) { ShowResult(false, "Не выбран образ Windows: вернитесь на шаг «Windows»."); Nav.SelectedIndex = PageWindows; return; }
            var profile = vm.BuildProfile();
            var error = ValidateIdentity(profile);
            if (error != null) { ShowResult(false, error); Nav.SelectedIndex = PageComputer; return; }
            InstallButton.IsEnabled = false;
            ShowResult(true, "Подключаю образ и запускаю установку…");
            try
            {
                var r = deployer.Save(profile, vm.Catalog, vm.Password);
                var iso = WinPeSetup.ResolveImage(deployer, image);
                var startError = WinPeSetup.Start(iso, r.UnattendPath);
                if (startError != null) { ShowResult(false, startError); InstallButton.IsEnabled = true; return; }
                Hide();   // дальше — окно установщика Windows
            }
            catch (Exception ex)
            {
                ShowResult(false, "Не удалось запустить установку: " + ex.Message);
                InstallButton.IsEnabled = true;
            }
        }

        void ShowResult(bool ok, string text)
        {
            SaveResult.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "Good" : "Bad");
            SaveResult.Text = text;
        }

        static bool IsValidComputerName(string name)
            => name.Length <= 15 && name.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || char.IsDigit(c) || c == '-')
               && !name.All(char.IsDigit);

        // ------------------------------------------------------------ тёмный заголовок окна (Windows 10 20H1+ / 11)

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void UseDarkTitleBar()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
            }
            catch { /* в WinPE dwmapi может отсутствовать — не страшно */ }
        }
    }
}
