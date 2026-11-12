using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WinFlash.Core
{
    /// <summary>
    /// Короткий текстовый код пресета, например «WF-1A2B3-C4D5E-…» (старые коды «RK-…» тоже принимаются).
    ///
    /// Внутри: версия, схема питания, три битовые маски (настройки, удаляемые и устанавливаемые
    /// приложения — по порядковым номерам в каталоге), имя ПК и имя пользователя, контрольная сумма.
    /// Кодировка — Crockford Base32: без похожих символов, регистр и путаница O/0, I/1 прощаются.
    /// Пароль в код не попадает никогда.
    ///
    /// Поэтому в каталогах новые элементы добавляются только в конец: иначе старые коды
    /// начнут означать другие галочки.
    /// </summary>
    public static class PresetCode
    {
        const byte Version = 1;
        const string Prefix = "WF";
        const string LegacyPrefix = "RK";
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        public static string Encode(KitProfile p, Catalog c)
        {
            var ms = new MemoryStream();
            ms.WriteByte(Version);
            ms.WriteByte((byte)Math.Max(0, Array.IndexOf(KitProfile.PowerPlans, p.PowerPlan)));
            WriteBits(ms, c.Tweaks, p.Tweaks);
            WriteBits(ms, c.RemoveApps, p.RemoveApps);
            WriteBits(ms, c.InstallApps, p.InstallApps);
            WriteString(ms, p.ComputerName);
            WriteString(ms, p.UserName);
            var body = ms.ToArray();
            var all = body.Concat(new[] { Crc8(body) }).ToArray();

            var text = ToBase32(all);
            var groups = Enumerable.Range(0, (text.Length + 4) / 5)
                .Select(i => text.Substring(i * 5, Math.Min(5, text.Length - i * 5)));
            return Prefix + "-" + string.Join("-", groups);
        }

        /// <summary>Разбирает код. Неизвестные (более новые) элементы каталога молча пропускаются.</summary>
        public static KitProfile Decode(string code, Catalog c)
        {
            if (string.IsNullOrWhiteSpace(code)) throw new FormatException("Код пустой.");
            var clean = new StringBuilder();
            foreach (char ch in code.Trim().ToUpperInvariant())
            {
                if (ch == '-' || char.IsWhiteSpace(ch)) continue;
                clean.Append(ch == 'O' ? '0' : ch == 'I' || ch == 'L' ? '1' : ch);
            }
            var s = clean.ToString();
            // «RK-» — коды, созданные до переименования программы (ReinstallKit). Формат тот же.
            var prefix = s.StartsWith(Prefix) ? Prefix : s.StartsWith(LegacyPrefix) ? LegacyPrefix : null;
            if (prefix == null) throw new FormatException("Это не код пресета WinFlash: он должен начинаться с «WF-».");
            var data = FromBase32(s.Substring(prefix.Length));
            if (data.Length < 3) throw new FormatException("Код слишком короткий: похоже, скопирован не полностью.");

            var body = data.Take(data.Length - 1).ToArray();
            if (Crc8(body) != data[data.Length - 1])
                throw new FormatException("Код повреждён: проверьте, что он скопирован полностью и без опечаток.");
            if (body[0] != Version)
                throw new FormatException("Код создан другой версией программы.");

            var r = new MemoryStream(body, 1, body.Length - 1);
            var p = new KitProfile { Name = "Из кода" };
            int plan = r.ReadByte();
            p.PowerPlan = plan >= 0 && plan < KitProfile.PowerPlans.Length ? KitProfile.PowerPlans[plan] : "balanced";
            ReadBits(r, c.Tweaks, p.Tweaks);
            ReadBits(r, c.RemoveApps, p.RemoveApps);
            ReadBits(r, c.InstallApps, p.InstallApps);
            p.ComputerName = ReadString(r);
            p.UserName = ReadString(r);
            return p;
        }

        // ------------------------------------------------------------ двоичные блоки

        static void WriteBits(Stream s, List<CatalogItem> items, HashSet<string> selected)
        {
            int count = items.Count;
            WriteVarInt(s, count);
            var bytes = new byte[(count + 7) / 8];
            foreach (var it in items)
                if (selected.Contains(it.Id)) bytes[it.Index / 8] |= (byte)(1 << (it.Index % 8));
            s.Write(bytes, 0, bytes.Length);
        }

        static void ReadBits(Stream s, List<CatalogItem> items, HashSet<string> selected)
        {
            int count = ReadVarInt(s);
            var bytes = new byte[(count + 7) / 8];
            if (s.Read(bytes, 0, bytes.Length) != bytes.Length) throw new FormatException("Код обрезан.");
            for (int i = 0; i < count && i < items.Count; i++)
                if ((bytes[i / 8] & (1 << (i % 8))) != 0) selected.Add(items[i].Id);
        }

        static void WriteString(Stream s, string value)
        {
            var b = Encoding.UTF8.GetBytes(value ?? "");
            WriteVarInt(s, b.Length);
            s.Write(b, 0, b.Length);
        }

        static string ReadString(Stream s)
        {
            if (s.Position >= s.Length) return "";
            int len = ReadVarInt(s);
            var b = new byte[len];
            if (s.Read(b, 0, len) != len) throw new FormatException("Код обрезан.");
            return Encoding.UTF8.GetString(b);
        }

        static void WriteVarInt(Stream s, int v)
        {
            while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
            s.WriteByte((byte)v);
        }

        static int ReadVarInt(Stream s)
        {
            int result = 0, shift = 0;
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) throw new FormatException("Код обрезан.");
                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift > 28) throw new FormatException("Код повреждён.");
            }
        }

        static byte Crc8(byte[] data)
        {
            byte crc = 0;
            foreach (var b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            }
            return crc;
        }

        // ------------------------------------------------------------ Base32 (Crockford)

        static string ToBase32(byte[] data)
        {
            var sb = new StringBuilder();
            int buffer = 0, bits = 0;
            foreach (var b in data)
            {
                buffer = (buffer << 8) | b; bits += 8;
                while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
            }
            if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
            return sb.ToString();
        }

        static byte[] FromBase32(string text)
        {
            var result = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (char ch in text)
            {
                int v = Alphabet.IndexOf(ch);
                if (v < 0) throw new FormatException($"В коде недопустимый символ «{ch}».");
                buffer = (buffer << 5) | v; bits += 5;
                if (bits >= 8) { result.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
            }
            return result.ToArray();
        }
    }
}
