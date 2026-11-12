using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinFlash.Core;

namespace WinFlash.Views
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value; Raise(name); return true;
        }
    }

    /// <summary>Значки: картинки программ из icons\*.png и векторные иконки из Icons.xaml.</summary>
    public static class IconCache
    {
        static readonly Dictionary<string, ImageSource> images = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource Image(string kitRoot, string id)
        {
            if (kitRoot == null || string.IsNullOrEmpty(id)) return null;
            if (images.TryGetValue(id, out var cached)) return cached;
            ImageSource result = null;
            try
            {
                var path = Path.Combine(kitRoot, "icons", id + ".png");
                if (File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    using (var fs = File.OpenRead(path))
                    {
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;   // файл не держим открытым
                        bmp.StreamSource = fs;
                        bmp.EndInit();
                    }
                    bmp.Freeze();
                    result = bmp;
                }
            }
            catch { /* нет кодека (редкий WinPE) или битый файл — покажем векторную иконку */ }
            images[id] = result;
            return result;
        }

        public static Geometry Glyph(string name)
            => string.IsNullOrEmpty(name) ? null : Application.Current?.TryFindResource("ic." + name) as Geometry;
    }

    public sealed class ItemVm : Observable
    {
        public ItemVm(CatalogItem source, string kitRoot, string fallbackIcon)
        {
            Source = source;
            IconImage = IconCache.Image(kitRoot, source.Id);
            IconGeometry = IconImage == null ? IconCache.Glyph(source.Icon ?? fallbackIcon) : null;
        }
        public CatalogItem Source { get; }
        public string Id => Source.Id;
        public string Title => Source.Title;
        public string Description => Source.Description;
        public bool HasDescription => !string.IsNullOrWhiteSpace(Source.Description);
        public ImageSource IconImage { get; }
        public Geometry IconGeometry { get; }
        public bool HasImage => IconImage != null;
        public bool HasGeometry => IconGeometry != null;
        /// <summary>Пометка «только Windows 11/10» для карточки.</summary>
        public string WinTag => string.IsNullOrEmpty(Source.Win) ? null : "Windows " + Source.Win;
        public bool HasWinTag => WinTag != null;
        public string LevelTag => Source.Level == "max" ? "максимум" : null;

        bool isChecked;
        public bool IsChecked { get => isChecked; set => Set(ref isChecked, value); }

        bool isVisible = true;
        public bool IsVisible { get => isVisible; set => Set(ref isVisible, value); }
    }

    public sealed class GroupVm : Observable
    {
        public string Title { get; set; }
        public ObservableCollection<ItemVm> Items { get; } = new ObservableCollection<ItemVm>();
        public string Counter => $"{Items.Count(i => i.IsVisible && i.IsChecked)} из {Items.Count(i => i.IsVisible)}";
        public bool IsVisible => Items.Any(i => i.IsVisible);
        public void Refresh() { Raise(nameof(Counter)); Raise(nameof(IsVisible)); }
    }

    /// <summary>Вкладка с группами галочек и мини-пресетами «Оптимально / Максимум».</summary>
    public sealed class TabVm : Observable
    {
        public string Key { get; set; }
        /// <summary>Название вкладки — для уникальных имён кнопок в UI Automation.</summary>
        public string Title { get; set; }
        public List<GroupVm> Groups { get; } = new List<GroupVm>();
        public IEnumerable<ItemVm> Items => Groups.SelectMany(g => g.Items);
        /// <summary>Менять ли схему питания вместе с мини-пресетом (вкладка «Оптимизация»).</summary>
        public bool ControlsPower { get; set; }
        public string OptimalHint { get; set; }
        public string MaxHint { get; set; }
        public bool HasMax => Items.Any(i => i.Source.Level == "max");

        public bool IsOptimal => Items.Any(i => i.IsVisible && i.Source.InOptimal) &&
                                 Items.Where(i => i.IsVisible).All(i => i.IsChecked == i.Source.InOptimal);
        public bool IsMax => HasMax && Items.Where(i => i.IsVisible).All(i => i.IsChecked == i.Source.InMax);
        public bool IsNone => Items.Where(i => i.IsVisible).All(i => !i.IsChecked);
        public string State => IsOptimal ? "Сейчас: оптимально" : IsMax ? "Сейчас: максимум" : IsNone ? "Сейчас: ничего не выбрано" : "Сейчас: свой выбор";
        public void Refresh() { Raise(nameof(IsOptimal)); Raise(nameof(IsMax)); Raise(nameof(IsNone)); Raise(nameof(State)); }
    }

    public sealed class PresetVm : Observable
    {
        public KitProfile Profile { get; set; }
        public string Name => Profile.Name;
        public string Description => Profile.Description;
        public string Summary => $"{Profile.Tweaks.Count} настроек · {Profile.InstallApps.Count} программ · удалить {Profile.RemoveApps.Count}";
        public List<string> Highlights => Profile.Highlights;
        public bool HasHighlights => Profile.Highlights.Count > 0;
        public string PowerTitle => PowerName(Profile.PowerPlan);
        public static string PowerName(string plan) => plan == "ultimate" ? "Питание: максимальное"
                                                    : plan == "high" ? "Питание: высокое" : "Питание: обычное";
        bool isSelected;
        public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
        /// <summary>Пресет выбран, но потом галочки поменяли — это уже «Свой режим».</summary>
        bool isModified;
        public bool IsModified { get => isModified; set => Set(ref isModified, value); }
    }

    public sealed class PowerPlanVm : Observable
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        bool isSelected;
        public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
    }

    /// <summary>Карточка «Какую Windows ставим».</summary>
    public sealed class WindowsVm : Observable
    {
        public string Version { get; set; }          // «11» / «10»
        public string Title { get; set; }
        public string Tagline { get; set; }
        public string Image { get; set; }            // путь в стиле Ventoy или null
        public string Status { get; set; }
        public bool IsAvailable { get; set; } = true;
        public bool IsMissing => !IsAvailable;
        bool isSelected;
        public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
    }

    /// <summary>Пункт бокового меню. ToString — имя для экранного диктора и UI Automation.</summary>
    public sealed class NavStep
    {
        public string Title { get; set; }
        public Geometry Icon { get; set; }
        public override string ToString() => Title;
    }

    /// <summary>Содержимое окна «Что входит в пресет».</summary>
    public sealed class PreviewVm
    {
        public PresetVm Preset { get; set; }
        public string Name => Preset.Name;
        public string Description => Preset.Description;
        public string PowerTitle => Preset.PowerTitle;
        /// <summary>Считается по видимым пунктам — так же, как списки ниже.</summary>
        public string Summary { get; set; }
        public List<PreviewSection> Sections { get; } = new List<PreviewSection>();
    }

    public sealed class PreviewSection
    {
        public string Title { get; set; }
        public string Hint { get; set; }
        public Geometry Icon { get; set; }
        public List<ItemVm> Items { get; set; }
        public string Count => Items.Count.ToString();
    }

    public sealed class MainVm : Observable
    {
        // Категории настроек по шагам: «Реклама и слежка», «Персонализация», остальное — «Оптимизация».
        static readonly HashSet<string> PersonalCategories = new HashSet<string> { "look", "start", "explorer" };
        static readonly HashSet<string> PrivacyCategories = new HashSet<string> { "ads", "tracking" };

        public const string DefaultComputerName = "ADMIN-PC";
        public const string DefaultUserName = "Admin";

        public Catalog Catalog { get; }
        public ObservableCollection<PresetVm> Presets { get; } = new ObservableCollection<PresetVm>();
        public ObservableCollection<GroupVm> AppGroups { get; } = new ObservableCollection<GroupVm>();
        public ObservableCollection<GroupVm> RemoveGroups { get; } = new ObservableCollection<GroupVm>();
        public ObservableCollection<GroupVm> OptimizeGroups { get; } = new ObservableCollection<GroupVm>();
        public ObservableCollection<GroupVm> PrivacyGroups { get; } = new ObservableCollection<GroupVm>();
        public ObservableCollection<GroupVm> PersonalGroups { get; } = new ObservableCollection<GroupVm>();
        public ObservableCollection<PowerPlanVm> PowerPlans { get; } = new ObservableCollection<PowerPlanVm>();
        public ObservableCollection<WindowsVm> WindowsOptions { get; } = new ObservableCollection<WindowsVm>();

        public TabVm AppsTab { get; }
        public TabVm RemoveTab { get; }
        public TabVm PrivacyTab { get; }
        public TabVm OptimizeTab { get; }
        public TabVm PersonalTab { get; }
        IEnumerable<TabVm> Tabs => new[] { AppsTab, RemoveTab, PrivacyTab, OptimizeTab, PersonalTab };

        readonly List<ItemVm> tweakItems = new List<ItemVm>();
        readonly List<ItemVm> installItems = new List<ItemVm>();
        readonly List<ItemVm> removeItems = new List<ItemVm>();
        bool suspend;
        /// <summary>Пресет, от которого считаются изменения («Свой режим»).</summary>
        KitProfile baseline;

        public event EventHandler SelectionChanged;

        public MainVm(Catalog catalog, IEnumerable<WindowsVm> windows)
        {
            Catalog = catalog;

            foreach (var p in catalog.Presets) Presets.Add(new PresetVm { Profile = p });
            foreach (var w in windows) WindowsOptions.Add(w);

            PowerPlans.Add(new PowerPlanVm { Id = "balanced", Title = "Сбалансированная", Description = "Обычный режим Windows. Лучший выбор для ноутбука." });
            PowerPlans.Add(new PowerPlanVm { Id = "high", Title = "Высокая производительность", Description = "Процессор не сбавляет частоту. Для ПК." });
            PowerPlans.Add(new PowerPlanVm { Id = "ultimate", Title = "Максимальная", Description = "Ещё меньше энергосбережения. Для игровых ПК." });
            foreach (var pp in PowerPlans) pp.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(PowerPlanVm.IsSelected)) OnChanged(); };

            BuildGroups(catalog.InstallApps, catalog.AppCategories, AppGroups, installItems, c => true, "apps");
            foreach (var g in GroupBy(catalog.RemoveApps, null, removeItems, "delete")) RemoveGroups.Add(g);
            BuildGroups(catalog.Tweaks, catalog.TweakCategories, PersonalGroups, tweakItems, c => PersonalCategories.Contains(c), "settings");
            BuildGroups(catalog.Tweaks, catalog.TweakCategories, PrivacyGroups, tweakItems, c => PrivacyCategories.Contains(c), "shield");
            BuildGroups(catalog.Tweaks, catalog.TweakCategories, OptimizeGroups, tweakItems,
                        c => !PersonalCategories.Contains(c) && !PrivacyCategories.Contains(c), "top_speed");

            AppsTab = MakeTab("apps", AppGroups,
                "Браузер, архиватор, видеоплеер, читалка PDF и библиотеки, без которых не запускаются программы.", null);
            RemoveTab = MakeTab("remove", RemoveGroups,
                "Удаляет то, чем почти никто не пользуется. Медиаплеер, записки, Xbox и Game Bar остаются.",
                "Удаляет ещё и OneDrive, новый Outlook, «Связь с телефоном», звукозапись и «Быструю помощь».");
            PrivacyTab = MakeTab("privacy", PrivacyGroups,
                "Убирает всю рекламу и почти всю слежку. Ничего полезного не ломается.",
                "Отключает всю телеметрию: службу, задачи в планировщике, отчёты об ошибках, геолокацию и облачный поиск.");
            OptimizeTab = MakeTab("optimize", OptimizeGroups,
                "Безопасные ускорения для любого ПК и ноутбука. Питание — сбалансированное.",
                "Всё для скорости: без анимаций и фоновых приложений, игровые настройки, мышь без ускорения, питание — высокое.");
            OptimizeTab.ControlsPower = true;
            PersonalTab = MakeTab("personal", PersonalGroups,
                "Удобный проводник и чистая панель задач. Тему и расположение «Пуска» выберите сами.", null);

            var initialWin = WindowsOptions.FirstOrDefault(w => w.IsAvailable && w.Version == "11") ?? WindowsOptions.FirstOrDefault(w => w.IsAvailable);
            if (initialWin != null) SelectWindows(initialWin);

            var initial = Presets.FirstOrDefault();
            if (initial != null) ApplyPreset(initial);
            else PowerPlans[0].IsSelected = true;
        }

        static readonly Dictionary<string, string> TabTitles = new Dictionary<string, string>
        {
            ["apps"] = "Программы", ["remove"] = "Встроенные приложения", ["privacy"] = "Реклама и слежка",
            ["optimize"] = "Оптимизация", ["personal"] = "Персонализация",
        };

        TabVm MakeTab(string key, IEnumerable<GroupVm> groups, string optimalHint, string maxHint)
        {
            var t = new TabVm { Key = key, Title = TabTitles[key], OptimalHint = optimalHint, MaxHint = maxHint };
            t.Groups.AddRange(groups);
            return t;
        }

        void BuildGroups(List<CatalogItem> items, List<Category> categories, ObservableCollection<GroupVm> target,
                         List<ItemVm> all, Func<string, bool> filter, string fallbackIcon)
        {
            foreach (var cat in categories.Where(c => filter(c.Id)))
            {
                var list = items.Where(i => i.Category == cat.Id).ToList();
                if (list.Count == 0) continue;
                foreach (var g in GroupBy(list, cat.Title, all, fallbackIcon)) target.Add(g);
            }
            // Элементы без известной категории — в «Другое».
            var known = new HashSet<string>(categories.Select(c => c.Id));
            var other = items.Where(i => !known.Contains(i.Category ?? "") && filter(i.Category ?? "")).ToList();
            if (other.Count > 0) foreach (var g in GroupBy(other, "Другое", all, fallbackIcon)) target.Add(g);
        }

        IEnumerable<GroupVm> GroupBy(List<CatalogItem> items, string title, List<ItemVm> all, string fallbackIcon)
        {
            var g = new GroupVm { Title = title };
            foreach (var it in items)
            {
                var vm = new ItemVm(it, Catalog.Root, fallbackIcon);
                vm.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(ItemVm.IsChecked)) { g.Refresh(); OnChanged(); } };
                g.Items.Add(vm);
                all.Add(vm);
            }
            yield return g;
        }

        void OnChanged()
        {
            if (suspend) return;
            RaiseAll();
        }

        void RaiseAll()
        {
            foreach (var t in Tabs) t?.Refresh();
            UpdateMode();
            Raise(nameof(Code));
            Raise(nameof(Summary));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------ Windows 11 / 10

        string windows = "";
        public string Windows => windows;
        public WindowsVm SelectedWindows => WindowsOptions.FirstOrDefault(w => w.IsSelected);

        public void SelectWindows(WindowsVm w)
        {
            foreach (var x in WindowsOptions) x.IsSelected = x == w;
            windows = w?.Version ?? "";
            // Пункты для другой версии Windows прячем: галочка у них остаётся, но применять их незачем.
            foreach (var i in tweakItems.Concat(installItems).Concat(removeItems)) i.IsVisible = i.Source.AppliesTo(windows);
            foreach (var g in AllGroups) g.Refresh();
            Raise(nameof(Windows));
            Raise(nameof(SelectedWindows));
            Raise(nameof(WindowsTitle));
            RaiseAll();
        }

        public string WindowsTitle => SelectedWindows?.Title ?? "Windows не выбрана";

        IEnumerable<GroupVm> AllGroups => AppGroups.Concat(RemoveGroups).Concat(PrivacyGroups).Concat(OptimizeGroups).Concat(PersonalGroups);

        // ------------------------------------------------------------ поля «Компьютер»

        string computerName = DefaultComputerName;
        public string ComputerName { get => computerName; set { if (Set(ref computerName, value)) RaiseAll(); } }

        string userName = DefaultUserName;
        public string UserName { get => userName; set { if (Set(ref userName, value)) RaiseAll(); } }

        public string Password { get; set; }

        // ------------------------------------------------------------ пресеты, «Свой режим» и код

        public void ApplyPreset(PresetVm preset)
        {
            foreach (var p in Presets) { p.IsSelected = p == preset; p.IsModified = false; }
            baseline = preset.Profile;
            Load(preset.Profile, keepIdentity: true);
        }

        /// <summary>Расставляет все галочки по профилю. Пустые имя ПК и пользователя не затирают текущие.</summary>
        public void Load(KitProfile p, bool keepIdentity)
        {
            suspend = true;
            try
            {
                foreach (var i in tweakItems) i.IsChecked = p.Tweaks.Contains(i.Id);
                foreach (var i in installItems) i.IsChecked = p.InstallApps.Contains(i.Id);
                foreach (var i in removeItems) i.IsChecked = p.RemoveApps.Contains(i.Id);
                foreach (var pp in PowerPlans) pp.IsSelected = pp.Id == p.PowerPlan;
                if (!PowerPlans.Any(x => x.IsSelected)) PowerPlans[0].IsSelected = true;
                if (!string.IsNullOrEmpty(p.ComputerName)) ComputerName = p.ComputerName;
                if (!string.IsNullOrEmpty(p.UserName)) UserName = p.UserName;
            }
            finally { suspend = false; }
            foreach (var g in AllGroups) g.Refresh();
            RaiseAll();
        }

        /// <summary>Загрузка из кода: «Свой режим» без базового пресета.</summary>
        public void LoadFromCode(KitProfile p)
        {
            foreach (var x in Presets) { x.IsSelected = false; x.IsModified = false; }
            baseline = null;
            Load(p, keepIdentity: false);
        }

        int changedCount;
        public bool IsCustom => baseline == null || changedCount > 0;
        public string ModeTitle => !IsCustom ? baseline.Name : "Свой режим";
        public string ModeDetail => baseline == null
            ? "Галочки расставлены вручную или из кода."
            : changedCount == 0
                ? "Готовый пресет без изменений."
                : $"На основе «{baseline.Name}»: изменено {changedCount} {Plural(changedCount, "пункт", "пункта", "пунктов")}.";

        void UpdateMode()
        {
            if (baseline != null)
            {
                var now = BuildProfile();
                changedCount = Diff(baseline.Tweaks, now.Tweaks, tweakItems) + Diff(baseline.InstallApps, now.InstallApps, installItems) +
                               Diff(baseline.RemoveApps, now.RemoveApps, removeItems) + (baseline.PowerPlan == now.PowerPlan ? 0 : 1);
                foreach (var p in Presets) p.IsModified = p.IsSelected && changedCount > 0;
            }
            Raise(nameof(IsCustom));
            Raise(nameof(ModeTitle));
            Raise(nameof(ModeDetail));
        }

        /// <summary>Сколько видимых пунктов отличается (скрытые для другой Windows не считаем).</summary>
        static int Diff(HashSet<string> a, HashSet<string> b, List<ItemVm> items)
            => items.Count(i => i.IsVisible && a.Contains(i.Id) != b.Contains(i.Id));

        public void ResetToBaseline()
        {
            var p = Presets.FirstOrDefault(x => x.IsSelected);
            if (p != null) ApplyPreset(p);
        }

        public static string Plural(int n, string one, string few, string many)
        {
            n = Math.Abs(n) % 100;
            if (n >= 11 && n <= 14) return many;
            switch (n % 10) { case 1: return one; case 2: case 3: case 4: return few; default: return many; }
        }

        public KitProfile BuildProfile()
        {
            var selected = Presets.FirstOrDefault(p => p.IsSelected);
            var p = new KitProfile
            {
                Name = selected == null ? "Свой режим" : selected.IsModified ? selected.Name + " (изменённый)" : selected.Name,
                PowerPlan = PowerPlans.FirstOrDefault(x => x.IsSelected)?.Id ?? "balanced",
                ComputerName = (ComputerName ?? "").Trim(),
                UserName = (UserName ?? "").Trim(),
                Windows = windows,
            };
            // Пункты для другой версии Windows не записываем: галочки остаются, но применять их незачем.
            p.Tweaks.UnionWith(tweakItems.Where(i => i.IsChecked && i.IsVisible).Select(i => i.Id));
            p.InstallApps.UnionWith(installItems.Where(i => i.IsChecked && i.IsVisible).Select(i => i.Id));
            p.RemoveApps.UnionWith(removeItems.Where(i => i.IsChecked && i.IsVisible).Select(i => i.Id));
            return p;
        }

        public string Code => PresetCode.Encode(BuildProfile(), Catalog);

        public string Summary
        {
            get
            {
                var p = BuildProfile();
                var plan = PowerPlans.FirstOrDefault(x => x.IsSelected)?.Title ?? "";
                return $"{WindowsTitle}   ·   Программ: {p.InstallApps.Count}   ·   Удалить встроенных: {p.RemoveApps.Count}   ·   " +
                       $"Настроек: {p.Tweaks.Count}   ·   Питание: {plan}";
            }
        }

        public void SetAll(GroupVm group, bool value)
        {
            suspend = true;
            try { foreach (var i in group.Items.Where(i => i.IsVisible)) i.IsChecked = value; }
            finally { suspend = false; }
            group.Refresh();
            RaiseAll();
        }

        /// <summary>Мини-пресет вкладки: «optimal», «max» или «none».</summary>
        public void ApplyLevel(TabVm tab, string level)
        {
            suspend = true;
            try
            {
                foreach (var i in tab.Items)
                    i.IsChecked = level == "optimal" ? i.Source.InOptimal : level == "max" && i.Source.InMax;
                if (tab.ControlsPower && level != "none")
                    foreach (var pp in PowerPlans) pp.IsSelected = pp.Id == (level == "max" ? "high" : "balanced");
            }
            finally { suspend = false; }
            foreach (var g in tab.Groups) g.Refresh();
            RaiseAll();
        }

        // ------------------------------------------------------------ предпросмотр пресета

        public PreviewVm BuildPreview(PresetVm preset)
        {
            var p = preset.Profile;
            var v = new PreviewVm { Preset = preset };
            List<ItemVm> Pick(IEnumerable<ItemVm> source, HashSet<string> ids, Func<ItemVm, bool> filter = null)
                => source.Where(i => ids.Contains(i.Id) && i.IsVisible && (filter == null || filter(i)))
                         .Select(i => new ItemVm(i.Source, Catalog.Root, i.Source.Icon)).ToList();

            void Add(string title, string hint, string icon, List<ItemVm> items)
            {
                if (items.Count > 0) v.Sections.Add(new PreviewSection { Title = title, Hint = hint, Icon = IconCache.Glyph(icon), Items = items });
            }

            var install = Pick(installItems, p.InstallApps);
            var remove = Pick(removeItems, p.RemoveApps);
            v.Summary = $"{Pick(tweakItems, p.Tweaks).Count} настроек · {install.Count} программ · удалить {remove.Count}";
            Add("Установит программы", "Сами поставятся после первого входа в Windows.", "arrow_download", install);
            Add("Удалит встроенные приложения", "Любое можно вернуть из Microsoft Store.", "delete", remove);
            Add("Реклама и слежка", null, "shield", Pick(tweakItems, p.Tweaks, i => PrivacyCategories.Contains(i.Source.Category)));
            Add("Оптимизация", PresetVm.PowerName(p.PowerPlan) + ".", "top_speed",
                Pick(tweakItems, p.Tweaks, i => !PrivacyCategories.Contains(i.Source.Category) && !PersonalCategories.Contains(i.Source.Category)));
            Add("Внешний вид и удобство", null, "paint_brush", Pick(tweakItems, p.Tweaks, i => PersonalCategories.Contains(i.Source.Category)));
            return v;
        }
    }
}
