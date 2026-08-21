using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ScriptOne.Preloader;
using ScriptOne.Host;

// ReSharper disable once CheckNamespace
namespace Doorstop
{
    /// <summary>
    /// Der Einstiegspunkt, den UnityDoorstop ruft. Der Name ist vorgeschrieben:
    /// Doorstop sucht 'Doorstop.Entrypoint.Start()' in der konfigurierten Assembly.
    /// </summary>
    /// <remarks>
    /// Zu diesem Zeitpunkt laeuft CoreCLR, aber Unity ist NOCH NICHT hochgefahren und
    /// die Il2Cpp-Domaene existiert nicht. Alles, was hier passiert, muss ohne das
    /// Spiel auskommen. Der eigentliche Start des Wirts haengt deshalb an einem
    /// Detour, der erst feuert, wenn die Laufzeit steht (siehe Il2CppBootstrap).
    ///
    /// KEIN Il2Cpp-Typ darf in dieser Methode vorkommen: der JIT loest die Typen
    /// beim Betreten auf, also bevor der AssemblyResolve-Haken haengt. Deshalb geht
    /// jeder Schritt ueber einen eigenen [MethodImpl(NoInlining)]-Sprung.
    /// </remarks>
    internal static class Entrypoint
    {
        private static FileLog _log;

