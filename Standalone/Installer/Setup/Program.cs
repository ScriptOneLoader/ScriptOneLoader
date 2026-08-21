using System;
using System.IO;

namespace ScriptOne.Setup
{
    /// <summary>
    /// Das Setup, das IN der Auslieferungs-ZIP liegt: entpacken, einmal doppelklicken, fertig.
    /// </summary>
    /// <remarks>
    /// ENTWURF: Es nimmt seinen EIGENEN Ordner als Spielordner. Das ist die ganze Bedienung -
    /// wer die ZIP ins Spielverzeichnis entpackt, hat damit schon alles gesagt. Kein Pfaddialog,
    /// keine Auswahl, keine Kommandozeile.
    ///
    /// ⚠ Fenster offen halten. Ein Doppelklick auf eine Konsolenanwendung schliesst das Fenster,
    /// sobald Main zurueckkehrt - der Nutzer saehe das Ergebnis nie. Deshalb am Ende warten,
    /// aber NUR, wenn wir die Konsole selbst besitzen (sonst haengt ein Skriptaufruf).
    ///
    /// Alle Ausgaben ENGLISCH.
    /// </remarks>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.Title = "ScriptOne Setup";
            var spiel = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            var modus = Modus.Auto;
            var trotzdem = false;
            var still = false;
            var nurStatus = false;

            foreach (var a in args)
            {
                var l = a.ToLowerInvariant();
                if (l == "--remove" || l == "-r")      modus = Modus.Entfernen;
                else if (l == "--plugin" || l == "-p") modus = Modus.Plugin;
                else if (l == "--standalone" || l == "-s") modus = Modus.Standalone;
                else if (l == "--force" || l == "-f")  trotzdem = true;
                else if (l == "--quiet" || l == "-q")  still = true;
                else if (l == "--status")             { nurStatus = true; still = true; }
                else if (l == "--help" || l == "-h" || l == "/?") { Hilfe(); return 0; }
                else if (Directory.Exists(a))          spiel = a.TrimEnd('\\');
            }

