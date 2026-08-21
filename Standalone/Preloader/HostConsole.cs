using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ScriptOne.Preloader
{
    /// <summary>
    /// Ein eigenes Konsolenfenster mit dem Protokoll - wie MelonLoader und BepInEx es zeigen,
    /// sobald man die Spiel-Exe startet.
    /// </summary>
    /// <remarks>
    /// WARUM DAS UEBERHAUPT GEHT, obwohl das Spiel eine GUI-Anwendung ist: Windows gibt jedem
    /// Prozess auf Wunsch eine Konsole per <c>AllocConsole</c>. Das ist ein reiner Win32-Aufruf
    /// und braucht Unity nicht - es laeuft also schon im Preloader, bevor die Spiel-Laufzeit
    /// existiert. Genau deshalb sieht man damit auch den START, nicht erst das Spiel.
    ///
    /// ⚠ DREI STOLPERSTELLEN, die man einzeln nicht sieht:
    ///
    /// 1. <c>AllocConsole</c> ALLEIN GENUEGT NICHT. .NET hat seine Standardausgabe zu diesem
    ///    Zeitpunkt bereits aufgeloest - auf „kein Ausgabegeraet". Ein spaeteres
    ///    <c>Console.WriteLine</c> geht dann ins Nichts, ohne Fehler. Der Griff auf die neue
    ///    Konsole muss ueber <c>CreateFile("CONOUT$")</c> geholt, per <c>SetStdHandle</c>
    ///    gesetzt UND <c>Console.SetOut</c> neu verdrahtet werden.
    /// 2. OHNE <c>AutoFlush</c> bleibt die letzte Zeile vor einem Absturz im Puffer - also
    ///    ausgerechnet die interessante.
    /// 3. Die Kodierung muss ausdruecklich auf UTF-8 gesetzt werden, sonst zerlegt die
    ///    Konsolen-Codepage jedes Nicht-ASCII-Zeichen.
    ///
    /// Es gibt hoechstens EINE Konsole je Prozess. Haengt schon eine dran (etwa weil das Spiel
    /// aus einer Eingabeaufforderung gestartet wurde), gibt <c>AllocConsole</c> false zurueck -
    /// das ist kein Fehler, wir benutzen dann die vorhandene.
    /// </remarks>
    internal static class HostConsole
    {
        private const int STD_OUTPUT_HANDLE = -11;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetConsoleTitleW(string lpConsoleTitle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        /// <summary>
        /// Fester Fenstertitel. ABSICHTLICH nicht konfigurierbar: das Fenster soll nach
        /// Namen auffindbar sein - fuer den Nutzer, der es unter zwanzig Fenstern sucht,
        /// und fuer jedes Werkzeug, das es per FindWindow ansprechen will.
        /// Eine Quelle - siehe <see cref="ScriptOne.Host.HostConfig.Fenstertitel"/>.
        /// </summary>
        internal const string Titel = ScriptOne.Host.HostConfig.Fenstertitel;

        private static bool _offen;

        internal static bool Offen { get { return _offen; } }

        /// <summary>
        /// Oeffnet die Konsole. Gibt bei Misserfolg eine Begruendung zurueck statt still zu
        /// scheitern - eine Konsole, die nicht kommt und nichts sagt, ist nicht von einer
        /// abgeschalteten zu unterscheiden.
        /// </summary>
        internal static bool Oeffne(out string grund)
        {
            grund = null;
            if (_offen) return true;
            try
            {
                // false heisst hier nicht zwangslaeufig Fehlschlag: es gibt vielleicht schon eine.
                var neu = AllocConsole();

                var griff = CreateFileW("CONOUT$",
                    0x40000000 /*GENERIC_WRITE*/ | 0x80000000 /*GENERIC_READ*/,
                    0x00000001 /*FILE_SHARE_READ*/ | 0x00000002 /*FILE_SHARE_WRITE*/,
                    IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);

                if (griff.IsInvalid)
                {
                    grund = "CONOUT$ could not be opened (win32 error " +
                            Marshal.GetLastWin32Error() + (neu ? "" : "; no console was allocated") + ")";
                    return false;
                }

                SetStdHandle(STD_OUTPUT_HANDLE, griff.DangerousGetHandle());

                var strom = new FileStream(griff, FileAccess.Write);
                var schreiber = new StreamWriter(strom, new UTF8Encoding(false)) { AutoFlush = true };
                System.Console.SetOut(schreiber);
                try { System.Console.OutputEncoding = Encoding.UTF8; } catch { /* nicht kritisch */ }
                try { SetConsoleTitleW(Titel); } catch { }

                _offen = true;
                return true;
            }
            catch (Exception ex)
            {
                grund = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>Schreibt eine bereits fertig formatierte Protokollzeile.</summary>
        internal static void Schreibe(string zeile)
        {
            if (!_offen) return;
            try { System.Console.Out.WriteLine(zeile); } catch { }
        }
    }
}
