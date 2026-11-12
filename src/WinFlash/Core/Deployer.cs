using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace WinFlash.Core
{
    /// <summary>
    /// Записывает выбор пользователя на флешку:
    ///   WinFlash\config\selected.json      — что применить после установки;
    ///   ventoy\script\winflash.xml         — файл автоустановки (на основе unattend\base.xml);
    ///   ventoy\ventoy.json                     — привязка этого файла к образам Windows.
    /// </summary>
    public sealed class Deployer
    {
        public const string UnattendNs = "urn:schemas-microsoft-com:unattend";
        public const string WcmNs = "http://schemas.microsoft.com/WMIConfig/2002/State";
        const string Marker = "WinFlash";
        public const string TemplateVentoyPath = "/ventoy/script/winflash.xml";
        public const string InstalledKitDir = @"C:\ProgramData\WinFlash";

        public string KitRoot { get; }
        /// <summary>Корень флешки Ventoy (папка, где лежит ventoy\), или null, если её нет.</summary>
        public string VentoyRoot { get; }

        public Deployer(string kitRoot)
        {
            KitRoot = kitRoot;
            var parent = Directory.GetParent(kitRoot.TrimEnd('\\'))?.FullName;
            if (parent != null && Directory.Exists(Path.Combine(parent, "ventoy"))) VentoyRoot = parent;
        }

        public string SelectedProfilePath => Path.Combine(KitRoot, "config", "selected.json");
        /// <summary>Свой шаблон из config\base.xml (не перезаписывается обновлением), иначе — нейтральный из unattend\.</summary>
        public string BaseTemplatePath
        {
            get
            {
                var own = Path.Combine(KitRoot, "config", "base.xml");
                return File.Exists(own) ? own : Path.Combine(KitRoot, "unattend", "base.xml");
            }
        }

        /// <summary>Образы Windows на флешке (относительные пути в стиле Ventoy: /ISO/Windows11.iso).</summary>
        public List<string> FindWindowsImages()
        {
            var result = new List<string>();
            if (VentoyRoot == null) return result;
            foreach (var file in Directory.EnumerateFiles(VentoyRoot, "*.iso", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name.IndexOf("win", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (name.IndexOf("hbcd", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("pe", StringComparison.OrdinalIgnoreCase) == 0) continue;
                result.Add(ToVentoyPath(file));
            }
            return result;
        }

        string ToVentoyPath(string file) => "/" + file.Substring(VentoyRoot.Length).TrimStart('\\').Replace('\\', '/');

        bool ImageExists(string ventoyPath)
            => !string.IsNullOrEmpty(ventoyPath) &&
               File.Exists(Path.Combine(VentoyRoot, ventoyPath.TrimStart('/').Replace('/', '\\')));

        public sealed class Result
        {
            public List<string> Written { get; } = new List<string>();
            public List<string> Warnings { get; } = new List<string>();
            public string UnattendPath { get; set; }
        }

        public Result Save(KitProfile profile, Catalog catalog, string password)
        {
            var r = new Result();

            Directory.CreateDirectory(Path.GetDirectoryName(SelectedProfilePath));
            File.WriteAllText(SelectedProfilePath, profile.ToJson(catalog), new UTF8Encoding(false));
            r.Written.Add(SelectedProfilePath);

            if (!File.Exists(BaseTemplatePath))
            {
                r.Warnings.Add("Нет шаблона автоустановки unattend\\base.xml — файл автоустановки не создан.");
                return r;
            }

            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(BaseTemplatePath);
            PatchUnattend(doc, profile, password);

            string xmlPath = VentoyRoot != null
                ? Path.Combine(VentoyRoot, "ventoy", "script", "winflash.xml")
                : Path.Combine(KitRoot, "config", "autounattend.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(xmlPath));
            using (var w = XmlWriter.Create(xmlPath, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
                doc.Save(w);
            r.Written.Add(xmlPath);
            r.UnattendPath = xmlPath;

            if (VentoyRoot != null)
            {
                var images = UpdateVentoyJson();
                if (images.Count == 0) r.Warnings.Add("На флешке не найдено образов Windows (*.iso с «win» в названии).");
                r.Written.Add(Path.Combine(VentoyRoot, "ventoy", "ventoy.json") + "  (" + string.Join(", ", images) + ")");
            }
            else
            {
                r.Warnings.Add("Флешка Ventoy не найдена рядом с программой. Файл автоустановки сохранён в config\\autounattend.xml: " +
                               "положите его в корень установочной флешки Windows.");
            }
            return r;
        }

        // ------------------------------------------------------------ autounattend.xml

        public static void PatchUnattend(XmlDocument doc, KitProfile profile, string password)
        {
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("u", UnattendNs);
            ns.AddNamespace("wcm", WcmNs);

            if (!string.IsNullOrWhiteSpace(profile.ComputerName))
                foreach (XmlElement e in doc.SelectNodes("//u:settings[@pass='specialize']/u:component[@name='Microsoft-Windows-Shell-Setup']/u:ComputerName", ns))
                    e.InnerText = profile.ComputerName.Trim();

            var shell = (XmlElement)doc.SelectSingleNode("//u:settings[@pass='oobeSystem']/u:component[@name='Microsoft-Windows-Shell-Setup']", ns);
            if (shell != null)
            {
                if (!string.IsNullOrWhiteSpace(profile.UserName))
                {
                    foreach (XmlElement e in shell.SelectNodes("u:UserAccounts/u:LocalAccounts/u:LocalAccount/u:Name | u:AutoLogon/u:Username", ns))
                        e.InnerText = profile.UserName.Trim();
                }
                if (password != null)
                {
                    foreach (XmlElement e in shell.SelectNodes("u:UserAccounts/u:LocalAccounts/u:LocalAccount/u:Password/u:Value | u:AutoLogon/u:Password/u:Value", ns))
                        e.InnerText = password;
                }

                // Запуск применения при первом входе.
                var flc = Child(doc, shell, "FirstLogonCommands");
                RemoveMarked(flc, "SynchronousCommand", ns);
                var cmd = AddCommand(doc, flc, "SynchronousCommand", ns);
                Child(doc, cmd, "CommandLine").InnerText =
                    $"cmd.exe /c start \"\" \"{InstalledKitDir}\\WinFlash.exe\" --apply";
            }

            // Копирование набора с флешки на диск ещё во время установки (флешка тогда точно подключена).
            var spec = (XmlElement)doc.SelectSingleNode("//u:settings[@pass='specialize']", ns);
            if (spec != null)
            {
                var deploy = (XmlElement)spec.SelectSingleNode("u:component[@name='Microsoft-Windows-Deployment']", ns);
                if (deploy == null)
                {
                    deploy = doc.CreateElement("component", UnattendNs);
                    deploy.SetAttribute("name", "Microsoft-Windows-Deployment");
                    deploy.SetAttribute("processorArchitecture", "amd64");
                    deploy.SetAttribute("publicKeyToken", "31bf3856ad364e35");
                    deploy.SetAttribute("language", "neutral");
                    deploy.SetAttribute("versionScope", "nonSxS");
                    spec.AppendChild(deploy);
                }
                var rs = Child(doc, deploy, "RunSynchronous");
                RemoveMarked(rs, "RunSynchronousCommand", ns);
                var cmd = AddCommand(doc, rs, "RunSynchronousCommand", ns);
                // Путь ограничен 259 символами — команда намеренно короткая.
                Child(doc, cmd, "Path").InnerText =
                    "cmd.exe /c \"for %d in (D E F G H I J K L M N O P Q R S T U V W X Y Z) do @if exist %d:\\WinFlash\\config\\selected.json " +
                    $"robocopy %d:\\WinFlash {InstalledKitDir} /E /R:1 /W:1 /NP /LOG+:C:\\Windows\\Setup\\WinFlash-copy.log\"";
            }
        }

        static XmlElement Child(XmlDocument doc, XmlElement parent, string name)
        {
            foreach (XmlNode n in parent.ChildNodes)
                if (n is XmlElement e && e.LocalName == name) return e;
            var created = doc.CreateElement(name, UnattendNs);
            parent.AppendChild(created);
            return created;
        }

        static void RemoveMarked(XmlElement parent, string name, XmlNamespaceManager ns)
        {
            foreach (var e in parent.SelectNodes($"u:{name}[u:Description='{Marker}']", ns).Cast<XmlElement>().ToList())
                parent.RemoveChild(e);
        }

        static XmlElement AddCommand(XmlDocument doc, XmlElement parent, string name, XmlNamespaceManager ns)
        {
            int max = 0;
            foreach (XmlElement o in parent.SelectNodes($"u:{name}/u:Order", ns))
                if (int.TryParse(o.InnerText, out var v)) max = Math.Max(max, v);
            var cmd = doc.CreateElement(name, UnattendNs);
            var action = doc.CreateAttribute("wcm", "action", WcmNs);
            action.Value = "add";
            cmd.Attributes.Append(action);
            parent.AppendChild(cmd);
            Child(doc, cmd, "Order").InnerText = (max + 1).ToString();
            Child(doc, cmd, "Description").InnerText = Marker;
            return cmd;
        }

        // ------------------------------------------------------------ ventoy.json

        /// <summary>Добавляет наш шаблон первым в список auto_install для каждого образа Windows.</summary>
        List<string> UpdateVentoyJson()
        {
            var path = Path.Combine(VentoyRoot, "ventoy", "ventoy.json");
            Dictionary<string, object> root;
            if (File.Exists(path))
            {
                var backup = path + ".before-winflash";
                if (!File.Exists(backup)) File.Copy(path, backup);
                root = Json.Parse(Catalog.ReadText(path)).Obj() ?? new Dictionary<string, object>();
            }
            else root = new Dictionary<string, object>();

            if (!(root.TryGetValue("auto_install", out var ai) && ai is List<object> list))
                root["auto_install"] = list = new List<object>();

            // Образ перенесли или удалили — убираем из его записи наш шаблон (чужие шаблоны не трогаем).
            foreach (var stale in list.Select(x => x.Obj()).Where(x => x != null && !ImageExists(x.Str("image"))).ToList())
            {
                if (stale.TryGetValue("template", out var st) && st is List<object> stl)
                    stl.RemoveAll(x => string.Equals(Convert.ToString(x), TemplateVentoyPath, StringComparison.OrdinalIgnoreCase));
                if (!(stale.TryGetValue("template", out st) && st is List<object> left && left.Count > 0)) list.Remove(stale);
            }

            var images = FindWindowsImages();
            foreach (var image in images)
            {
                var entry = list.Select(x => x.Obj()).FirstOrDefault(x => x != null &&
                    string.Equals(x.Str("image"), image, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    entry = new Dictionary<string, object> { ["image"] = image, ["template"] = new List<object>() };
                    list.Add(entry);
                }
                var templates = entry.TryGetValue("template", out var t) && t is List<object> tl ? tl : new List<object>();
                templates.RemoveAll(x => string.Equals(Convert.ToString(x), TemplateVentoyPath, StringComparison.OrdinalIgnoreCase));
                templates.Insert(0, TemplateVentoyPath);
                entry["template"] = templates;
            }
            // Понятное имя для нашего WinPE-образа в меню Ventoy.
            var peIso = Directory.EnumerateFiles(VentoyRoot, "WinFlash-Setup.iso", SearchOption.AllDirectories).FirstOrDefault();
            if (peIso != null)
            {
                var pePath = ToVentoyPath(peIso);
                if (!(root.TryGetValue("menu_alias", out var ma) && ma is List<object> aliases))
                    root["menu_alias"] = aliases = new List<object>();
                aliases.RemoveAll(x => x.Str("image").EndsWith("/WinFlash-Setup.iso", StringComparison.OrdinalIgnoreCase));
                aliases.Insert(0, new Dictionary<string, object>
                {
                    ["image"] = pePath,
                    ["alias"] = "WinFlash — настроить и установить Windows",
                });
            }

            File.WriteAllText(path, Json.Write(root), new UTF8Encoding(false));
            return images;
        }
    }
}