            // Die Nutzlast liegt in EINEM Unterordner neben dem Setup, nicht lose daneben:
            // sonst bleiben nach dem Entpacken licenses\ und setup-files\ im Spielordner
            // liegen und sehen aus, als gehoerten sie dem Spiel.
            var beipack = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Pfade.BeipackOrdner);
            if (Directory.Exists(beipack)) Installer.Quelle = beipack;

            Kopf();
            Console.WriteLine("  Game folder : " + spiel);

            var b = GameDetect.Untersuche(spiel);
            Console.WriteLine("  Backend     : " + b.BackendText);
            Console.WriteLine("  MelonLoader : " + b.MelonText);
            if (b.BepInEx) Console.WriteLine("  BepInEx     : present");
            Console.WriteLine("  ScriptOne   : standalone " + (b.StandaloneDa ? "yes" : "no") +
                              " | plugin " + (b.PluginDa ? b.PluginArt.ToLowerInvariant() : "no"));
            if (b.PluginDa) Console.WriteLine("                found at " + b.PluginPfad);

            // ⚠ NUR NACHSEHEN, NICHTS TUN. Ohne diesen Ausgang gibt es keine Moeglichkeit,
            //   die ERKENNUNG allein zu pruefen - jeder Aufruf haette installiert und damit
            //   genau den Zustand erzeugt, den er messen soll. Ein Pruefer, der seine eigene
            //   Vorbedingung herstellt, kann den Fall "meldet faelschlich nichts" nicht sehen.
            if (nurStatus) return Ende(0, still);
            Console.WriteLine();

            if (b.Backend == Backend.Unbekannt)
            {
                Fehler("This folder does not look like a Unity game.");
                Console.WriteLine("  Unzip the package INTO the game folder - the one that contains the game's .exe,");
                Console.WriteLine("  a <name>_Data folder and UnityPlayer.dll - and run this again from there.");
                return Ende(2, still);
            }

            // ⚠ HIER STAND EINE RUECKFRAGE, und sie war der Fehler: bei aktivem MelonLoader
            // bot das Setup an, ihn ABZUSCHALTEN - und wer mit 'N' antwortete, bekam gar nichts.
            // Nutzlos genau im haeufigsten Fall. Jetzt waehlt Auto den Weg, der hier funktioniert:
            // MelonLoader aktiv -> Plugin, sonst Standalone. Wer den anderen Weg WILL, sagt es
            // mit --standalone bzw. --plugin.
            if (modus == Modus.Standalone && b.Melon == MelonZustand.Aktiv && !trotzdem)
            {
                Console.WriteLine("  You asked for the standalone host, but MelonLoader is active here.");
                Console.WriteLine("  Both replace the same import entry in UnityPlayer.dll and neither chains the");
                Console.WriteLine("  other - exactly one survives. Installing standalone switches MelonLoader OFF,");
                Console.WriteLine("  and all your other mods with it. It is set aside, not deleted.");
                Console.WriteLine();
                if (!Frage("  Switch MelonLoader off?")) return Ende(1, still);
                trotzdem = true;
                Console.WriteLine();
            }

            // ⚠ NICHT ungeschuetzt: eine IO-Ausnahme beendete das Setup vorher mit einem
            //   .NET-Stacktrace in der Sprache des Systems - fuer einen Mod-Nutzer ist das
            //   kein Ergebnis, sondern ein Absturz. Das Fenster zeigt jetzt, was schiefging
            //   und wie man den Spielordner wieder sauber bekommt. (Gemessen an einem sehr
            //   langen Pfad: ueber MAX_PATH wirft File.Copy DirectoryNotFoundException.)
            Ergebnis e;
            try { e = Installer.Fuehre(spiel, modus, trotzdem); }
            catch (Exception ex)
            {
                Fehler(ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine();
                Console.WriteLine("  Nothing depends on a half-finished install: delete " + Pfade.DoorstopDll +
                                  " and " + Pfade.DoorstopCfg);
                Console.WriteLine("  from the game folder and it starts normally again.");
                if (spiel.Length > 150)
                    Console.WriteLine("  This path is very long - Windows stops at 260 characters.");
                return Ende(1, still);
            }
            foreach (var z in e.Zeilen) Console.WriteLine("  " + z);

            if (!e.Erfolg)
            {
                Console.WriteLine();
                Fehler(e.Abbruch);
                Console.WriteLine();
                Console.WriteLine("  Nothing was left in a half-installed state that would stop the game:");
                Console.WriteLine("  delete " + Pfade.DoorstopDll + " and " + Pfade.DoorstopCfg +
                                  " from the game folder and it starts normally again.");
                return Ende(1, still);
            }

            // ⚠ AUFRAEUMEN. Der Beipackordner blieb bisher im Spielordner liegen und sah
            // aus, als gehoerte er dem Spiel - der Autor nannte die Ordnerstruktur danach
            // zu Recht chaotisch. Er wird NUR bei Erfolg entfernt: nach einem Abbruch
            // braucht ihn der naechste Versuch.
            var beipackOrdner = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Pfade.BeipackOrdner);
            if (Directory.Exists(beipackOrdner))
            {
                // ⚠ Kein stilles catch: als das Aufraeumen zum ersten Mal fehlschlug, blieb der
                // Ordner liegen UND das Setup schwieg dazu - der Autor sah nur das Ergebnis und
                // musste raten. Ein Aufraeumschritt, der scheitern darf, muss es sagen.
                try { Directory.Delete(beipackOrdner, true); Console.WriteLine("  cleaned up: " + Pfade.BeipackOrdner + Pfade.T); }
                catch (Exception ex)
                {
                    Console.WriteLine("  note: could not remove " + Pfade.BeipackOrdner + Pfade.T +
                                      " (" + ex.GetType().Name + ": " + ex.Message + ")");
                    Console.WriteLine("  it is only the setup payload - delete it by hand, nothing depends on it.");
                }
            }

            Console.WriteLine();
            Farbe(ConsoleColor.Green, "  Done.");
            Console.WriteLine();
            // ⚠ KEINE unbedingte Zusage. Diese Zeilen versprachen Ordner, die erst der WIRT
            //   beim Start anlegt - startet er nicht, sucht der Nutzer nach etwas, das nie
            //   kommt, und haelt die Installation fuer kaputt. Genau so passiert am
            //   2026-08-19: der Wirt starb an einer Assembly, die Installation war korrekt.
            Console.WriteLine("  Now start the game once. If ScriptOne comes up, you will find:");
            Console.WriteLine("    " + Pfade.Wurzel + Pfade.T + Pfade.Doku + Pfade.T + "   what your scripts can call IN THIS GAME");
            Console.WriteLine("    " + Pfade.Wurzel + Pfade.T + Pfade.Cfg + "  settings, every option explained inside");
            Console.WriteLine("    " + (e.Gewaehlt == Modus.Plugin
                ? "MelonLoader's own log carries ScriptOne's lines"
                : Pfade.Wurzel + Pfade.T + Pfade.LogDatei + "        the log of that run"));
            Console.WriteLine();
            Console.WriteLine("  Put your .lua files into " + Pfade.SkriptOrdner + Pfade.T + ".");
            Console.WriteLine();
            Console.WriteLine("  If none of that appears, ScriptOne did not start - the reason is then in");
            Console.WriteLine("  " + (e.Gewaehlt == Modus.Plugin
                ? Pfade.MelonOrdner + Pfade.T + "Latest.log"
                : Pfade.Wurzel + Pfade.T + Pfade.LogDatei) + ", not in this installer.");
            Console.WriteLine();
            if (e.Gewaehlt == Modus.Standalone)
            {
                Console.WriteLine("  If the game does not start: delete " + Pfade.DoorstopDll + " and " +
                                  Pfade.DoorstopCfg + " from");
                Console.WriteLine("  the game folder. That always works and undoes everything that matters.");
                Console.WriteLine();
            }
            // Der Hinweis gehoert in BEIDE Zweige - im Plugin-Zweig blieben die Setup-Dateien
            // sonst kommentarlos liegen, und genau das war die Beschwerde.
            Console.WriteLine("  You can delete " + Path.GetFileName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) + " and READ-ME-FIRST.txt now.");
            return Ende(0, still);
        }

        private static void Kopf()
        {
            Console.WriteLine();
            Farbe(ConsoleColor.Cyan, "  ScriptOne - setup");
            // ⚠ NICHT "with or without a mod loader": dieses Setup INSTALLIERT einen Lader
            //   (UnityDoorstop), wenn kein anderer da ist. Die alte Zeile las sich, als brauche
            //   es gar keinen - und widersprach dem, was der Nutzer gleich danach sieht.
            Console.WriteLine("  Lua scripting for Unity games by Virtunerd. Runs under MelonLoader if you have it,");
            Console.WriteLine("  and brings its own loader if you do not.");
            Console.WriteLine();
        }

        private static void Hilfe()
        {
            Kopf();
            Console.WriteLine("  Unzip into the game folder and run this once. No arguments needed.");
            Console.WriteLine();
            Console.WriteLine("    --plugin    install as a MelonLoader plugin instead of standalone");
            Console.WriteLine("    --remove    remove the loader files (scripts and state stay)");
            Console.WriteLine("    --force     go ahead even when another loader is in the way");
            Console.WriteLine("    --status    only report what is there, install nothing");
            Console.WriteLine("    --quiet     do not wait for a key press at the end");
            Console.WriteLine("    <path>      use this folder instead of the one this program is in");
            Console.WriteLine();
        }

        private static bool Frage(string text)
        {
            Console.Write(text + " [y/N] ");
            var k = Console.ReadLine();
            return k != null && (k.Trim().ToLowerInvariant() == "y" || k.Trim().ToLowerInvariant() == "yes");
        }

        private static void Fehler(string t) { Farbe(ConsoleColor.Red, "  " + t); }

        private static void Farbe(ConsoleColor c, string t)
        {
            var alt = Console.ForegroundColor;
            try { Console.ForegroundColor = c; Console.WriteLine(t); }
            finally { Console.ForegroundColor = alt; }
        }

        /// <remarks>
        /// ⚠ Nur warten, wenn das Fenster UNS gehoert. Wird das Setup aus einer Shell oder einem
        /// Skript gerufen, wuerde ein ReadKey den Aufrufer haengen lassen - und in einem
        /// Dienstkontext gibt es gar keine Eingabe, dann wirft es.
        /// </remarks>
        private static int Ende(int code, bool still)
        {
            if (still) return code;
            try
            {
                if (Console.IsInputRedirected) return code;
                Console.WriteLine();
                Console.Write("  Press any key to close . . . ");
                Console.ReadKey(true);
                Console.WriteLine();
            }
            catch { }
            return code;
        }
    }
}