        /// <summary>
        /// Startet einen Doorstop-basierten FREMDLADER weiter, dessen Einstieg wir uebernommen
        /// haben. Heute ist das genau BepInEx.
        /// </summary>
        /// <remarks>
        /// ⚠ SCHEITERT DAS, IST ES DER TEUERSTE FEHLER DES GANZEN PRODUKTS: der Nutzer verliert
        /// nicht ScriptOne, sondern JEDES andere Plugin, das er installiert hat. Deshalb faengt
        /// diese Methode alles, meldet in eine eigene Datei (der Logger steht hier noch nicht)
        /// und laesst den eigenen Start trotzdem weiterlaufen - ein halb gestartetes Spiel ist
        /// besser als ein gar nicht gestartetes.
        /// </remarks>
        /// <returns>
        /// <c>true</c>, wenn hier wirklich ein Fremdlader angesprungen ist. Das ist keine
        /// Buchhaltung, sondern die Antwort auf „laeuft ScriptOne allein?" - und davon haengt
        /// ab, ob es sein eigenes Konsolenfenster oeffnet (console = auto).
        /// </returns>
        private static bool StarteFremdenLader(string spielOrdner)
        {
            var pfad = Path.Combine(spielOrdner,
                           Path.Combine("BepInEx", Path.Combine("core", "BepInEx.Preloader.dll")));
            try
            {
                if (!File.Exists(pfad)) return false;   // kein Fremdlader (mehr) - normaler Fall
                var asm = System.Reflection.Assembly.LoadFrom(pfad);
                var typ = asm.GetType("Doorstop.Entrypoint");
                if (typ == null) { NotMeldung(spielOrdner, "BepInEx.Preloader.dll has no Doorstop.Entrypoint"); return false; }
                var m = typ.GetMethod("Start", System.Reflection.BindingFlags.Public
                                             | System.Reflection.BindingFlags.Static);
                if (m == null) { NotMeldung(spielOrdner, "Doorstop.Entrypoint has no static Start()"); return false; }

                // ⚠ OHNE DIESE ZEILE TUT BEPINEX EINFACH NICHTS - ohne Fehler, ohne Log.
                //   BepInEx.Preloader leitet seinen EIGENEN Wurzelordner aus der Umgebung ab:
                //     bepinPath = ParentDirectory(DOORSTOP_INVOKE_DLL_PATH, 2)
                //   (BepInEx.Preloader/Entrypoint.cs, PreloaderPreMain). Doorstop setzt diese
                //   Variable auf das konfigurierte target_assembly - und das zeigt seit der
                //   Uebernahme auf UNS. Zwei Ebenen ueber core-runtime\<tfm>\ landet BepInEx
                //   damit in ScriptOne\ und findet dort keine einzige seiner Dateien.
                //   Gemessen 2026-08-20: Kette lief durch, kein Fehler, BepInEx schrieb keine
                //   Zeile. Wir setzen die Variable also auf DESSEN Pfad, wie Doorstop es taete.
                //
                // ⚠ UND DANACH ZURUECK: unser eigener Zweig liest dieselbe Variable, um sich
                //   selbst zu finden. Wer sie stehen laesst, repariert den Fremdlader und
                //   bricht sich dabei selbst das Genick.
                var vorher = Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH");
                try
                {
                    Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH", Path.GetFullPath(pfad));
                    m.Invoke(null, null);
                }
                finally { Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH", vorher); }
                return true;
            }
            catch (Exception ex)
            {
                NotMeldung(spielOrdner, "could not chain-start BepInEx: " + ex.GetType().Name + ": " + ex.Message);
                // ⚠ FALSE, obwohl eine Datei da war: der Fremdlader ist NICHT hochgekommen, also
                //   bringt er auch keine Ausgabe mit - und der Nutzer braucht unsere umso mehr.
                return false;
            }
        }

        /// <summary>
        /// Eine Meldung, bevor es einen Logger gibt - und an einen Ort, den ein Nutzer findet.
        /// </summary>
        private static void NotMeldung(string spielOrdner, string text)
        {
            try
            {
                var d = Path.Combine(spielOrdner, "ScriptOne");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                File.AppendAllText(Path.Combine(d, "ScriptOne-CHAIN-PROBLEM.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine +
                    "  ScriptOne took over the Doorstop entry point so it can survive BepInEx being removed," + Environment.NewLine +
                    "  and it is supposed to start BepInEx afterwards. That failed, so your other" + Environment.NewLine +
                    "  BepInEx plugins did NOT load in this session." + Environment.NewLine +
                    "  To undo: copy the original doorstop_config.ini back from" + Environment.NewLine +
                    "  ScriptOne / disabled-loaders / doorstop_config.ini.original, or" + Environment.NewLine +
                    "  doorstop_config.ini next to the game, or run the ScriptOne installer with --remove." + Environment.NewLine);
            }
            catch { }
        }

        public static void Start()
        {
            try
            {
                var spielOrdner = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);

                // ⚠ ALLERERSTES, VOR ALLEM EIGENEN. Steht ScriptOne auf einem Spiel mit BepInEx,
                //   biegt der Installer target_assembly auf UNS - denn BepInEx IST Doorstop und
                //   es gibt nur EINE doorstop_config.ini, die beide lesen wuerden. Wer den
                //   Einstieg uebernimmt, muss den Fremdlader also selbst weiterstarten, sonst
                //   verliert der Nutzer alle seine uebrigen Plugins.
                //
                //   Das ist KEIN Nachbau: BepInEx.Preloader.dll fuehrt denselben Vertrag
                //   'Doorstop.Entrypoint.Start()', den auch Doorstop aufruft (per Bytesuche
                //   belegt). Wir machen exakt denselben Aufruf.
                //
                // ⚠ UND VOR unserem eigenen Wirt, nicht danach: bricht spaeter etwas bei uns,
                //   laeuft BepInEx trotzdem - und traegt ScriptOne dann ueber sein Plugin
                //   weiter. Umgekehrt waere ein Fehler bei uns das Ende fuer ALLE Plugins des
                //   Nutzers.
                var verkettet = StarteFremdenLader(spielOrdner);

                // REIHENFOLGE IST HIER INHALT: erst Konfiguration, dann Konsole, dann das
                // Protokoll. Wer die Konsole spaeter oeffnet, verliert genau die Zeilen, die
                // erklaeren, warum der Start schiefging.
                var wurzel = Path.Combine(spielOrdner, "ScriptOne");
                var cfg = HostConfig.LiesOderLege(Path.Combine(wurzel, "ScriptOne-Starter.cfg"));
                string konsolenGrund = null;
                // ⚠ 'auto' heisst: allein -> Fenster. Ohne Fremdlader gibt es sonst NICHTS zu
                //   sehen, und dieser Zustand entsteht auch ohne jeden Installerlauf - naemlich
                //   wenn der Nutzer den Fremdlader spaeter loescht.
                var konsoleAn = cfg.KonsoleAn(!verkettet);
                if (konsoleAn) HostConsole.Oeffne(out konsolenGrund);

                // Der LAUFENDE Lauf liegt oben, die fuenf vorherigen in logs\ - wie
                // MelonLoaders Latest.log neben seinem Logs-Ordner.
                _log = new FileLog(Path.Combine(wurzel, "ScriptOne.log"),
                                   Path.Combine(wurzel, "logs"));
                _log.Info("preloader alive - CoreCLR is up, Unity is not");
                foreach (var m in cfg.Meldungen) _log.Info(m);
                if (konsoleAn && !HostConsole.Offen)
                    _log.Warn("console requested but could not be opened - " + (konsolenGrund ?? "no reason given"));
                // Warum das Fenster da ist oder fehlt, gehoert ins Protokoll - sonst sucht der
                // Nutzer den Schalter, der schon richtig steht.
                if (string.Equals(cfg.Console, HostConfig.Auto, StringComparison.OrdinalIgnoreCase))
                    _log.Info("console = auto -> " + (konsoleAn
                        ? "on (ScriptOne runs alone here)"
                        : "off (a chain-started loader brings its own output)"));
                _log.Info("game folder: " + spielOrdner);
                _log.Info("runtime    : " + RuntimeInformation.FrameworkDescription);

                Bootstrap.Run(spielOrdner, _log, cfg);
            }
            catch (Exception ex)
            {
                // Ohne Loader gibt es niemanden, der einen Fehler hier anzeigt.
                // Deshalb landet er in einer Datei, die auch dann existiert, wenn
                // sonst nichts mehr passiert.
                try
                {
                    var notfall = Path.Combine(Path.GetTempPath(), "ScriptOne-preloader-crash.log");
                    File.WriteAllText(notfall, DateTime.Now + Environment.NewLine + ex);
                    if (_log != null) _log.Exception("Entrypoint.Start", ex);
                }
                catch { }
            }
        }
    }
}

namespace ScriptOne.Preloader
{
    internal static class Bootstrap
    {
        /// <summary>
        /// Erst hier darf Il2Cpp-Code auftauchen - der Resolver haengt zu diesem
        /// Zeitpunkt bereits.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Run(string spielOrdner, FileLog log, HostConfig cfg)
        {
            var interopOrdner = FindeInteropAssemblies(spielOrdner, log);
#if IL2CPP
            if (interopOrdner == null)
            {
                log.Error("no Il2Cpp interop assemblies found - cannot continue.");
                log.Error("expected a folder with Assembly-CSharp.dll and Il2Cppmscorlib.dll");
                return;
            }
#endif
            // ⚠ DER WAECHTER DARUEBER STEHT IN #if IL2CPP - im MONO-Bau laeuft es hier mit
            //   interopOrdner == null weiter, und der AssemblyResolve-Haken unten ruft dann
            //   Path.Combine(null, ...): ArgumentNullException, geworfen INNERHALB eines
            //   Resolve-Handlers. Aufgefallen ist es nie, weil der Mono-Zweig bis heute nie
            //   so weit kam (gemeldet von luaHELPER, 2026-08-19). Ohne Interop-Ordner braucht
            //   der Mono-Zweig auch keinen Haken: die Spielassemblies sind schon geladen.
            log.Info("interop assemblies: " + (interopOrdner ?? "(none - not needed on Mono)"));

            // Passen sie zum installierten Spielstand? Nur MELDEN - erzeugen ist ein eigener,
            // bewusster Schritt und gehoert nicht in einen Spielstart. Begruendung: InteropStamp.
            if (cfg.CheckInterop && interopOrdner != null) InteropStamp.Pruefe(spielOrdner, interopOrdner, log);

            // Il2CppInterop sucht seine .db-Dateien ueber diese Variable.
            if (interopOrdner != null)
                Environment.SetEnvironmentVariable("IL2CPP_INTEROP_DATABASES_LOCATION", interopOrdner);

            // Die Proxy-Assemblies liegen nicht neben uns - ohne diesen Haken findet
            // die Laufzeit weder Assembly-CSharp noch Il2Cppmscorlib.
            if (interopOrdner != null)
            {
                var ordner = interopOrdner;
                AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
                {
                    // ⚠ EIN RESOLVE-HAKEN DARF NIE WERFEN. Er laeuft mitten in der
                    //   Assembly-Aufloesung der LAUFZEIT - eine Ausnahme hier trifft nicht uns,
                    //   sondern denjenigen, der gerade etwas laden wollte, und das ist meistens
                    //   das Spiel. Rueckgabe null heisst "ich kann nicht helfen" und ist immer
                    //   sicher; werfen ist es nie.
                    try
                    {
                        var name = new AssemblyName(e.Name).Name;
                        var pfad = Path.Combine(ordner, name + ".dll");
                        return File.Exists(pfad) ? Assembly.LoadFrom(pfad) : null;
                    }
                    catch { return null; }
                };
            }

            // MoonSharp kommt aus der eingebetteten Ressource, wie im Plugin auch.
            ScriptOne.Host.EmbeddedAssemblies.Install();

#if IL2CPP
            Il2CppBootstrap.Start(spielOrdner, interopOrdner, log, cfg);
#else
            // Auf Mono gibt es keine Interop-Schicht - der Wirt startet direkt.
            MonoBootstrap.Start(spielOrdner, log, cfg);
#endif
        }

