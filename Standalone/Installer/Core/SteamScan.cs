using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ScriptOne.Setup
{
    /// <summary>Ein gefundenes Unity-Spiel: Ordner plus das, was der Befund darueber sagt.</summary>
    internal sealed class Fund
    {
        internal string Ordner;
        internal string Name;
        internal Befund Befund;
        public override string ToString()
        {
            return Name + "   [" + Befund.BackendText + "]" +
                   (Befund.Melon == MelonZustand.Aktiv ? "  MelonLoader" : "") +
                   (Befund.StandaloneDa ? "  ScriptOne" : "");
        }
    }

    /// <summary>
    /// Sucht installierte Unity-Spiele ueber Steams Bibliotheksverzeichnisse.
    /// </summary>
    /// <remarks>
    /// WARUM UEBERHAUPT: der Nutzer soll seinen Spielordner nicht suchen muessen, und wer ihn
    /// von Hand tippt, tippt irgendwann den falschen. Gefunden wird ueber
    /// steamapps\libraryfolders.vdf - dort stehen ALLE Bibliotheken, auch die auf anderen
    /// Laufwerken.
    ///
    /// ⚠ Das ist eine Bequemlichkeit, kein Verlass: Spiele ausserhalb von Steam findet es
    /// nicht, und deshalb bleibt die Pfadeingabe gleichberechtigt daneben stehen. Ein
    /// Suchlauf, der nichts findet, darf nicht wie ein Fehler aussehen.
    ///
    /// Erkannt wird ein Unity-Spiel am ORDNERINHALT, nicht an einer Namensliste - sonst
    /// funktioniert es nur fuer Spiele, die jemand vorher eingetragen hat.
    /// </remarks>
    internal static class SteamScan
    {
        internal static List<Fund> Finde()
        {
            var treffer = new List<Fund>();
            foreach (var lib in Bibliotheken())
            {
                var common = Path.Combine(lib, Path.Combine("steamapps", "common"));
                if (!Directory.Exists(common)) continue;
                string[] spiele;
                try { spiele = Directory.GetDirectories(common); } catch { continue; }
                foreach (var s in spiele)
                {
                    var echt = SpielWurzel(s);
                    if (echt == null) continue;
                    var b = GameDetect.Untersuche(echt);
                    if (b.Backend == Backend.Unbekannt) continue;
                    treffer.Add(new Fund { Ordner = echt, Name = Path.GetFileName(s), Befund = b });
                }
            }
            treffer.Sort((a, b2) => string.Compare(a.Name, b2.Name, StringComparison.OrdinalIgnoreCase));
            return treffer;
        }

        /// <summary>
        /// Der Ordner, in dem das Spiel WIRKLICH liegt - der Steam-Ordner ist es nicht immer.
        /// </summary>
        /// <remarks>
        /// ⚠ GEMESSEN, nicht vermutet: von zwei installierten Unity-Spielen lag EINES eine Ebene
        /// tiefer ("No Knock\windows_content\"). Eine Suche nur auf der obersten Ebene fand
        /// deshalb die Haelfte - und meldete das nicht als Luecke, sondern als Ergebnis.
        /// Ein Fund weniger sieht eben aus wie ein Spiel weniger.
        ///
        /// Eine Ebene reicht: tiefer verschachtelt hat es bisher keiner, und jede weitere Ebene
        /// kostet einen vollen Verzeichnisdurchlauf je Spiel.
        /// </remarks>
        private static string SpielWurzel(string ordner)
        {
            if (GameDetect.SiehtWieUnityAus(ordner)) return ordner;
            string[] unter;
            try { unter = Directory.GetDirectories(ordner); } catch { return null; }
            foreach (var u in unter)
                if (GameDetect.SiehtWieUnityAus(u)) return u;
            return null;
        }

        private static IEnumerable<string> Bibliotheken()
        {
            var gesehen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var wurzel in SteamWurzeln())
            {
                if (!gesehen.Add(wurzel)) continue;
                yield return wurzel;

                var vdf = Path.Combine(wurzel, Path.Combine("steamapps", "libraryfolders.vdf"));
                if (!File.Exists(vdf)) continue;
                string text;
                try { text = File.ReadAllText(vdf); } catch { continue; }

                // "path"    "D:\\SteamLibrary"
                foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
                {
                    var p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (Directory.Exists(p) && gesehen.Add(p)) yield return p;
                }
            }
        }

        private static IEnumerable<string> SteamWurzeln()
        {
            foreach (var k in new[] { @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" })
            {
                string p = null;
                try
                {
                    using (var r = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(k))
                        if (r != null) p = r.GetValue("InstallPath") as string;
                }
                catch { }
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) yield return p;
            }
            // Rueckfall fuer den Fall, dass die Registrierung nichts hergibt.
            foreach (var p in new[] { @"C:\Steam", @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
                if (Directory.Exists(p)) yield return p;
        }
    }
}
