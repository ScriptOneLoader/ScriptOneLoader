using System;
using System.Globalization;
using System.IO;
using System.Text;
using ScriptOne.Host;

namespace ScriptOne.Preloader
{
    /// <summary>
    /// Ausgabe ohne Loader. Es gibt hier keinen MelonLogger und keine Konsole -
    /// also schreibt der Wirt in eine eigene Datei unter ScriptOne\.
    /// </summary>
    /// <remarks>
    /// Bewusst so frueh wie moeglich benutzbar: der Preloader laeuft, bevor Unity
    /// steht, und ein Fehler DORT ist sonst voellig unsichtbar. Jede Zeile wird
    /// sofort geschrieben (kein Puffer), damit ein Absturz die letzten Zeilen nicht
    /// mitnimmt - genau die sind die interessanten.
    /// </remarks>
    internal sealed class FileLog : IScriptLog
    {
        private static readonly object Sperre = new object();
        private readonly string _pfad;

        /// <summary>Wie viele abgeschlossene Laeufe aufgehoben werden.</summary>
        internal const int Vorlaeufe = 5;

        /// <param name="pfad">Das Protokoll des LAUFENDEN Starts, in der ScriptOne-Wurzel.</param>
        /// <param name="archiv">Ordner fuer die abgeschlossenen Laeufe (<c>logs\</c>).</param>
        /// <remarks>
        /// AUFTEILUNG WIE BEI MELONLOADER: der aktuelle Lauf liegt oben und ist mit einem
        /// Griff zu finden, die aelteren stehen daneben im Ordner. Wer eine Absturzmeldung
        /// sucht, will nicht erst nach Zeitstempeln sortieren.
        ///
        /// ⚠ Die Rotation laeuft RUECKWAERTS (5 loeschen, 4→5, 3→4, ...). Vorwaerts wuerde
        /// jede Datei die naechste ueberschreiben und am Ende blieben fuenf Kopien desselben
        /// Laufs - das sieht in der Ordneransicht voellig richtig aus.
        ///
        /// Die ganze Rotation ist Komfort: schlaegt sie fehl, wird trotzdem protokolliert.
        /// Ein Wirt, der wegen seines eigenen Protokolls nicht startet, waere absurd.
        /// </remarks>
        internal FileLog(string pfad, string archiv)
        {
            _pfad = pfad;
            try
            {
                var dir = Path.GetDirectoryName(pfad);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                try { Rotiere(pfad, archiv); }
                catch { /* Rotation darf den Start nie verhindern */ }

                File.WriteAllText(pfad,
                    "ScriptOne standalone - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>Schiebt die Vorlaeufe eine Stelle weiter und legt den letzten Lauf als .1 ab.</summary>
        private static void Rotiere(string pfad, string archiv)
        {
            if (!File.Exists(pfad)) return;               // erster Start - nichts aufzuheben
            if (!Directory.Exists(archiv)) Directory.CreateDirectory(archiv);

            var name = Path.GetFileName(pfad);            // ScriptOne.log
            Func<int, string> stelle = n => Path.Combine(archiv, name + "." + n.ToString(CultureInfo.InvariantCulture));

            // Rueckwaerts, sonst frisst jede Datei ihren Nachfolger.
            if (File.Exists(stelle(Vorlaeufe))) File.Delete(stelle(Vorlaeufe));
            for (var n = Vorlaeufe - 1; n >= 1; n--)
                if (File.Exists(stelle(n))) File.Move(stelle(n), stelle(n + 1));

            File.Move(pfad, stelle(1));
        }

        internal void Write(string stufe, string text)
        {
            var zeile = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + stufe + "] " + text;
            lock (Sperre)
            {
                try { File.AppendAllText(_pfad, zeile + Environment.NewLine, new UTF8Encoding(false)); }
                catch { }
            }
            // Die eigene Konsole, falls sie offen ist. Ohne sie geht Console.WriteLine in
            // einer GUI-Anwendung ins Leere - deshalb hier ueber HostConsole, das den
            // Zustand kennt, statt blind zu schreiben.
            HostConsole.Schreibe(zeile);
        }

        public void Info(string message)  { Write("INFO", message); }
        public void Warn(string message)  { Write("WARN", message); }
        public void Error(string message) { Write("ERR ", message); }

        /// <summary>Schreibt eine Ausnahme MIT ihrer inneren Kette.</summary>
        /// <remarks>
        /// ⚠ VORHER STAND HIER NUR DIE AEUSSERE. Bei einer TypeInitializationException ist das
        /// wertlos: sie nennt nur den Typ, dessen Initialisierung scheiterte - WARUM steht erst
        /// eine oder zwei Ebenen tiefer. Gemessen 2026-08-19 im Mono-Standalone: das Log sagte
        /// "The type initializer for 'UnityEngine.Object' threw an exception" und sonst nichts,
        /// waehrend die eigentliche Ursache in der inneren Ausnahme steckte. Genau derselbe
        /// blinde Fleck wie im Plugin-Zweig, dort schon behoben.
        /// </remarks>
        internal void Exception(string wo, Exception ex)
        {
            var tiefe = 0;
            for (var e = ex; e != null; e = e.InnerException, tiefe++)
            {
                var vorsatz = tiefe == 0 ? wo + ": " : new string(' ', 2 * tiefe) + "-> caused by: ";
                Write("ERR ", vorsatz + e.GetType().FullName + ": " + e.Message);
                if (e.StackTrace != null) Write("ERR ", e.StackTrace);
                if (tiefe > 6) { Write("ERR ", "  (weitere innere Ausnahmen abgeschnitten)"); break; }
            }
        }
    }
}
