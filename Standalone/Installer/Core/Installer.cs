using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ScriptOne.Setup
{
    internal enum Modus { Auto, Standalone, Plugin, BepPlugin, Entfernen }

    /// <summary>Wie ein Wirt abgelegt wird.</summary>
    /// <remarks>
    /// ⚠ DAS WAR EIN BOOL UND DAMIT EINE FRAGE ZU WENIG. "scharf" hiess gleichzeitig "lege
    /// den Wirt ab" UND "mach den Lader scharf" - und im Uebernahme-Weg (BepInEx) stimmt nur
    /// die erste Haelfte: der Einstiegspunkt gehoert dort schon uns, die Konfiguration ist
    /// geschrieben, ein zweiter Proxy waere falsch. Mit dem bool lief der Zweig in die
    /// Vorsorge-Ablage und meldete "loader installed:  + doorstop_config.ini" - mit LEEREM
    /// Namen, weil ProxyName neben BepInEx null gibt. Gefangen vom Auslieferungspruefer
    /// 2026-08-20.
    /// </remarks>
    internal enum Ablage
    {
        /// <summary>Wirt ablegen UND den Lader scharf machen (Standalone).</summary>
        Scharf,
        /// <summary>Wirt ablegen, Lader nur als Sicherheitsnetz (Plugin-Weg).</summary>
        Vorsorge,
        /// <summary>Nur den Wirt - der Einstiegspunkt gehoert bereits uns (Uebernahme).</summary>
        WirtNur
    }

    /// <summary>Was ein Lauf getan und gefunden hat. Der Aufrufer entscheidet, wie er es zeigt.</summary>
    internal sealed class Ergebnis
    {
        internal readonly List<string> Zeilen = new List<string>();
        internal bool Erfolg = true;
        internal string Abbruch;

        /// <summary>Welcher Weg tatsaechlich gegangen wurde - Auto loest sich hier drin auf.</summary>
        /// <remarks>
        /// Ohne das raet der Aufrufer: er kennt nur 'Auto' und schrieb im Plugin-Fall einen
        /// Schlusstext ueber das Standalone-Protokoll, das es dort gar nicht gibt.
        /// </remarks>
        internal Modus Gewaehlt = Modus.Auto;

        internal void Sag(string z)     { Zeilen.Add(z); }
        internal bool Stop(string grund) { Erfolg = false; Abbruch = grund; return false; }
    }

    /// <summary>
    /// Installiert, wechselt den Zweig oder entfernt - dieselbe Logik fuer Konsolen-Setup und
    /// Fenster-Installer.
    /// </summary>
    /// <remarks>
    /// ⚠ WARUM DER ZWEIG UEBERHAUPT GEWAEHLT WERDEN MUSS: MelonLoader und UnityDoorstop
    /// ersetzen DENSELBEN Importeintrag in UnityPlayer.dll (kernel32!GetProcAddress), und
    /// keiner von beiden verkettet den Vorgaenger. Gemessen: UnityPlayer.dll importiert
    /// GetProcAddress genau EINMAL - ein Slot, ein Zeiger. Nebeneinander ueberlebt genau
    /// einer, lautlos. Verschiedene Proxy-Dateinamen helfen nicht.
    ///
    /// Deshalb: ist MelonLoader aktiv, ist der PLUGIN-Weg der richtige - ScriptOne laeuft
    /// dann unter MelonLoader statt gegen ihn.
    ///
    /// Alle nutzersichtbaren Texte sind ENGLISCH.
    /// </remarks>
    internal static class Installer
    {
        /// <summary>Wo die zu installierenden Dateien liegen (Paketordner neben dem Setup).</summary>
        internal static string Quelle = null;

        internal static Ergebnis Fuehre(string spiel, Modus modus, bool trotzdem)
        {
            return Fuehre(spiel, modus, trotzdem, null);
        }

        /// <param name="melonBasis">Siehe <see cref="Befund.MelonBasis"/> - leer heisst Spielordner.</param>
        internal static Ergebnis Fuehre(string spiel, Modus modus, bool trotzdem, string melonBasis)
        {
            var e = new Ergebnis();
            var b = GameDetect.Untersuche(spiel, melonBasis);

            if (b.Backend == Backend.Unbekannt)
            {
                e.Stop("This does not look like a Unity game folder: no GameAssembly.dll and no " +
                       "<name>_Data\\Managed\\Assembly-CSharp.dll. Pick the folder that contains the game's .exe.");
                return e;
            }

            // ⚠ AUTO ist die Vorgabe, und es waehlt den Weg, der HIER funktioniert.
            // Ist MelonLoader aktiv, ist das Plugin richtig: beide Lader ersetzen denselben
            // Importeintrag, und wer stattdessen standalone installiert, schaltet dem Nutzer
            // ungefragt alle seine anderen Mods ab.
            if (modus == Modus.Auto)
            {
                // ⚠ DREI WEGE, NICHT ZWEI. Ist BepInEx aktiv, gehoert ScriptOne in dessen
                //   plugins-Ordner - nicht auf den Einstiegspunkt, den BepInEx schon haelt.
                //   Gemessen 2026-08-19: BepInEx ueberschrieb beim Installieren unsere
                //   winhttp.dll und unsere doorstop_config.ini; danach lief KEINER von beiden
                //   sauber, weil sich beide dieselbe Datei teilen.
                modus = WaehleModus(b);
                e.Sag(Ansage(b));
            }
            e.Gewaehlt = modus;

            switch (modus)
            {
                case Modus.Standalone: Standalone(spiel, b, trotzdem, e); break;
                case Modus.Plugin:     Plugin(spiel, b, e);               break;
                case Modus.BepPlugin:  BepPlugin(spiel, b, e);            break;
                case Modus.Entfernen:  Entfernen(spiel, b, e);            break;
            }
            return e;
        }

        // ------------------------------------------------------------------ Standalone
        private static void Standalone(string spiel, Befund b, bool trotzdem, Ergebnis e)
        {
            if (b.FremdesDoorstop && !trotzdem)
            {
                e.Stop("A FOREIGN Doorstop is already installed here (BepInEx uses the same " +
                       "winhttp.dll). Overwriting it would silently kill that installation.");
                return;
            }
            if (b.Melon == MelonZustand.Aktiv && !trotzdem)
            {
                e.Stop("MelonLoader is active. Both replace the same import entry in UnityPlayer.dll " +
                       "and neither chains the other - exactly one survives, silently. " +
                       "Use the plugin mode instead: ScriptOne then runs UNDER MelonLoader.");
                return;
            }

            // ⚠ 'KAPUTT' IST NICHT DASSELBE WIE 'KAPUTT'. Der Zustand heisst so, weil
            //   <Spiel>\version.dll daliegt, aber <Spiel>\MelonLoader\ fehlt - und das sieht
            //   GENAUSO aus wie eine voellig gesunde Installation ueber r2modman oder den
            //   Thunderstore Mod Manager. Die halten MelonLoader in ihrem PROFIL und starten
            //   das Spiel mit '--melonloader.basedir <Profil>'; MelonLoader liest Plugins\ und
            //   UserLibs\ dann von dort (LoaderConfig.cs:30/37/110 -> MelonEnvironment.cs:18/35/36).
            //   Im Spielordner bleibt genau eine Datei: version.dll.
            //
            //   Vorher lief dieser Fall in den Standalone-Zweig, weil WaehleModus nur auf
            //   'Aktiv' prueft. Der schrieb sein eigenes winhttp.dll UND schob version.dll
            //   beiseite - und schaltete damit einem Mod-Manager-Nutzer ungefragt SEINE
            //   GESAMTE Mod-Installation ab. Genau das, wovor der Kommentar bei WaehleModus
            //   warnt. Dazu zeigte die Oberflaeche eine Ferndiagnose ("the MelonLoader folder
            //   is gone"), die sie nicht belegen kann.
            //
            //   Unterscheiden laesst sich beides von aussen NICHT sicher. Also wird hier nicht
            //   geraten, sondern angehalten - der Nutzer weiss, was bei ihm gilt.
            if (b.Melon == MelonZustand.Kaputt && !trotzdem)
            {
                e.Stop(Pfade.MelonDll + " is here, but there is no " + Pfade.MelonOrdner +
                       " folder next to it. That is ALSO what a mod manager looks like " +
                       "(r2modman / Thunderstore Mod Manager keep MelonLoader inside their " +
                       "profile and start the game with --melonloader.basedir). Installing " +
                       "standalone here would move that loader aside and silently disable every " +
                       "mod in your profile. Nothing was installed. If MelonLoader really is " +
                       "broken here, remove " + Pfade.MelonDll + " yourself and run this again.");
                return;
            }

            var tfm = b.Tfm;
            string clr = null;
            if (tfm == "net6")
            {
                clr = GameDetect.FindeCoreClr();
                if (clr == null)
                {
                    e.Stop("No .NET 6 runtime found. An Il2Cpp game needs one - Doorstop starts a " +
                           "CoreCLR from it. Install the .NET 6 Desktop Runtime and run this again.");
                    return;
                }
                e.Sag(".NET 6 runtime: " + clr);
            }
            else e.Sag("Mono branch - no CoreCLR needed.");

            var quelle = QuelleFuer(tfm, spiel);
            if (quelle == null)
            {
                // ⚠ "SCHON INSTALLIERT" IST KEIN FEHLSCHLAG. Der zweite Doppelklick auf die
                //   Setup-exe landet genau hier - der Beipack ist nach dem ersten Lauf aufgeraeumt.
                //   Vorher gab das Exitcode 1 plus den Rat, den Loader zu loeschen; wer aus
                //   Unsicherheit nochmal klickt, wird also zur Deinstallation seiner
                //   funktionierenden Installation aufgefordert (Audit 2026-08-19).
                if (File.Exists(Path.Combine(Pfade.CoreTfm(spiel, tfm), Pfade.PreloaderDll))
                    && File.Exists(Path.Combine(spiel, Pfade.DoorstopDll)))
                {
                    e.Sag("ScriptOne is already installed here - nothing to do.");
                    e.Sag("To reinstall or update, unzip the package again and run this once more.");
                    e.Gewaehlt = Modus.Standalone;
                    return;
                }
                e.Stop("The payload folder " + Pfade.BeipackOrdner + " is missing next to this program - "
                       + "unzip the package again, then run the setup from inside the game folder.");
                return;
            }

            Migriere(spiel, e);

            // Der ANDERE Zweig muss weg: zwei Preloader nebeneinander sind zwei Wirte.
            foreach (var anderer in new[] { "net6", "net472" })
            {
                if (anderer == tfm) continue;
                var p = Pfade.CoreTfm(spiel, anderer);
                if (Directory.Exists(p)) { Loesche(p); e.Sag("removed the other branch: " + anderer); }
            }

            var ziel = Pfade.CoreTfm(spiel, tfm);
            Ordner(ziel);
            KopiereOrdner(quelle, ziel);
            e.Sag("host installed: " + Pfade.Wurzel + "\\" + Pfade.CoreRuntime + "\\" + tfm + " (backend " + b.BackendText + ")");

            // ⚠ DIE PASSENDE BITBREITE, nicht irgendeine. Windows laedt eine 64-Bit-DLL nicht in
            //   einen 32-Bit-Prozess - ein 32-Bit-Unity-Spiel bekaeme sonst einen Lader, der nie
            //   anspringt, und der Nutzer sucht den Fehler bei uns im Wirt. Vorher lieferte das
            //   Paket ueberhaupt nur x64 mit.
            if (b.Arch == Architektur.Unbekannt)
            {
                e.Stop("Could not read the game's architecture from UnityPlayer.dll or its .exe - "
                       + "refusing to guess, because the wrong loader would simply never start.");
                return;
            }
            var archOrdner = b.Arch == Architektur.x86 ? "x86" : "x64";
            var doorQ = Path.Combine(PaketWurzel(),
                            Path.Combine(Pfade.PaketOrdner,
                                Path.Combine(Pfade.DoorstopOrdner, Path.Combine(archOrdner, Pfade.DoorstopDll))));
            if (!File.Exists(doorQ))
            {
                e.Stop("the " + b.ArchText + " loader is missing from this package (" + doorQ + ")");
                return;
            }
            File.Copy(doorQ, Path.Combine(spiel, Pfade.DoorstopDll), true);
            VermerkeProxy(spiel, Pfade.DoorstopDll);
            e.Sag("loader architecture: " + b.ArchText + " (measured from the game's own binaries)");

            var verQ = Path.Combine(PaketWurzel(),
                            Path.Combine(Pfade.PaketOrdner,
                                Path.Combine(Pfade.DoorstopOrdner, Path.Combine(archOrdner, Pfade.DoorstopVersion))));
            if (File.Exists(verQ)) File.Copy(verQ, Path.Combine(Pfade.W(spiel), Pfade.DoorstopVersion), true);

            SchreibeDoorstopConfig(Path.Combine(spiel, Pfade.DoorstopCfg), clr, tfm);
            e.Sag("loader installed: " + Pfade.DoorstopDll + " + " + Pfade.DoorstopCfg);

            // MelonLoader beiseite - NICHT loeschen. Zurueckschalten muss moeglich bleiben.
            var mlAn = Path.Combine(spiel, Pfade.MelonDll);
            if (File.Exists(mlAn))
            {
                Ordner(Pfade.AbstellOrdner(spiel));
                Verschiebe(mlAn, Path.Combine(Pfade.AbstellOrdner(spiel), Pfade.MelonAus));
                e.Sag("MelonLoader set aside (" + Pfade.MelonDll + " -> " + Pfade.Abgestellt + "\\)");
            }
            LegeVorsorgePlugin(spiel, b, tfm, e);

            if (!LegeInterpreter(Pfade.CoreTfm(spiel, tfm), tfm, e)) return;
            Gemeinsam(spiel, b, e);
            Abschlusspruefung(spiel, tfm, e);
        }

        /// <summary>
        /// Legt die Plugin-Kopie VORSORGLICH mit - damit ScriptOne einen Lader ueberlebt, der
        /// erst SPAETER installiert wird.
        ///
        /// ⚠ HIER STAND DAS GEGENTEIL. Der Zweig verschob jede gefundene Plugin-Kopie nach
        /// <c>.plugin-off</c>, mit der Begruendung "two entry points would be two hosts". Die
        /// Sorge war berechtigt, die Abhilfe zielte aber daneben: sie verhinderte, dass die
        /// DATEI da ist, statt zu verhindern, dass zwei Wirte LAUFEN. Und sie kostete genau die
        /// Eigenschaft, die man braucht.
        ///
        /// Der Fall, um den es geht, ist gemessen und lautlos: wer spaeter BepInEx installiert,
        /// bekommt dessen winhttp.dll UND doorstop_config.ini ueber unsere - ab da zeigt der
        /// Einstiegspunkt auf BepInEx, unser Wirt liegt vollstaendig da und wird nie wieder
        /// geladen. Das Spiel startet normal, ScriptOne fehlt einfach; niemand kann es melden,
        /// weil nichts laeuft. Liegt die Plugin-Kopie dagegen schon bereit, findet der neue
        /// Lader sie beim ersten Start von selbst.
        ///
        /// Gegen zwei gleichzeitig scharfe Einstiegspunkte steht jetzt <see cref="Pfade"/>-frei
        /// der HostGuard im Wirt: der erste beansprucht den Prozess, der zweite legt sich
        /// hin und sagt es im Protokoll.
        ///
        /// ⚠ Und der naheliegende ANDERE Weg ist gemessen eine Sackgasse: dieselbe Doorstop-DLL
        /// exportiert sowohl winhttp- als auch version-Funktionen, liesse sich also als zweiter
        /// Proxy danebenlegen. Es hilft nicht - der Konfigurationsdateiname 'doorstop_config.ini'
        /// steht fest in der DLL, beide Kopien teilen sich also EINE Konfiguration, und BepInEx
        /// ueberschreibt genau die.
        /// </summary>
        private static void LegeVorsorgePlugin(string spiel, Befund b, string tfm, Ergebnis e)
        {
            // Aeltere Installationen haben die Kopie beiseitegelegt - die gehoert jetzt weg.
            foreach (var n in new[] { Pfade.PluginIl2Cpp, Pfade.PluginMono })
            {
                var weg = Path.Combine(Pfade.AbstellOrdner(spiel), n + ".plugin-off");
                if (File.Exists(weg)) { try { File.Delete(weg); } catch { } }
            }

            // ⚠ NUR bei EINDEUTIGEM Backend etwas ablegen. 'Befund.Tfm' bildet alles ausser
            //   Mono auf net6 ab - also auch 'Unbekannt' und 'Beides' (GameAssembly.dll UND ein
            //   Managed-Ordner). Fuer den laufenden Wirt ist das eine vertretbare Vorgabe, denn
            //   er wird sofort danach gestartet und scheitert sichtbar. Eine VORSORGE-Kopie
            //   dagegen liegt Monate herum und wird von einem spaeter installierten Lader
            //   geladen - eine falsch geratene Backend-Variante faellt dann zu einem Zeitpunkt
            //   auf, an dem niemand mehr an diese Installation denkt. Im Zweifel also nichts.
            if (b.Backend != Backend.Mono && b.Backend != Backend.Il2Cpp)
            {
                e.Sag("no stand-by plugin: the backend is " + b.BackendText + ", and a copy that" +
                      " a later loader picks up must not be a guess.");
                return;
            }

            var mono = b.Backend == Backend.Mono;
            var gelegt = 0;

            // 1. MelonLoader-Weg: Plugins\ + der Interpreter in UserLibs\.
            var name = mono ? Pfade.PluginMono : Pfade.PluginIl2Cpp;
            var q = Path.Combine(PaketWurzel(), Path.Combine(Pfade.PaketOrdner, name));
            if (File.Exists(q))
            {
                var mb = b.MelonBasis ?? spiel;
                Ordner(Path.Combine(mb, Pfade.PluginOrdner));
                File.Copy(q, Path.Combine(Path.Combine(mb, Pfade.PluginOrdner), name), true);
                LegeInterpreter(Path.Combine(mb, Pfade.UserLibs), tfm, e);
                e.Sag("stand-by plugin: " + Path.Combine(mb, Pfade.PluginOrdner) + Pfade.T + name);
                gelegt++;
            }

            // 2. BepInEx-Weg - JEDER Bau, der zu diesem Backend passen KANN.
            // ⚠ HIER LAG NUR DER 5er-MONO-BAU, und zwar unabhaengig davon, was der Nutzer
            //   spaeter installiert. Das ist genau der Fehler, den diese Vorsorge verhindern
            //   soll: ein Plugin fuer die falsche BepInEx-Fassung wird nicht abgelehnt, sondern
            //   GAR NICHT ANGEFASST - jeder Chainloader laedt nur Assemblies, die SEINE
            //   Assembly referenzieren. Wer auf ein Mono-Spiel spaeter BepInEx 6 setzte, verlor
            //   ScriptOne also lautlos, und auf einem Il2Cpp-Spiel lag ueberhaupt nichts bereit.
            //   WELCHE Fassung kommt, weiss hier niemand - also liegen alle passenden da; jede
            //   sieht nur ihre eigene. Gefangen vom Auslieferungspruefer 2026-08-20.
            var bepBaue = mono
                ? new[] { Pfade.BepAdapterDll, Pfade.Bep6MonoDll }
                : new[] { Pfade.Bep6Il2CppDll };
            var bepZiel = Path.Combine(Path.Combine(Path.Combine(spiel, "BepInEx"), "plugins"), "ScriptOne");
            var bepGelegt = "";
            foreach (var bau in bepBaue)
            {
                var qb = Path.Combine(PaketWurzel(), Path.Combine(Pfade.PaketOrdner,
                              Path.Combine("bepinex", bau)));
                if (!File.Exists(qb)) continue;
                Ordner(bepZiel);
                File.Copy(qb, Path.Combine(bepZiel, bau), true);
                bepGelegt += (bepGelegt.Length > 0 ? ", " : "") + bau;
            }
            if (bepGelegt.Length > 0)
            {
                LegeInterpreter(bepZiel, tfm, e);
                e.Sag("stand-by plugin: BepInEx" + Pfade.T + "plugins" + Pfade.T + "ScriptOne" +
                      Pfade.T + "(" + bepGelegt + ")");
                gelegt++;
            }

            if (gelegt > 0)
                e.Sag("   these do nothing right now. If you ever install MelonLoader or BepInEx," +
                      " that loader finds them and ScriptOne keeps working - without you having to" +
                      " run this installer again.");
        }

        // ------------------------------------------------------------------ Plugin
        private static void Plugin(string spiel, Befund b, Ergebnis e)
        {
            Migriere(spiel, e);

            // ⚠ NUR DAS EIGENE DOORSTOP ENTFERNEN. Vorher loeschte dieser Zweig winhttp.dll
            //   bedingungslos - und traf damit eine FREMDE BepInEx-Installation, die ScriptOne nie
            //   angefasst hatte. Ein Aufraeumschritt, der nichts zu tun hat, zerstoerte eine
            //   fremde Mod-Installation ohne Rueckfrage und ohne Sicherung (Audit 2026-08-19).
            if (b.FremdesDoorstop)
                e.Sag("left alone: " + Pfade.DoorstopDll + " belongs to another loader (BepInEx)");
            else
                EntferneEigenesDoorstop(spiel, e);

            // ⚠ HIER STAND EntferneVerwaistenWirt - der Kern wurde beim Plugin-Weg GELOESCHT.
            //   Genau das soll er nicht mehr: wird MelonLoader spaeter entfernt, muss ScriptOne
            //   sich selbst organisieren koennen, und dafuer muss der Kern liegenbleiben.
            //   Ansage des Autors, 2026-08-20.

            var mlAus = Path.Combine(Pfade.AbstellOrdner(spiel), Pfade.MelonAus);
            var mlAn  = Path.Combine(spiel, Pfade.MelonDll);
            if (File.Exists(mlAus) && !File.Exists(mlAn))
            {
                if (!Directory.Exists(Path.Combine(spiel, Pfade.MelonOrdner)))
                {
                    e.Stop("Refusing to restore " + Pfade.MelonDll + ": its MelonLoader folder is gone. " +
                           "That state hijacks the start and then finds nothing - worse than not installed.");
                    return;
                }
                Verschiebe(mlAus, mlAn);
                e.Sag("MelonLoader switched back on");
            }

            // ⚠ UNTERGRENZE 0.6. Das Plugin benutzt MelonLoader.Utils.MelonEnvironment - diesen
            //   Namensraum gibt es erst ab 0.6; unter 0.5.x wirft das Laden. Die Bindung selbst
            //   ist tolerant (MelonLoader ist NICHT strong-named, Token=null, die Laufzeit
            //   ignoriert die Versionsnummer) - es sind die MEMBER, die die Grenze setzen.
            if (b.MelonFassung != null && b.MelonFassung < new Version(0, 6))
            {
                e.Stop("MelonLoader " + b.MelonFassung + " is too old for ScriptOne - it needs 0.6 or "
                       + "newer (it uses MelonLoader.Utils.MelonEnvironment, which does not exist "
                       + "before 0.6). Update MelonLoader, then run this again. Nothing was installed.");
                return;
            }
            if (b.MelonFassung != null) e.Sag("MelonLoader " + b.MelonFassung + " detected.");

            var name = b.Backend == Backend.Mono ? Pfade.PluginMono : Pfade.PluginIl2Cpp;
            var q = Path.Combine(PaketWurzel(), Path.Combine(Pfade.PaketOrdner, name));
            if (!File.Exists(q))
            {
                e.Stop("The plugin build " + name + " is not in this package. This package installs the " +
                       "STANDALONE host; the plugin build ships separately.");
                return;
            }
            // ⚠ NICHT 'spiel'. Siehe Befund.MelonBasis - MelonLoader liest Plugins\ aus seiner
            //   BASIS, und die ist umlenkbar.
            var basis = b.MelonBasis ?? spiel;
            Ordner(Path.Combine(basis, Pfade.PluginOrdner));
            var ziel = Path.Combine(Path.Combine(basis, Pfade.PluginOrdner), name);
            File.Copy(q, ziel, true);

            // ⚠ Die beiseitegelegte Kopie AUFRAEUMEN. Der Standalone-Weg legt das Plugin nach
            //   disabled-loaders\<name>.plugin-off; kommt der Nutzer zurueck auf den Plugin-Weg,
            //   liegt sie sonst fuer immer dort und sieht aus, als waere noch etwas abgeschaltet.
            //   Gemessen 2026-08-19 nach einem Hin-und-Zurueck im Testspiel.
            foreach (var n2 in new[] { Pfade.PluginIl2Cpp, Pfade.PluginMono })
            {
                var alt = Path.Combine(Pfade.AbstellOrdner(spiel), n2 + ".plugin-off");
                if (File.Exists(alt)) { File.Delete(alt); e.Sag("cleaned up: " + n2 + ".plugin-off"); }
            }
            e.Sag("plugin installed: " + Path.Combine(basis, Pfade.PluginOrdner) + "\\" + name);

            // ⚠ DAS HIER FEHLTE. Der Standalone-Zweig legte den Skriptordner an, kopierte die
            // Beispiele und die Lizenztexte - der Plugin-Zweig endete nach dem File.Copy. Wer
            // ihn benutzte, bekam vom Setup den Satz "put your .lua files into LuaScripts" und
            // einen Ordner, den es nicht gab. Zwei Zweige, eine Nachbehandlung: sie steht jetzt
            // in EINER Methode, damit der naechste Zusatz nicht wieder nur in einem landet.
            // Der Zielrahmen des PLUGINS, nicht der des Wirts: der Mono-Bau ist net472,
            // der Il2Cpp-Bau net6 - und MelonLoader laedt beide aus UserLibs vor.
            if (!LegeInterpreter(Path.Combine(basis, Pfade.UserLibs),
                                 b.Backend == Backend.Mono ? "net472" : "net6", e)) return;

            Gemeinsam(spiel, b, e);

            if (!File.Exists(ziel)) { e.Stop("the plugin was not written: " + ziel); return; }
            // ⚠ NICHT UEBER DAS EIGENE SICHERHEITSNETZ WARNEN. Die Zeile stammt aus der Zeit,
            //   in der eine winhttp.dll neben MelonLoader zwangslaeufig ein FREMDER zweiter
            //   Lader war. Seit Gemeinsam sie absichtlich scharf danebenlegt, meldete der
            //   Installer eine Zeile nach "safety net armed: winhttp.dll" ein "NOTE: winhttp.dll
            //   is still here - a second loader next to MelonLoader" ueber genau diese Datei.
            //   Zwei Aussagen ueber denselben Zustand, und der Nutzer glaubt danach keiner mehr.
            //   Entschieden wird am VERMERK: was wir aufgezeichnet haben, gehoert uns.
            var eigenerProxy = "";
            try
            {
                var v = Path.Combine(Pfade.W(spiel), Pfade.Bestand);
                if (File.Exists(v)) eigenerProxy = File.ReadAllText(v).Trim();
            }
            catch { }
            if (File.Exists(Path.Combine(spiel, Pfade.DoorstopDll))
                && !string.Equals(eigenerProxy, Pfade.DoorstopDll, StringComparison.OrdinalIgnoreCase))
                e.Sag("NOTE: " + Pfade.DoorstopDll + " is still here - a second loader next to MelonLoader.");
            // ⚠ HIER STAND EINE ZUSICHERUNG, DIE SIE NICHT WAR: File.Exists beweist nur, dass
            //   WIR geschrieben haben - nicht, dass MelonLoader dort LIEST. Solange die Basis
            //   nur die Vorgabe ist, ist das eine Vermutung und wird als solche gemeldet. Die
            //   einzige belastbare Quelle dafuer, welche Basis der Loader wirklich benutzt hat,
            //   ist die Zeile 'Core::BasePath = ...' in <Basis>\MelonLoader\Logs\Latest.log -
            //   und die gibt es erst NACH einem Spielstart.
            if (b.MelonBasisGeraten || string.Equals(basis, spiel, StringComparison.OrdinalIgnoreCase))
                e.Sag("HINWEIS[UNGEPRUEFT]: this assumes MelonLoader reads Plugins\\ from the game "
                      + "folder. If you start the game through a mod manager (r2modman / Thunderstore "
                      + "Mod Manager), it reads them from its PROFILE instead - pass "
                      + "--melonloader-basedir <profile folder> then.");
            else
                e.Sag("plugin placed in the MelonLoader base you named: " + basis);
        }

        /// <summary>Welchen Weg 'Auto' hier nimmt - die EINZIGE Stelle, die das entscheidet.</summary>
        /// <remarks>
        /// ⚠ SIE MUSS EINZIG SEIN. Vorher entschied der Installer hier und das Fenster in seiner
        /// Zustandskarte noch einmal - und die Karte kannte BepInEx nicht. Der Autor sah am
        /// 2026-08-19 "Will install as: standalone" auf dem Knopf, waehrend das Protokoll
        /// darunter "installing as a BepInEx plugin" meldete. Ansage und Abhandlung deckten sich
        /// nicht, und beide waren fuer sich genommen "richtig".
        /// </remarks>
        internal static Modus WaehleModus(Befund b)
        {
            if (b.Melon == MelonZustand.Aktiv) return Modus.Plugin;
            // ⚠ AUCH NACH DER UEBERNAHME. Sonst faellt ein zweiter Lauf in den
            //   Standalone-Weg, ueberschreibt BepInEx' winhttp.dll mit unserer und vermerkt
            //   sie als UNSERE - das folgende --remove haette sie geloescht und BepInEx ohne
            //   Lader zurueckgelassen. Der BepInEx-Weg ist dagegen wiederholbar: er sichert
            //   nur einmal, schreibt die Konfiguration neu und laesst den Proxy in Ruhe.
            if (b.BepInExAktiv || b.BepVonUnsGestartet) return Modus.BepPlugin;
            return Modus.Standalone;
        }

        /// <summary>Was der Nutzer dazu liest - ebenfalls nur hier.</summary>
        internal static string Ansage(Befund b)
        {
            switch (WaehleModus(b))
            {
                case Modus.Plugin:
                    return "MelonLoader is active -> installing as a MelonLoader plugin, so your other mods keep working.";
                case Modus.BepPlugin:
                    // ⚠ GENAU DIE FALLE AUS DEM KOMMENTAR OBEN, ein zweites Mal: hier stand
                    //   noch "installing as a BepInEx plugin", waehrend BepPlugin laengst den
                    //   Einstiegspunkt uebernimmt und BepInEx selbst nachstartet. Ansage und
                    //   Abhandlung deckten sich wieder nicht.
                    return "BepInEx is active -> ScriptOne takes the entry point and starts BepInEx "
                         + "itself, so your other plugins keep working - and ScriptOne survives if you "
                         + "ever remove BepInEx. If that is not possible here, it installs as a "
                         + "BepInEx plugin instead.";
                default:
                    return "No mod loader active -> installing the standalone host.";
            }
        }

        // ------------------------------------------------------------------ BepInEx-Plugin

        /// <summary>
        /// ScriptOne neben BepInEx: zuerst der Versuch, den Einstiegspunkt zu UEBERNEHMEN und
        /// BepInEx danach selbst zu starten; nur wenn das nicht geht, als BepInEx-Plugin.
        /// </summary>
        /// <remarks>
        /// Der Kopf sagte "ohne den Einstiegspunkt anzufassen" - das war bis zur Verkettung
        /// am 2026-08-20 richtig und ist es seitdem nicht mehr.
        /// </remarks>
        /// <remarks>
        /// ⚠ HIER WIRD NICHTS AN winhttp.dll ODER doorstop_config.ini GEAENDERT. Beide gehoeren
        /// in dieser Konstellation BepInEx; sie zu ueberschreiben waere genau der Schaden, den
        /// BepInEx uns zugefuegt hat, nur in die andere Richtung.
        ///
        /// Der Interpreter liegt NEBEN dem Plugin: gemessen findet die Mono-Laufzeit die
        /// eingebettete Fassung nicht (der Resolve-Haken wird nie gefragt), eine Datei am
        /// richtigen Ort dagegen immer.
        /// </remarks>
        private static void BepPlugin(string spiel, Befund b, Ergebnis e)
        {
            Migriere(spiel, e);

            // ⚠ ZUERST DIE UEBERNAHME, DENN SIE ENTSCHEIDET UEBER DEN GANZEN REST. Gelingt sie,
            //   laeuft ScriptOne als STANDALONE-Wirt und startet BepInEx danach selbst - und
            //   dann waere ein zusaetzliches BepInEx-Plugin nicht nur ueberfluessig, sondern
            //   SCHAEDLICH: gemessen 2026-08-20 in Landlord Simulator hat es den Prozess
            //   zuerst beansprucht ("another ScriptOne host is already running (BepInEx
            //   plugin) - standing down"), womit der eigene Wirt schlief - und ausgerechnet der
            //   Plugin-Weg hat in diesem Spiel den toten Frame-Takt, den der Standalone-Weg
            //   nicht hat. Der Autor hat genau das vorhergesehen: "wenn beides dasselbe
            //   ausliest, brauchen wir doch gar kein Plugin".
            var uebernommen = UebernimmDoorstop(spiel, b, e);
            if (uebernommen)
            {
                LegeStandaloneAb(spiel, b, b.Tfm, Ablage.WirtNur, e);
                // ⚠ AB HIER IST DAS EINE STANDALONE-INSTALLATION, keine Plugin-Installation.
                //   Nicht kosmetisch: Gemeinsam legt sonst NOCH EINMAL einen Vorsorge-Satz ab
                //   (doppelte "interpreter installed"/"stand-by host"-Meldungen) und raet dem
                //   Nutzer, den Lader zu entfernen und den Installer erneut zu starten - genau
                //   das, was durch die Uebernahme unnoetig geworden ist. Und die Oberflaeche
                //   meldet den Zustand danach richtig.
                e.Gewaehlt = Modus.Standalone;
                Gemeinsam(spiel, b, e);
                Abschlusspruefung(spiel, b.Tfm, e);
                e.Sag("ScriptOne runs on its own here and starts BepInEx afterwards - so it keeps");
                e.Sag("   working even if you remove BepInEx later, and your other plugins still load.");
                return;
            }

            // ⚠ FASSUNG UND BACKEND ZUSAMMEN. Ein Plugin fuer die falsche BepInEx-Fassung wird
            //   nicht abgelehnt, sondern GAR NICHT ANGEFASST: jeder Chainloader laedt nur
            //   Assemblies, die SEINE Assembly referenzieren (BepInEx / BepInEx.Unity.Mono /
            //   BepInEx.Unity.IL2CPP). Der Nutzer haette eine erfolgreiche Installation und im
            //   Spiel nichts - die schlechteste aller Meldungen.
            //   Seit es alle drei Baue gibt, wird hier nicht mehr abgelehnt, sondern GEWAEHLT.
            var il2 = b.Backend == Backend.Il2Cpp;
            var name = Pfade.BepAdapterFuer(b.BepHaupt, il2);
            if (name == null)
            {
                e.Stop("No ScriptOne build fits BepInEx " + (b.BepFassung ?? b.BepHaupt.ToString())
                       + " on a " + b.BackendText + " game. BepInEx 5 exists for Mono only; for "
                       + "Il2Cpp you need BepInEx 6. Nothing was installed.");
                return;
            }
            e.Sag("BepInEx " + (b.BepFassung ?? (b.BepHaupt + ".x")) + " detected (" + b.BackendText + ").");

            var q = Path.Combine(PaketWurzel(),
                        Path.Combine(Pfade.PaketOrdner, Path.Combine(Pfade.BepOrdnerImPaket, name)));
            if (!File.Exists(q))
            {
                e.Stop("The BepInEx build " + name + " is not in this package (" + q + ")");
                return;
            }

            var ziel = Pfade.BepPluginOrdner(spiel);
            Ordner(ziel);
            File.Copy(q, Path.Combine(ziel, name), true);
            e.Sag("plugin installed: " + Pfade.BepInExOrdner + Pfade.T + Pfade.BepInExPlugins + Pfade.T
                  + Pfade.BepInExUnterordner + Pfade.T + name);

            // ⚠ DER INTERPRETER MUSS ZUM BAU PASSEN, nicht zum Ordner: der Il2Cpp-Adapter ist
            //   net6.0 und laedt die netstandard-Fassung von MoonSharp, der Mono-Adapter net472.
            //   Vorher stand hier fest "net472" - unter BepInEx 6 auf Il2Cpp haette das eine
            //   Assembly hinterlegt, die der Adapter nicht laden kann.
            if (!LegeInterpreter(ziel, il2 ? "net6" : "net472", e)) return;

            // ⚠ HIER WURDE EINE VORHANDENE MELONLOADER-KOPIE ABGESTELLT (.plugin-off), damit
            //   nicht zwei Wirte im selben Prozess hochkommen. Das ist der UEBERHOLTE Weg:
            //   HostGuard loest dasselbe Problem an der richtigen Stelle - der erste Wirt
            //   beansprucht den Prozess, der zweite legt sich schlafen. Sein eigener Klassenkopf
            //   sagt woertlich, dass das Verschieben "das falsche Problem loest": es verhindert,
            //   dass die DATEI da ist, statt zu verhindern, dass zwei Wirte LAUFEN. Und die
            //   Datei wird gebraucht - sie ist die Vorsorge fuer den Fall, dass BepInEx
            //   verschwindet.

            Gemeinsam(spiel, b, e);
            e.Sag("checked: the plugin is where BepInEx looks for it.");
        }

        // ------------------------------------------------------------------ Entfernen
        /// <summary>Entfernt Doorstop NUR, wenn es unseres ist.</summary>
        /// <remarks>
        /// Beleg dafuer, dass es unseres ist: die von uns geschriebene .doorstop_version in
        /// ScriptOne\ bzw. eine doorstop_config.ini, die auf unseren Preloader zeigt. Fehlt beides
        /// und liegt ein BepInEx-Ordner daneben, gehoert die winhttp.dll jemand anderem.
        /// </remarks>
        /// <summary>
        /// Raeumt einen Standalone-Wirt ab, den niemand mehr startet - und SAGT, was das fuer
        /// den Nutzer bedeutet.
        ///
        /// WARNUNG: Der Fall ist der unangenehmste im ganzen Installer, weil er LAUTLOS ist.
        /// Jemand installiert ScriptOne standalone und spaeter BepInEx. BepInEx bringt DIESELBE
        /// winhttp.dll mit und ueberschreibt sie samt doorstop_config.ini - ab da zeigt der
        /// Einstiegspunkt auf BepInEx, unser core-runtime\ liegt vollstaendig da und wird nie
        /// wieder geladen. Das Spiel startet normal, ScriptOne fehlt einfach. Kein Fehler, keine
        /// Meldung, nichts im Protokoll: es LAEUFT ja nichts, das etwas schreiben koennte.
        /// Bei MelonLoader dieselbe Lage mit anderem Mechanismus - version.dll und unsere
        /// winhttp.dll greifen denselben IAT-Slot in UnityPlayer, und keiner verkettet.
        ///
        /// Bemerken kann das nur, wer VON AUSSEN nachsieht - also dieser Installer. Und weil der
        /// Nutzer bis dahin ohne ScriptOne gespielt hat, ohne es zu merken, gehoert genau das in
        /// die Meldung und nicht bloss ein "aufgeraeumt".
        /// Gemessen 2026-08-20 an nachgebauten Spielordnern, beide Reihenfolgen.
        /// </summary>
        private static void EntferneVerwaistenWirt(string spiel, Befund b, Ergebnis e)
        {
            var wurzel = Path.Combine(Pfade.W(spiel), Pfade.CoreRuntime);
            if (!Directory.Exists(wurzel)) return;

            Loesche(wurzel);
            var stempel = Path.Combine(Pfade.W(spiel), ".doorstop_version");
            try { if (File.Exists(stempel)) File.Delete(stempel); } catch { }

            if (Directory.Exists(wurzel))
            {
                // Nicht loeschen zu koennen ist kein Grund zu schweigen - im Gegenteil.
                e.Sag("NOTE: could not remove the old standalone host in " + Pfade.CoreRuntime +
                      " - it is dead weight, nothing loads it. Delete it by hand if you like.");
                return;
            }

            var wer = b.BepInExAktiv ? "BepInEx" : "MelonLoader";
            e.Sag("removed the old standalone host (" + Pfade.CoreRuntime + ") - it was orphaned.");
            e.Sag("   " + wer + " owns the game's entry point now, so that host could never start" +
                  " again. If ScriptOne seemed to stop working after you installed " + wer + "," +
                  " that was why - this install fixes it.");
        }

        /// <summary>
        /// Haelt fest, dass DIESER Proxyname von uns stammt - die einzige Grundlage, auf der
        /// <c>--remove</c> ihn spaeter loeschen darf.
        /// </summary>
        /// <remarks>
        /// Aufgezeichnet wird an JEDER Stelle, die scharf ablegt. Es gab zwei davon und nur
        /// eine schrieb den Vermerk; die andere - der Standalone-Weg - liess winhttp.dll und
        /// doorstop_config.ini beim Entfernen liegen. Gefangen vom Auslieferungspruefer
        /// ("still there after --remove"), nicht von mir (2026-08-20). Deshalb steht das jetzt
        /// an genau einer Stelle statt zweimal nebeneinander.
        /// </remarks>
        private static void VermerkeProxy(string spiel, string name)
        {
            try
            {
                Ordner(Pfade.W(spiel));
                File.WriteAllText(Path.Combine(Pfade.W(spiel), Pfade.Bestand), name);
            }
            catch { }
        }

        private static void EntferneEigenesDoorstop(string spiel, Ergebnis e)
        {
            // ⚠ BEIDE PROXY-NAMEN, aber NUR wenn die Datei wirklich unsere ist. Seit ScriptOne
            //   sich den jeweils freien Namen nimmt, kann sein Lader auch version.dll heissen -
            //   und eine dort liegengelassene Datei saehe fuer jeden wie MelonLoader aus.
            //   Umgekehrt darf eine ECHTE MelonLoader-version.dll niemals geloescht werden:
            //   entschieden wird deshalb am INHALT (Doorstop-Groesse und -Kennung), nicht am
            //   Namen. Groessenvergleich gegen die mitgelieferten Lader.
            // ⚠ NUR DIE AUFGEZEICHNETE DATEI. Frueher entschied eine Inhaltspruefung, und die
            //   hat BepInEx' eigene winhttp.dll geloescht: es ist byte-identisch dieselbe
            //   Doorstop-Binaerdatei. Wer keine Aufzeichnung hat, loescht im Zweifel NICHTS -
            //   eine liegengebliebene Datei ist reparabel, eine geloeschte fremde nicht.
            var vermerk = Path.Combine(Pfade.W(spiel), Pfade.Bestand);
            string gelegt = null;
            try { if (File.Exists(vermerk)) gelegt = File.ReadAllText(vermerk).Trim(); } catch { }

            if (string.IsNullOrEmpty(gelegt))
            {
                e.Sag("no record of a loader placed by ScriptOne - leaving " + Pfade.DoorstopDll
                      + " and " + Pfade.DoorstopCfg + " alone.");
                return;
            }

            var dd = Path.Combine(spiel, gelegt);
            if (File.Exists(dd)) { File.Delete(dd); e.Sag("removed: " + gelegt); }
            try { File.Delete(vermerk); } catch { }

            // Die Konfiguration nur, wenn sie NICHT dem Fremdlader gehoert - der Rueckweg
            // dafuer laeuft ueber die Sicherung und ist schon vorher passiert.
            var cfg = Path.Combine(spiel, Pfade.DoorstopCfg);
            if (File.Exists(cfg) && !File.Exists(Path.Combine(Pfade.AbstellOrdner(spiel), Pfade.DoorstopCfgOriginal)))
            { File.Delete(cfg); e.Sag("removed: " + Pfade.DoorstopCfg); }
        }


        /// <summary>
        /// Entfernt ScriptOne RESTLOS - als haette es hier nie existiert.
        /// </summary>
        /// <remarks>
        /// Ansage des Autors, 2026-08-19: "remove heisst nachweislich alles loeschen von
        /// ScriptOne, also auch Ordner-Struktur". Vorher blieben ScriptOne\ und LuaScripts        /// stehen, und der Nutzer las dazu einen Absatz, warum das gut so sei - er hatte aber
        /// auf ENTFERNEN geklickt.
        ///
        /// ⚠ DIE REIHENFOLGE IST HIER KEIN STIL, SONDERN DER GANZE PUNKT: in
        /// ScriptOne\disabled-loaders\ liegt bei einer Standalone-Installation die
        /// version.dll DES NUTZERS - sein MelonLoader. Wer ScriptOne\ loescht, bevor er sie
        /// zurueckgelegt hat, zerstoert eine fremde Installation, die ScriptOne nur geliehen
        /// hatte. Deshalb: erst alles Fremde zurueck, dann das Eigene weg.
        ///
        /// Und die eine Ausnahme, die bleiben MUSS: laesst sich ein beiseitegelegter Lader
        /// NICHT zurueckgeben (sein Ordner fehlt), bleibt er liegen - lieber ein erklaerter
        /// Rest als eine geloeschte fremde Datei. Das wird ausdruecklich gemeldet.
        /// </remarks>
        private static void Entfernen(string spiel, Befund b, Ergebnis e)
        {
            // ---- 1. Alles Fremde zuerst zurueck ------------------------------------------
            // ⚠ DIE UEBERNOMMENE doorstop_config.ini ZUERST. Sie gehoert dem Fremdlader, und
            //   ohne sie startet er nach dem Entfernen von ScriptOne NICHT MEHR - der Nutzer
            //   haette ScriptOne deinstalliert und dabei alle seine anderen Plugins verloren.
            //   Das ist der schlimmstmoegliche Ausgang einer Deinstallation, deshalb steht es
            //   vor allem anderen.
            var cfgOrig = Path.Combine(Pfade.AbstellOrdner(spiel), Pfade.DoorstopCfgOriginal);
            if (File.Exists(cfgOrig))
            {
                try
                {
                    File.Copy(cfgOrig, Path.Combine(spiel, Pfade.DoorstopCfg), true);
                    File.Delete(cfgOrig);
                    e.Sag("the loader's original " + Pfade.DoorstopCfg + " is back in place.");
                }
                catch (Exception ex)
                {
                    e.Stop("could not restore the loader's " + Pfade.DoorstopCfg + " (" + ex.GetType().Name
                           + ") - stopping, because removing ScriptOne would leave that loader broken."
                           + " The original is here: " + cfgOrig);
                    return;
                }
            }

            var behalten = false;
            var mlAus = Path.Combine(Pfade.AbstellOrdner(spiel), Pfade.MelonAus);
            var mlAn  = Path.Combine(spiel, Pfade.MelonDll);
            if (File.Exists(mlAus))
            {
                if (File.Exists(mlAn))
                {
                    File.Delete(mlAus);
                    e.Sag(Pfade.MelonDll + " is already back in place - the set-aside copy was dropped");
                }
                else if (Directory.Exists(Path.Combine(spiel, Pfade.MelonOrdner)))
                {
                    Verschiebe(mlAus, mlAn);
                    e.Sag("MelonLoader switched back on");
                }
                else
                {
                    behalten = true;
                    e.Sag("KEPT: " + Pfade.Wurzel + Pfade.T + Pfade.Abgestellt + Pfade.T + Pfade.MelonAus);
                    e.Sag("  That is YOUR MelonLoader. Its own folder is gone, so putting it back would");
                    e.Sag("  hijack the game start and then find nothing. Reinstall MelonLoader, then");
                    e.Sag("  rename this file back to " + Pfade.MelonDll + " - or delete it if you are done with it.");
                }
            }

            // ---- 2. Unser Lader, unsere Dateien ------------------------------------------
            if (b.FremdesDoorstop)
                e.Sag("left alone: " + Pfade.DoorstopDll + " belongs to another loader (BepInEx)");
            else
                EntferneEigenesDoorstop(spiel, e);

            // ⚠ BEIDE ORTE. Installiert wurde in b.MelonBasis (per Vorgabe der Spielordner,
            //   sonst der genannte Profilordner). Wer beim Entfernen nur den Spielordner
            //   abraeumt, laesst bei jedem Mod-Manager-Nutzer die Dateien liegen - und die
            //   Schlusszeile behauptet trotzdem "as if it had never been here". Der Spielordner
            //   bleibt in der Liste, weil eine FRUEHERE Installation dort gelandet sein kann.
            var orte = new List<string> { spiel };
            if (!string.IsNullOrEmpty(b.MelonBasis)
                && !string.Equals(b.MelonBasis, spiel, StringComparison.OrdinalIgnoreCase))
                orte.Add(b.MelonBasis);

            foreach (var ort in orte)
            {
                foreach (var n in new[] { Pfade.PluginIl2Cpp, Pfade.PluginMono })
                {
                    var pl = Path.Combine(Path.Combine(ort, Pfade.PluginOrdner), n);
                    if (File.Exists(pl)) { File.Delete(pl); e.Sag("removed: " + Path.Combine(ort, Pfade.PluginOrdner) + Pfade.T + n); }
                }
                var ms = Path.Combine(Path.Combine(ort, Pfade.UserLibs), Pfade.MoonSharpDll);
                if (File.Exists(ms)) { File.Delete(ms); e.Sag("removed: " + Path.Combine(ort, Pfade.UserLibs) + Pfade.T + Pfade.MoonSharpDll); }
            }

            // ---- 3. Die Ordnerstruktur, restlos ------------------------------------------
            if (behalten)
            {
                // Alles ausser dem fremden Lader - der Ordner bleibt genau deswegen stehen.
                LoescheAusser(Pfade.W(spiel), Pfade.Abgestellt, e);
            }
            else if (Directory.Exists(Pfade.W(spiel)))
            {
                Loesche(Pfade.W(spiel));
                e.Sag("removed: " + Pfade.Wurzel + Pfade.T + " (config, logs, docs, licenses, state)");
            }

            // LuaScripts gehoert ScriptOne - aber was DRIN liegt, gehoert dem Nutzer. Deshalb
            // wird gesagt, wie viele Dateien mitgehen, statt sie stillschweigend zu entfernen.
            var skripte = Path.Combine(spiel, Pfade.SkriptOrdner);
            if (Directory.Exists(skripte))
            {
                var n2 = Directory.GetFiles(skripte, "*", SearchOption.AllDirectories).Length;
                Loesche(skripte);
                e.Sag("removed: " + Pfade.SkriptOrdner + Pfade.T +
                      (n2 > 0 ? " (including " + n2 + " file(s) you had put there)" : " (was empty)"));
            }

            // Leere Ordner, die es ohne uns nicht gaebe.
            // ⚠ Die BepInEx-Vorsorgekopie gehoert genauso weg. Sie wurde beim Entfernen
            //   uebersehen, weil sie als einzige NICHT in einem Ordner liegt, den ScriptOne
            //   sonst anfasst - gemessen blieb 'BepInEx\plugins\ScriptOne' samt beider DLLs
            //   stehen, obwohl die Meldung "as if it had never been here" lautete.
            //   NUR DEN EIGENEN UNTERORDNER loeschen; 'plugins' und 'BepInEx' nur dann, wenn
            //   sie danach LEER sind - eine echte BepInEx-Installation darf das nie treffen.
            var bepMein = Path.Combine(Path.Combine(Path.Combine(spiel, "BepInEx"), "plugins"), "ScriptOne");
            if (Directory.Exists(bepMein))
            {
                Loesche(bepMein);
                if (!Directory.Exists(bepMein))
                    e.Sag("removed: BepInEx" + Pfade.T + "plugins" + Pfade.T + "ScriptOne" + Pfade.T);
            }
            foreach (var leer in new[] { Path.Combine(Path.Combine(spiel, "BepInEx"), "plugins"),
                                         Path.Combine(spiel, "BepInEx") })
            {
                try
                {
                    if (Directory.Exists(leer) && Directory.GetFileSystemEntries(leer).Length == 0)
                    {
                        Directory.Delete(leer);
                        var kurz = leer.Substring(spiel.Length).TrimStart(Path.DirectorySeparatorChar);
                        e.Sag("removed empty folder: " + kurz + Pfade.T);
                    }
                }
                catch { }
            }

            foreach (var o in new[] { Pfade.UserLibs, Pfade.PluginOrdner })
            {
                var d = Path.Combine(spiel, o);
                try
                {
                    if (Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length == 0)
                    { Directory.Delete(d); e.Sag("removed empty folder: " + o + Pfade.T); }
                }
                catch { }
            }

            e.Sag("");
            e.Sag(behalten
                ? "ScriptOne is gone except for the one file named above. The game starts normally again."
                : "ScriptOne is gone - as if it had never been here. The game starts normally again.");
        }

        /// <summary>Loescht einen Ordner bis auf EINEN Unterordner, den es zu schuetzen gilt.</summary>
        private static void LoescheAusser(string wurzel, string ausnahme, Ergebnis e)
        {
            if (!Directory.Exists(wurzel)) return;
            foreach (var d in Directory.GetDirectories(wurzel))
                if (!string.Equals(Path.GetFileName(d), ausnahme, StringComparison.OrdinalIgnoreCase))
                    Loesche(d);
            foreach (var f in Directory.GetFiles(wurzel))
                try { File.Delete(f); } catch { }
            e.Sag("removed: everything in " + Pfade.Wurzel + Pfade.T + " except " + ausnahme + Pfade.T);
        }


        // ------------------------------------------------------------------ Hilfen
        /// <summary>Benennt Ordner aelterer Installationen um. Idempotent.</summary>
        private static void Migriere(string spiel, Ergebnis e)
        {
            var w = Pfade.W(spiel);
            if (!Directory.Exists(w)) return;
            foreach (var paar in new[]
            {
                new[] { "core",             Pfade.CoreRuntime },
                new[] { "interop",          Pfade.Interop     },
                new[] { "Il2CppAssemblies", Pfade.Interop     },
                new[] { "backup",           Pfade.Abgestellt  },
            })
            {
                var alt = Path.Combine(w, paar[0]);
                var neu = Path.Combine(w, paar[1]);
                if (!Directory.Exists(alt) || Directory.Exists(neu)) continue;
                try { Directory.Move(alt, neu); e.Sag("renamed: " + paar[0] + "\\ -> " + paar[1] + "\\"); } catch { }
            }
            var cfgAlt = Path.Combine(w, "ScriptOne.cfg");
            var cfgNeu = Pfade.CfgPfad(spiel);
            if (File.Exists(cfgAlt) && !File.Exists(cfgNeu)) { try { File.Move(cfgAlt, cfgNeu); } catch { } }
        }

        private static string PaketWurzel()
        {
            return Quelle ?? AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>Die Quelle fuer einen Zielrahmen - oder <c>null</c>, wenn das Paket fehlt.</summary>
        /// <remarks>
        /// ⚠ EINE QUELLE IM ZIELORDNER IST KEINE QUELLE. Nach einem erfolgreichen Lauf raeumt das
        /// Setup den Beipackordner weg; PaketWurzel() faellt danach auf den eigenen Ordner zurueck,
        /// und das ist der SPIELordner. Dort fand diese Methode die eben INSTALLIERTE DLL und gab
        /// sie als Quelle zurueck - File.Copy(x, x, true) wirft dann, und der Nutzer las eine
        /// deutsche .NET-Ausnahme mitten in englischer Ausgabe plus den Rat, seine gerade
        /// erfolgreich installierte Software zu loeschen. Reproduzierbar beim ZWEITEN Doppelklick,
        /// also genau bei "nochmal klicken, ob es geklappt hat" (Audit 2026-08-19).
        /// </remarks>
        private static string QuelleFuer(string tfm, string spiel)
        {
            var p = Path.Combine(PaketWurzel(), Path.Combine(Pfade.Wurzel, Path.Combine(Pfade.CoreRuntime, tfm)));
            if (!Directory.Exists(p) || !File.Exists(Path.Combine(p, Pfade.PreloaderDll))) return null;
            // ⚠ NICHT "unter dem Spielordner" pruefen - der Beipack liegt beim ZIP-Weg genau dort,
            //   und zwar mit Absicht. Der Fehler ist enger: die Quelle darf nicht DIE INSTALLATION
            //   SELBST sein, sonst kopiert File.Copy eine Datei auf sich selbst. Meine erste
            //   Fassung sperrte den ganzen Spielordner und brach damit den Normalfall - gefangen
            //   vom Paketpruefer im selben Lauf, nicht vom Bau.
            if (LiegtUnter(p, Pfade.CoreTfm(spiel, tfm))) return null;
            return p;
        }

        /// <summary>Liegt <paramref name="pfad"/> innerhalb von <paramref name="wurzel"/>?</summary>
        private static bool LiegtUnter(string pfad, string wurzel)
        {
            try
            {
                var a = Path.GetFullPath(pfad).TrimEnd(Path.DirectorySeparatorChar);
                var b = Path.GetFullPath(wurzel).TrimEnd(Path.DirectorySeparatorChar);
                return a.Equals(b, StringComparison.OrdinalIgnoreCase)
                    || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Was BEIDE Wege brauchen: Lizenztexte, Skriptordner, Beispiele.
        /// </summary>
        /// <remarks>
        /// ⚠ Warum gemeinsam und nicht zweimal: genau das ging schief. Der Plugin-Zweig war
        /// eine kuerzere Kopie des Standalone-Zweigs, und was spaeter am laengeren wuchs, fehlte
        /// im kuerzeren - ohne Fehlermeldung, weil "nichts kopiert" wie "nichts noetig" aussieht.
        /// </remarks>
        /// <summary>
        /// Legt den STANDALONE-Satz ab: Kern, Interpreter, Lader und Konfiguration - scharf oder
        /// abgestellt.
        /// </summary>
        /// <param name="scharf">
        /// true: die Proxy-DLL und doorstop_config.ini kommen in den SPIELORDNER, der Wirt
        /// startet also selbst. false: beide kommen nach <c>ScriptOne\disabled-loaders\</c>.
        ///
        /// ⚠ ABGESTELLT HEISST NICHT ABGESCHALTET, SONDERN NICHT VORHANDEN. Eine
        /// doorstop_config.ini mit enabled=false wuerde nicht genuegen: die DLL waere trotzdem
        /// geladen und haette denselben IAT-Eintrag belegt wie der Fremdlader
        /// (kernel32!GetProcAddress in UnityPlayer) - es ueberlebt genau einer, lautlos. Wer
        /// friedlich danebenliegen will, legt die Datei GAR NICHT hin.
        ///
        /// WARUM DER SATZ TROTZDEM KOMPLETT ABGELEGT WIRD: verschwindet MelonLoader oder
        /// BepInEx spaeter, gibt es niemanden mehr, der den teuren, spielabgeleiteten Teil
        /// erzeugen koennte. Er muss also schon dasein. Scharfschalten ist danach nur noch ein
        /// Verschieben zweier Dateien - das erledigt der naechste Installerlauf, der dann keinen
        /// Fremdlader mehr findet.
        /// </param>
        private static void LegeStandaloneAb(string spiel, Befund b, string tfm, Ablage lage, Ergebnis e)
        {
            var scharf = lage == Ablage.Scharf;
            var quelle = QuelleFuer(tfm, spiel);
            if (quelle == null)
            {
                if (scharf) e.Stop("this package carries no host for " + tfm + ".");
                else e.Sag("NOTE: no stand-by host for " + tfm + " in this package.");
                return;
            }

            // Der ANDERE Zweig muss weg: zwei Preloader nebeneinander sind zwei Wirte.
            foreach (var anderer in new[] { "net6", "net472" })
            {
                if (anderer == tfm) continue;
                var alt = Pfade.CoreTfm(spiel, anderer);
                if (Directory.Exists(alt)) { Loesche(alt); e.Sag("removed the other branch: " + anderer); }
            }

            var kern = Pfade.CoreTfm(spiel, tfm);
            Ordner(kern);
            KopiereOrdner(quelle, kern);
            if (!LegeInterpreter(kern, tfm, e)) return;
            e.Sag((lage == Ablage.Vorsorge ? "stand-by host installed: " : "host installed: ")
                  + Pfade.Wurzel + Pfade.T + Pfade.CoreRuntime + Pfade.T + tfm
                  + " (backend " + b.BackendText + ")");

            // Der Einstiegspunkt gehoert schon uns - ab hier gaebe es nichts mehr zu tun,
            // und jeder weitere Schritt wuerde den Fremdlader anfassen, den wir gerade
            // verketten.
            if (lage == Ablage.WirtNur) return;

            // ⚠ DIE PASSENDE BITBREITE, nicht irgendeine. Windows laedt eine 64-Bit-DLL nicht in
            //   einen 32-Bit-Prozess - ein 32-Bit-Spiel bekaeme sonst einen Lader, der nie
            //   anspringt, und der Nutzer sucht den Fehler bei uns im Wirt.
            if (b.Arch == Architektur.Unbekannt)
            {
                if (scharf)
                {
                    e.Stop("Could not read the game's architecture from UnityPlayer.dll or its .exe - "
                           + "refusing to guess, because the wrong loader would simply never start.");
                    return;
                }
                e.Sag("NOTE: architecture unreadable - the stand-by loader was not staged.");
                return;
            }

            var archOrdner = b.Arch == Architektur.x86 ? "x86" : "x64";
            var doorQ = Path.Combine(PaketWurzel(),
                            Path.Combine(Pfade.PaketOrdner,
                                Path.Combine(Pfade.DoorstopOrdner, Path.Combine(archOrdner, Pfade.DoorstopDll))));
            if (!File.Exists(doorQ))
            {
                if (scharf) { e.Stop("the " + b.ArchText + " loader is missing from this package (" + doorQ + ")"); return; }
                e.Sag("NOTE: no " + b.ArchText + " loader in this package - nothing staged.");
                return;
            }

            // ⚠ AUCH IM PLUGIN-WEG SCHARF - unter dem FREIEN Proxy-Namen. Der abgestellte Satz
            //   erfuellte die Anforderung nicht: wer den Fremdlader loescht, klickt keine Datei
            //   an, die er nicht kennt - er stellt fest, dass seine Skripte nicht mehr starten.
            //   Gemessen 2026-08-20 in Schedule I: scharf neben MelonLoader gelegt, MelonLoader
            //   gewinnt den Einstieg und unser Doorstop bleibt vollstaendig still; danach
            //   MelonLoader geloescht, Spiel gestartet - OHNE Installer und ohne Klick - und
            //   ScriptOne uebernahm samt der eigenen Skripte des Nutzers.
            var name = Pfade.ProxyName(b.Melon == MelonZustand.Aktiv, b.BepInExAktiv);
            var eigenScharf = name != null;
            var dllZiel = eigenScharf ? Path.Combine(spiel, name)
                                      : Path.Combine(Pfade.AbstellOrdner(spiel), Pfade.DoorstopDll);
            var cfgZiel = eigenScharf ? Path.Combine(spiel, Pfade.DoorstopCfg)
                                      : Path.Combine(Pfade.AbstellOrdner(spiel), Pfade.DoorstopCfg);
            if (!eigenScharf) Ordner(Pfade.AbstellOrdner(spiel));

            // ⚠ EINE FREMDE doorstop_config.ini DARF NICHT UEBERSCHRIEBEN WERDEN. BepInEx
            //   benutzt dieselbe Datei; unsere Angaben wuerden seinen Einstieg zerstoeren.
            //   In dem Fall bleibt es beim abgestellten Satz - lieber kein Selbstheilen als ein
            //   kaputter Fremdlader.
            if (eigenScharf && b.FremdesDoorstop && name == Pfade.DoorstopDll)
            {
                e.Sag("NOTE: " + Pfade.DoorstopCfg + " belongs to another loader - staging instead of arming.");
                eigenScharf = false;
                dllZiel = Path.Combine(Pfade.AbstellOrdner(spiel), Pfade.DoorstopDll);
                cfgZiel = Path.Combine(Pfade.AbstellOrdner(spiel), Pfade.DoorstopCfg);
                Ordner(Pfade.AbstellOrdner(spiel));
            }
            var lagVorher = File.Exists(dllZiel);
            File.Copy(doorQ, dllZiel, true);
            // ⚠ NUR WAS WIR SELBST HINGELEGT HABEN, DARF SPAETER WEG. Lag dort schon eine
            //   Datei desselben Namens, gehoert sie dem Fremdlader (BepInEx benutzt dieselbe
            //   Doorstop-Binaerdatei) - dann wird sie NICHT vermerkt und beim Entfernen nicht
            //   angefasst.
            if (eigenScharf && !lagVorher) VermerkeProxy(spiel, name);

            var verQ = Path.Combine(PaketWurzel(),
                            Path.Combine(Pfade.PaketOrdner,
                                Path.Combine(Pfade.DoorstopOrdner, Path.Combine(archOrdner, Pfade.DoorstopVersion))));
            if (File.Exists(verQ)) File.Copy(verQ, Path.Combine(Pfade.W(spiel), Pfade.DoorstopVersion), true);

            // ⚠ Die CoreCLR-Suche darf im ABGESTELLTEN Fall nicht abbrechen: das Plugin laeuft
            //   ja ueber seinen Lader, und ob spaeter eine .NET-6-Laufzeit da ist, entscheidet
            //   sich erst beim Scharfschalten. Fehlt sie jetzt, wird die Konfiguration trotzdem
            //   geschrieben - der Standalone-Wirt meldet den Mangel dann selbst und benennt ihn.
            SchreibeDoorstopConfig(cfgZiel, tfm == "net6" ? GameDetect.FindeCoreClr() : null, tfm);
            if (scharf)
            {
                e.Sag("loader architecture: " + b.ArchText + " (measured from the game's own binaries)");
                e.Sag("loader installed: " + name + " + " + Pfade.DoorstopCfg);
            }
            else if (eigenScharf)
            {
                e.Sag("safety net armed: " + name + " - it stays silent while " + Ladername(b)
                      + " runs, and takes over by itself if you ever remove it.");
                e.Sag("   no second install, no click: your scripts keep working.");
            }
            else
            {
                e.Sag("stand-by loader staged: " + Pfade.Wurzel + Pfade.T + Pfade.Abgestellt + Pfade.T
                      + Pfade.DoorstopDll + " - it cannot be armed next to " + Ladername(b)
                      + ", because that loader IS Doorstop and both would read the same"
                      + " doorstop_config.ini. Remove that loader and run this once.");
            }
        }

        /// <summary>Wie der aktive Fremdlader in einer Meldung heisst.</summary>
        private static string Ladername(Befund b)
        {
            if (b.Melon == MelonZustand.Aktiv) return "MelonLoader";
            if (b.BepInExAktiv || b.BepVonUnsGestartet) return "BepInEx";
            return "the other loader";
        }

        /// <summary>
        /// Uebernimmt bei einem DOORSTOP-BASIERTEN Fremdlader (BepInEx) den Einstieg - und
        /// laesst unseren Preloader diesen Lader danach weiterstarten.
        /// </summary>
        /// <remarks>
        /// ⚠ WARUM DIESER EINGRIFF UEBERHAUPT: BepInEx IST Doorstop. Es gibt genau EINE
        /// doorstop_config.ini, und beide Proxy-DLLs wuerden sie lesen - ein zweiter Lader
        /// unter freiem Dateinamen bringt deshalb nichts. Ohne Uebernahme ist ScriptOne tot,
        /// sobald jemand BepInEx loescht, und der Nutzer sieht nur, dass seine Skripte nicht
        /// mehr starten. Bei MelonLoader ist das anders geloest (eigener Proxy daneben), weil
        /// MelonLoader gar kein Doorstop benutzt.
        ///
        /// ⚠ DER PREIS, offen benannt: danach sitzt ScriptOne VOR dem fremden Lader. Bricht
        /// unser Preloader, startet BepInEx nicht - und mit ihm keines seiner Plugins. Deshalb
        /// wird die Originalkonfiguration GESICHERT (Rueckweg per --remove), der Kettenaufruf
        /// steht als Allererstes im Preloader und faengt alles ab, und das ScriptOne-Plugin
        /// bleibt als zweites Bein liegen: kommt die Kette durch und scheitert nur unser Wirt,
        /// traegt BepInEx ihn ueber das Plugin. Welcher der beiden Wege gewinnt, entscheidet
        /// HostGuard zur Laufzeit.
        /// </remarks>
        private static bool UebernimmDoorstop(string spiel, Befund b, Ergebnis e)
        {
            var cfg = Path.Combine(spiel, Pfade.DoorstopCfg);
            if (!File.Exists(cfg)) { e.Sag("no " + Pfade.DoorstopCfg + " here - nothing to take over."); return false; }

            var sicherung = Path.Combine(Pfade.AbstellOrdner(spiel), Pfade.DoorstopCfgOriginal);
            try
            {
                Ordner(Pfade.AbstellOrdner(spiel));
                // ⚠ NUR EINMAL SICHERN. Beim zweiten Lauf stuende dort sonst UNSERE Fassung,
                //   und der Rueckweg fuehrte ins Nichts.
                if (!File.Exists(sicherung))
                {
                    File.Copy(cfg, sicherung, false);
                    e.Sag("saved the loader's original " + Pfade.DoorstopCfg + " - '--remove' puts it back.");
                }
                SchreibeDoorstopConfig(cfg, b.Tfm == "net6" ? GameDetect.FindeCoreClr() : null, b.Tfm);
                e.Sag("entry point taken over: ScriptOne starts first and then starts "
                      + Ladername(b) + " itself, so nothing of yours is lost.");
                e.Sag("   that is what lets ScriptOne keep working if you ever remove "
                      + Ladername(b) + " - without running this installer again.");
                return true;
            }
            catch (Exception ex)
            {
                e.Sag("could not take over the entry point (" + ex.GetType().Name
                      + ") - ScriptOne runs as a plugin only, and would stop working if you remove "
                      + Ladername(b) + ".");
                return false;
            }
        }

        private static void Gemeinsam(string spiel, Befund b, Ergebnis e)
        {
            // RECHTLICHE PFLICHT, nicht Komfort: MIT und LGPL verlangen den Lizenztext
            // "in all copies". Ein vergessener Kopierschritt faellt sonst nie auf.
            var lizQ = Path.Combine(PaketWurzel(), Pfade.Lizenzen);
            if (Directory.Exists(lizQ))
            {
                Ordner(Pfade.LizenzOrdner(spiel));
                KopiereOrdner(lizQ, Pfade.LizenzOrdner(spiel));
            }

            // ⚠ AM INHALT MESSEN, NICHT AM ORDNER. Die Erfolgsmeldung haengte am Vorhandensein
            //   des Quellordners und erschien auch bei NULL kopierten Dateien - wer spaeter eine
            //   Installation diagnostiziert, liest "license texts installed" und streicht die
            //   Lizenzfrage von der Liste. Eine solche Meldung ist schlechter als keine.
            var fehlend = "";
            foreach (var n in Pfade.LizenzSoll)
                if (!File.Exists(Path.Combine(Pfade.LizenzOrdner(spiel), n))) fehlend += " " + n;
            if (fehlend.Length > 0)
            {
                e.Stop("license texts missing - this package may not be distributed like this:" + fehlend);
                return;
            }
            e.Sag("license texts installed: " + Pfade.LizenzSoll.Length + " files in "
                  + Pfade.Wurzel + "\\" + Pfade.Lizenzen);

            // ⚠ DIE KONFIGURATION ENTSTEHT BEIM INSTALLIEREN, nicht erst beim ersten
            //   Spielstart. Vorher legte sie nur der Wirt an - wer nach der Installation
            //   nachsehen wollte, welche Schalter es gibt, fand einen Ordner ohne
            //   Konfigurationsdatei. Dieselbe Klasse wie ueberall heute: was der Nutzer nicht
            //   sieht, existiert fuer ihn nicht.
            try
            {
                ScriptOne.Host.HostConfig.LiesOderLege(Pfade.CfgPfad(spiel));
                e.Sag("settings file written: " + Pfade.Wurzel + Pfade.T + Pfade.Cfg
                      + " - every option is explained inside it.");
            }
            catch (Exception ex)
            {
                // Kein Abbruch: der Wirt legt sie beim ersten Start ohnehin an. Verschwiegen
                // werden darf der Fehlschlag aber nicht.
                e.Sag("could not write " + Pfade.Cfg + " (" + ex.GetType().Name
                      + ") - the host will create it on the first game start.");
            }

            SkriptordnerAnlegen(spiel, e);

            // ⚠ AUF JEDEM WEG DER VOLLE SATZ. Beim Plugin-Weg abgestellt, nicht scharf -
            //   Begruendung bei LegeStandaloneAb. Der Standalone-Weg hat ihn schon selbst
            //   scharf abgelegt und kommt hier nicht noch einmal vorbei.
            if (e.Erfolg && e.Gewaehlt != Modus.Standalone && b != null && b.Tfm != null)
                LegeStandaloneAb(spiel, b, b.Tfm, Ablage.Vorsorge, e);

            RichteProxiesEin(spiel, b, e);
        }

        /// <summary>
        /// Erzeugt die Il2Cpp-Proxy-Assemblies aus den Spieldateien - auf JEDEM Weg, auch wenn
        /// nur das Plugin abgelegt wird.
        /// </summary>
        /// <remarks>
        /// ⚠ WARUM AUCH IM PLUGIN-WEG, wo der Lader eigene Proxies mitbringt: wird MelonLoader
        /// oder BepInEx spaeter GELOESCHT, soll ScriptOne beim naechsten Start weiterlaufen
        /// koennen, statt in einen Fehler zu laufen. Die teuren, spielabgeleiteten Teile muessen
        /// dafuer schon dasein - nachtraeglich gibt es niemanden mehr, der sie erzeugen koennte.
        /// Ansage des Autors, 2026-08-20.
        ///
        /// ⚠ EIN FEHLSCHLAG KIPPT DIE INSTALLATION NICHT. Auf einem Mono-Spiel gibt es hier
        /// nichts zu tun; im Plugin-Weg ist das Ergebnis blosse Vorsorge; und ohne Netz kann die
        /// Fassung der Unity-Basisbibliotheken nicht beschafft werden. In allen drei Faellen ist
        /// die Installation trotzdem brauchbar - verschwiegen wird der Ausfall aber nicht.
        /// </remarks>
        private static void RichteProxiesEin(string spiel, Befund b, Ergebnis e)
        {
            if (b == null || b.Backend != Backend.Il2Cpp) return;   // Mono braucht keine Proxies

            var ziel = Path.Combine(Pfade.W(spiel), Pfade.Interop);
            if (Directory.Exists(ziel) && File.Exists(Path.Combine(ziel, "Assembly-CSharp.dll")))
            {
                e.Sag("Il2Cpp proxy assemblies: already present, left alone.");
                return;
            }

            // Der Erzeuger bleibt liegen - nach einem Spiel-Update laesst sich damit ohne Netz
            // und ohne Setup neu erzeugen.
            var werkzeugQ = Path.Combine(PaketWurzel(), Path.Combine(Pfade.PaketOrdner, Pfade.Generator));
            if (!Directory.Exists(werkzeugQ))
            {
                e.Sag("NOTE: this package carries no proxy generator - " + Pfade.Wurzel + Pfade.T
                      + Pfade.Interop + Pfade.T + " stays empty.");
                return;
            }
            var werkzeug = Path.Combine(Pfade.W(spiel), Pfade.Generator);
            Ordner(werkzeug);
            KopiereOrdner(werkzeugQ, werkzeug);

            var fassung = Proxies.UnityFassung(spiel);
            if (fassung == null)
            {
                e.Sag("could not read the Unity version from UnityPlayer.dll - skipping proxy generation.");
                return;
            }
            e.Sag("Unity " + fassung + " detected.");

            if (!Proxies.HoleUnityLibs(werkzeug, fassung, e))
            {
                e.Sag("without them the proxies cannot be generated. Everything else is installed;");
                e.Sag("run the installer again once you are online, or generate them yourself with");
                e.Sag("  " + Pfade.Wurzel + Pfade.T + Pfade.Generator + Pfade.T + "InteropGen.exe --game \"<game folder>\" --out \""
                      + Pfade.Wurzel + Pfade.T + Pfade.Interop + "\"");
                return;
            }

            Ordner(ziel);
            Proxies.Erzeuge(werkzeug, spiel, ziel, e);
        }




        /// <summary>
        /// Legt den Lua-Interpreter als DATEI dorthin, wo die Laufzeit ihn von selbst findet.
        /// </summary>
        /// <remarks>
        /// ⚠ WARUM ALS DATEI, obwohl er in der Mod-DLL eingebettet ist: gemessen 2026-08-19
        /// unter Unity 6 / MonoBleedingEdge / MelonLoader 0.7.3 findet die Laufzeit die
        /// eingebettete Fassung NICHT. Der AssemblyResolve-Haken wurde nie gefragt
        /// ("resolve hook asked 0 time(s)"), und auch Vorabladen per Assembly.Load half nicht -
        /// Mono bedient eine Referenz nicht aus einer per Bytes geladenen Assembly. Beide
        /// Messungen stehen im Latest.log des Testspiels.
        ///
        /// Was die Laufzeit dagegen IMMER findet, ist eine Datei am richtigen Ort:
        ///   * als MelonLoader-Plugin -> UserLibs\ (wird gemessen VOR Plugins\ geladen)
        ///   * standalone             -> neben den Preloader in core-runtime\&lt;tfm&gt;        /// Die eingebettete Kopie bleibt als Rueckfall drin; sie kostet nichts und traegt im
        /// Il2Cpp-Zweig unter CoreCLR nachweislich.
        /// </remarks>
        private static bool LegeInterpreter(string ziel, string tfm, Ergebnis e)
        {
            var q = Path.Combine(PaketWurzel(),
                        Path.Combine(Pfade.PaketOrdner, Path.Combine(Pfade.MoonSharpOrdner, tfm)));
            var datei = Path.Combine(q, Pfade.MoonSharpDll);
            if (!File.Exists(datei))
            {
                e.Stop("the Lua interpreter is missing from this package (" + datei + ")");
                return false;
            }
            Ordner(ziel);
            File.Copy(datei, Path.Combine(ziel, Pfade.MoonSharpDll), true);
            e.Sag("interpreter installed: " + Pfade.MoonSharpDll + " (" + tfm + ")");
            return true;
        }


        /// <remarks>
        /// ⚠ BOM-frei schreiben. Der native Doorstop-Parser haengt sonst an der ersten Zeile
        /// [General] - und PowerShells Out-File/Set-Content setzen in 5.1 einen BOM.
        /// </remarks>
        private static void SchreibeDoorstopConfig(string ziel, string clrOrdner, string tfm)
        {
            var t = new StringBuilder();
            t.AppendLine("# Written by the ScriptOne setup - do not edit by hand, it is regenerated.");
            t.AppendLine("# target_assembly points at the branch that matches this game's backend:");
            t.AppendLine("#   net6   = Il2Cpp (Doorstop starts a CoreCLR)");
            t.AppendLine("#   net472 = Mono   (Doorstop runs inside the game's Mono domain, no CoreCLR)");
            t.AppendLine("[General]");
            t.AppendLine("enabled=true");
            t.AppendLine("target_assembly=" + Pfade.TargetAssembly(tfm));
            t.AppendLine("redirect_output_log=false");
            t.AppendLine("boot_config_override=");
            t.AppendLine("ignore_disable_switch=false");
            t.AppendLine();
            t.AppendLine("[UnityMono]");
            t.AppendLine("dll_search_path_override=");
            t.AppendLine("debug_enabled=false");
            t.AppendLine("debug_address=127.0.0.1:10000");
            t.AppendLine("debug_suspend=false");
            t.AppendLine();
            t.AppendLine("[Il2Cpp]");
            t.AppendLine("coreclr_path=" + (clrOrdner == null ? "" : Path.Combine(clrOrdner, "coreclr.dll")));
            t.AppendLine("corlib_dir=" + (clrOrdner ?? ""));
            File.WriteAllText(ziel, t.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// Was fehlen wuerde, faellt HIER auf - nicht beim Nutzer als "startet nicht".
        /// </summary>
        /// <summary>Legt den Skriptordner an - LEER.</summary>
        /// <remarks>
        /// ⚠ HIER WURDEN FRUEHER BEISPIELE HINEINKOPIERT, und das war zweimal falsch. Zuerst
        /// wanderten sie in JEDES Spiel, obwohl alle drei Funktionen genau eines Spiels rufen -
        /// der Autor fand sie in einem frischen fremden Ordner. Danach lag statt ihrer eine
        /// README.txt dort. Ansage des Autors am 2026-08-19: LuaScripts enthaelt NICHTS.
        ///
        /// Der Ordner selbst wird angelegt, damit der Nutzer sieht, WOHIN seine Dateien gehoeren.
        /// Was er aufrufen kann, schreibt der Wirt beim ersten Start nach ScriptOne\documentation -
        /// aus der tatsaechlich installierten Flaeche, also passend zu SEINEM Spiel.
        /// </remarks>
        private static void SkriptordnerAnlegen(string spiel, Ergebnis e)
        {
            var ziel = Path.Combine(spiel, Pfade.SkriptOrdner);
            var neu = !Directory.Exists(ziel);
            Ordner(ziel);
            if (neu) e.Sag("created: " + Pfade.SkriptOrdner + Pfade.T);

            // ⚠ GENAU EIN SKRIPT, und nur wenn noch keines da ist. Ansage des Autors: man soll
            //   nach der Installation SEHEN, dass es laeuft, ohne selbst etwas zu schreiben.
            //   Es benutzt ausschliesslich Kernfunktionen (s1.on/s1.log/s1.surface_size) und
            //   laeuft deshalb in JEDEM Spiel, auch ohne Spielschicht. Ein vorhandenes
            //   Nutzerskript wird nie ueberschrieben - dann bleibt der Ordner, wie er ist.
            if (Directory.GetFiles(ziel, "*.lua", SearchOption.AllDirectories).Length == 0)
            {
                var q = Path.Combine(PaketWurzel(), Path.Combine(Pfade.SkriptOrdner, Pfade.HelloLua));
                if (File.Exists(q))
                {
                    File.Copy(q, Path.Combine(ziel, Pfade.HelloLua), true);
                    e.Sag("example installed: " + Pfade.SkriptOrdner + Pfade.T + Pfade.HelloLua
                          + " (delete it once you have your own)");
                }
            }
        }

        private static void Abschlusspruefung(string spiel, string tfm, Ergebnis e)
        {
            var fehlt = new List<string>();
            void P(string p) { if (!File.Exists(p)) fehlt.Add(p.Substring(spiel.Length).TrimStart('\\')); }

            P(Path.Combine(Pfade.CoreTfm(spiel, tfm), Pfade.PreloaderDll));
            P(Path.Combine(spiel, Pfade.DoorstopDll));
            P(Path.Combine(spiel, Pfade.DoorstopCfg));
            P(Path.Combine(Pfade.LizenzOrdner(spiel), "UnityDoorstop.LICENSE.txt"));

            if (fehlt.Count > 0)
            {
                e.Stop("Installation incomplete - missing: " + string.Join(", ", fehlt.ToArray()));
                return;
            }

            // ⚠ Und die Zusicherung, die eine Umbenennung faengt: der Pfad in der Konfiguration
            //   muss auf eine Datei zeigen, DIE ES GIBT.
            var ziel = Path.Combine(spiel, Pfade.TargetAssembly(tfm));
            if (!File.Exists(ziel))
            {
                e.Stop("target_assembly points nowhere: " + Pfade.TargetAssembly(tfm) +
                       " - the host would not start at all.");
                return;
            }
            e.Sag("checked: target_assembly resolves to an existing file.");

            if (tfm == "net6" && !Directory.Exists(Path.Combine(Pfade.W(spiel), Pfade.Interop)))
            {
                e.Sag("");
                e.Sag("NOTE: " + Pfade.Wurzel + "\\" + Pfade.Interop + "\\ is empty.");
                e.Sag("On an Il2Cpp game the host needs proxy assemblies generated from YOUR game files.");
                e.Sag("They cannot be shipped - they are derived from the game you own.");
            }
        }

        private static void Ordner(string p)   { if (!Directory.Exists(p)) Directory.CreateDirectory(p); }
        private static void Loesche(string p)  { try { Directory.Delete(p, true); } catch { } }
        private static void Verschiebe(string a, string b)
        {
            if (File.Exists(b)) File.Delete(b);
            File.Move(a, b);
        }

        private static void KopiereOrdner(string q, string z)
        {
            Ordner(z);
            foreach (var f in Directory.GetFiles(q))
                File.Copy(f, Path.Combine(z, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(q))
                KopiereOrdner(d, Path.Combine(z, Path.GetFileName(d)));
        }
    }
}