        /// <summary>
        /// Sucht die erzeugten Proxy-Assemblies. Reihenfolge mit Absicht: der eigene
        /// Ordner zuerst, damit eine spaetere EIGENE Erzeugung die fremde ablöst,
        /// ohne dass hier etwas geaendert werden muss.
        /// </summary>
        /// <remarks>
        /// Der eigene Ordner heisst 'interopgenerator' - er traegt, was der eigene Erzeuger
        /// hervorgebracht hat, und bewusst NICHT wie bei
        /// MelonLoader ('Il2CppAssemblies'). Zwei verschieden benannte Ordner sind hier ein
        /// Merkmal, kein Schoenheitsfehler: wer in einem Spielordner steht, sieht am Namen
        /// sofort, WESSEN Proxies er vor sich hat. Der alte eigene Name bleibt als zweiter
        /// Kandidat stehen, damit eine Installation von vor dem Umbau weiterlaeuft.
        /// </remarks>
        private static string FindeInteropAssemblies(string spielOrdner, FileLog log)
        {
            var kandidaten = new[]
            {
                Path.Combine(spielOrdner, "ScriptOne", "interopgenerator"),
                Path.Combine(spielOrdner, "ScriptOne", "interop"),            // Stand vor 2026-08-19
                Path.Combine(spielOrdner, "ScriptOne", "Il2CppAssemblies"),   // Stand vor 2026-08-18
                Path.Combine(spielOrdner, "MelonLoader", "Il2CppAssemblies"),
                Path.Combine(spielOrdner, "BepInEx", "interop"),
            };
            foreach (var k in kandidaten)
            {
                if (File.Exists(Path.Combine(k, "Assembly-CSharp.dll")) &&
                    File.Exists(Path.Combine(k, "Il2Cppmscorlib.dll")))
                    return k;
                log.Info("  not here: " + k);
            }
            return null;
        }
    }
}
