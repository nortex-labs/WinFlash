using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WinFlash.Core
{
    public sealed class Category
    {
        public string Id { get; set; }
        public string Title { get; set; }
    }

    /// <summary>Элемент каталога: настройка, программа для установки или встроенное приложение.</summary>
    public sealed class CatalogItem
    {
        /// <summary>Порядковый номер в каталоге — на него ссылаются коды пресетов.</summary>
        public int Index { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        /// <summary>Имя векторной иконки (Views\Icons.xaml, без «ic.»).</summary>
        public string Icon { get; set; }
        /// <summary>«optimal» — входит в «Оптимально»; «max» — только в «Максимум»; пусто — ни туда, ни туда.</summary>
        public string Level { get; set; }
        /// <summary>«11» или «10», если пункт нужен только в этой версии Windows.</summary>
        public string Win { get; set; }

        public bool InOptimal => Level == "optimal";
        public bool InMax => Level == "optimal" || Level == "max";
        public bool AppliesTo(string windows) => string.IsNullOrEmpty(Win) || string.IsNullOrEmpty(windows) || Win == windows;
    }

    public sealed class Catalog
    {
        public List<Category> TweakCategories { get; } = new List<Category>();
        public List<Category> AppCategories { get; } = new List<Category>();
        public List<CatalogItem> Tweaks { get; } = new List<CatalogItem>();
        public List<CatalogItem> InstallApps { get; } = new List<CatalogItem>();
        public List<CatalogItem> RemoveApps { get; } = new List<CatalogItem>();
        public List<KitProfile> Presets { get; } = new List<KitProfile>();
        /// <summary>Папка набора; значки программ лежат в icons\&lt;id&gt;.png.</summary>
        public string Root { get; private set; }

        public static Catalog Load(string kitRoot)
        {
            var c = new Catalog { Root = kitRoot };
            var tweaks = Json.Parse(ReadText(Path.Combine(kitRoot, "catalog", "tweaks.json")));
            var apps = Json.Parse(ReadText(Path.Combine(kitRoot, "catalog", "apps.json")));

            c.TweakCategories.AddRange(ReadCategories(tweaks));
            c.AppCategories.AddRange(ReadCategories(apps));
            c.Tweaks.AddRange(ReadItems(tweaks.Arr("tweaks")));
            c.InstallApps.AddRange(ReadItems(apps.Arr("install")));
            c.RemoveApps.AddRange(ReadItems(apps.Arr("remove")));

            var profilesDir = Path.Combine(kitRoot, "profiles");
            if (Directory.Exists(profilesDir))
            {
                // Порядок пресетов задаётся полем "order", иначе — по имени файла.
                c.Presets.AddRange(Directory.GetFiles(profilesDir, "*.json")
                    .Select(f => KitProfile.Load(f))
                    .OrderBy(p => p.Order).ThenBy(p => p.Name));
            }
            return c;
        }

        static IEnumerable<Category> ReadCategories(object root)
            => root.Arr("categories").Select(x => new Category { Id = x.Str("id"), Title = x.Str("title") });

        static IEnumerable<CatalogItem> ReadItems(List<object> list)
            => list.Select((x, i) => new CatalogItem
            {
                Index = i,
                Id = x.Str("id"),
                Title = x.Str("title"),
                Description = x.Str("description"),
                Category = x.Str("category"),
                Icon = x.Str("icon"),
                Level = x.Str("level"),
                Win = x.Str("win"),
            });

        public static string ReadText(string path) => File.ReadAllText(path, Encoding.UTF8);
    }

    /// <summary>Набор выбранных настроек: то, что пресет описывает и что применяется после установки.</summary>
    public sealed class KitProfile
    {
        public static readonly string[] PowerPlans = { "balanced", "high", "ultimate" };

        public string FileId { get; set; } = "";
        public int Order { get; set; } = 100;
        public string Name { get; set; } = "Мой пресет";
        public string Description { get; set; } = "";
        public string PowerPlan { get; set; } = "balanced";
        /// <summary>Короткие пункты «что меняется» для карточки пресета.</summary>
        public List<string> Highlights { get; } = new List<string>();
        public HashSet<string> Tweaks { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RemoveApps { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> InstallApps { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Параметры установки (идут в autounattend.xml, в пресет-файлы каталога не входят).
        public string ComputerName { get; set; } = "";
        public string UserName { get; set; } = "";
        /// <summary>Какую Windows ставим: «11», «10» или пусто (не выбрано).</summary>
        public string Windows { get; set; } = "";

        public static KitProfile Load(string path)
        {
            var p = FromJson(Json.Parse(Catalog.ReadText(path)));
            p.FileId = Path.GetFileNameWithoutExtension(path);
            return p;
        }

        public static KitProfile FromJson(object o)
        {
            var p = new KitProfile
            {
                Name = o.Str("name", "Без названия"),
                Description = o.Str("description"),
                PowerPlan = o.Str("powerPlan", "balanced"),
                ComputerName = o.Str("computerName"),
                UserName = o.Str("userName"),
                Windows = o.Str("windows"),
            };
            if (int.TryParse(o.Str("order", "100"), out var order)) p.Order = order;
            foreach (var x in o.Arr("highlights")) p.Highlights.Add(Convert.ToString(x));
            foreach (var x in o.Arr("tweaks")) p.Tweaks.Add(Convert.ToString(x));
            foreach (var x in o.Arr("removeApps")) p.RemoveApps.Add(Convert.ToString(x));
            foreach (var x in o.Arr("installApps")) p.InstallApps.Add(Convert.ToString(x));
            return p;
        }

        /// <summary>Записывает профиль в JSON, сохраняя порядок элементов как в каталоге.</summary>
        public string ToJson(Catalog catalog)
        {
            var o = new Dictionary<string, object>
            {
                ["name"] = Name,
                ["description"] = Description,
                ["order"] = Order,
                ["powerPlan"] = PowerPlan,
                ["tweaks"] = catalog.Tweaks.Where(t => Tweaks.Contains(t.Id)).Select(t => (object)t.Id).ToList(),
                ["removeApps"] = catalog.RemoveApps.Where(t => RemoveApps.Contains(t.Id)).Select(t => (object)t.Id).ToList(),
                ["installApps"] = catalog.InstallApps.Where(t => InstallApps.Contains(t.Id)).Select(t => (object)t.Id).ToList(),
            };
            if (!string.IsNullOrEmpty(ComputerName)) o["computerName"] = ComputerName;
            if (!string.IsNullOrEmpty(UserName)) o["userName"] = UserName;
            if (!string.IsNullOrEmpty(Windows)) o["windows"] = Windows;
            return Json.Write(o);
        }

        public KitProfile Clone()
        {
            var p = new KitProfile
            {
                FileId = FileId, Order = Order, Name = Name, Description = Description, PowerPlan = PowerPlan,
                ComputerName = ComputerName, UserName = UserName, Windows = Windows,
            };
            p.Tweaks.UnionWith(Tweaks);
            p.RemoveApps.UnionWith(RemoveApps);
            p.InstallApps.UnionWith(InstallApps);
            return p;
        }
    }
}
