using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ScriptOne.Host
{
    /// <summary>
    /// Gedaechtnis fuer Skripte: s1.get(key, default), s1.set(key, value), s1.save().
    /// Eine Datei je Skript, damit ein kaputtes Skript kein fremdes Gedaechtnis beschaedigt.
    /// </summary>
    /// <remarks>
    /// FORMAT BEWUSST EIGEN UND WINZIG: eine Zeile "typ|schluessel=wert". Kein JSON, weil
    /// dafuer eine Fremdbibliothek noetig waere, und kein MelonPreferences, weil das die
    /// Werte einer FESTEN Struktur zuordnet - ein Skript legt seine Schluessel aber zur
    /// Laufzeit an.
    ///
    /// NUR FLACHE WERTE: Zahl, Zeichenkette, Wahrheitswert. Dieselbe Grenzregel wie ueberall
    /// im Wirt; eine Tabelle zu serialisieren waere der erste Schritt zu einem Objektgraphen.
    ///
    /// ZAHLEN WERDEN IMMER INVARIANT GESCHRIEBEN UND GELESEN. Das ist keine Stilfrage:
    /// gemessen macht MoonSharps '..' auf einem de-DE-Rechner aus 1234.5 die Zeichenkette
    /// "1234,5", und eine Datei, die einmal mit Komma und einmal mit Punkt geschrieben wurde,
    /// liest sich auf dem naechsten Rechner still falsch ein.
    /// </remarks>
    internal sealed class ScriptStore
    {
        private readonly string _pfad;
        private readonly Dictionary<string, object> _werte =
            new Dictionary<string, object>(StringComparer.Ordinal);
        private bool _schmutzig;

        internal ScriptStore(string ordner, string skriptName)
        {
            var sicher = MakeSafe(skriptName);
            _pfad = Path.Combine(ordner, sicher + ".state");
            Load();
        }

        internal string Path_ { get { return _pfad; } }
        internal int Count { get { return _werte.Count; } }
        internal bool Dirty { get { return _schmutzig; } }

        private static string MakeSafe(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        internal object Get(string key, object fallback)
        {
            object v;
            return _werte.TryGetValue(key, out v) ? v : fallback;
        }

        internal void Set(string key, object value)
        {
            if (string.IsNullOrEmpty(key)) return;
            _werte[key] = value;
            _schmutzig = true;
        }

        // ---------------------------------------------------------------- Datei

        private void Load()
        {
            try
            {
                if (!File.Exists(_pfad)) return;
                foreach (var zeile in File.ReadAllLines(_pfad, Encoding.UTF8))
                {
                    if (zeile.Length < 3 || zeile[0] == '#') continue;
                    var trenner = zeile.IndexOf('|');
                    var gleich = zeile.IndexOf('=', trenner + 1);
                    if (trenner < 0 || gleich < 0) continue;

                    var typ = zeile.Substring(0, trenner);
                    var key = zeile.Substring(trenner + 1, gleich - trenner - 1);
                    var roh = zeile.Substring(gleich + 1);

                    if (typ == "n")
                    {
                        double d;
                        if (double.TryParse(roh, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                            _werte[key] = d;
                    }
                    else if (typ == "b") _werte[key] = roh == "1";
                    else if (typ == "s") _werte[key] = Unescape(roh);
                }
            }
            catch
            {
                // Ein beschaedigtes Gedaechtnis darf das Skript nicht am Start hindern -
                // es startet dann eben ohne. Gemeldet wird es vom Aufrufer.
                _werte.Clear();
            }
        }

        /// <summary>Schreibt, wenn sich etwas geaendert hat. Gibt zurueck, ob geschrieben wurde.</summary>
        internal bool Save()
        {
            if (!_schmutzig) return false;
            var dir = Path.GetDirectoryName(_pfad);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.Append("# ScriptOne state - one line per key, type|key=value\n");
            foreach (var kv in _werte)
            {
                if (kv.Value is bool) sb.Append("b|").Append(kv.Key).Append('=').Append(((bool)kv.Value) ? "1" : "0");
                else if (kv.Value is double) sb.Append("n|").Append(kv.Key).Append('=')
                        .Append(((double)kv.Value).ToString("R", CultureInfo.InvariantCulture));
                else sb.Append("s|").Append(kv.Key).Append('=').Append(Escape(Convert.ToString(kv.Value, CultureInfo.InvariantCulture)));
                sb.Append('\n');
            }

            // Erst daneben schreiben, dann ersetzen: ein Absturz mitten im Schreiben darf
            // nicht die vorhandenen Daten halbieren.
            var tmp = _pfad + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(_pfad)) File.Delete(_pfad);
            File.Move(tmp, _pfad);
            _schmutzig = false;
            return true;
        }

        /// <summary>Macht aus einem Wert eine Zeile - und aus einer Zeile wieder den Wert.</summary>
        /// <remarks>
        /// ⚠ HIER STAND EINE SELBSTABBILDUNG: beide Seiten von .Replace("\n", "\n") sind in C#
        /// ein ECHTER Zeilenumbruch, die Zeile tat also nichts. Gemeint war Umbruch -> die zwei
        /// Zeichen Backslash+n. Weil die Backslash-Haelfte daneben korrekt ist, sah das Paar
        /// plausibel aus und rutschte beim Lesen durch.
        ///
        /// Das Format ist EINE ZEILE JE SCHLUESSEL: ein per s1.set gespeicherter Wert mit
        /// Umbruch ging damit roh in die Datei und zerlegte sie beim naechsten Laden - das ist
        /// Datenverlust im Gedaechtnis eines Nutzerskripts, kein Schoenheitsfehler.
        ///
        /// Unescape laeuft EINMAL von links nach rechts. Zwei nacheinander ausgefuehrte Replace
        /// waeren wieder falsch: die Eingabe \n (maskierter Backslash, dann ein n) wuerde sonst
        /// je nach Reihenfolge zu einem Umbruch statt zu Backslash+n.
        /// </remarks>
        private static string Escape(string s)
        {
            return s == null ? string.Empty : s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "");
        }

        private static string Unescape(string s)
        {
            if (s == null) return string.Empty;
            var b = new System.Text.StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { b.Append(s[i]); continue; }
                i++;
                if (s[i] == 'n')          b.Append('\n');
                else if (s[i] == '\\') b.Append('\\');
                else { b.Append('\\'); b.Append(s[i]); }
            }
            return b.ToString();
        }
    }
}
