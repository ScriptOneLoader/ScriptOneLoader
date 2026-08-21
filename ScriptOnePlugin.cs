using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using ScriptOne.Host;

// MelonLoaders statischer Referenz-Scan liest die Assembly-Verweise der DLL, BEVOR
// irgendein AssemblyResolve haengt - eine eingebettete Assembly sieht er deshalb
// grundsaetzlich nie. Ohne diese Zeile meldet der Loader
//     'ScriptOne' is missing the following dependencies: 'MoonSharp.Interpreter' v2.0.0.0
// und kuendigt in derselben Meldung an, dass daraus kuenftig ein LADEFEHLER wird.
// Gefunden nur durch den Lauf im echten Spiel: der spielfreie Pruefstand ist fuer diese
// Fehlerklasse strukturell blind, weil er den Loader umgeht.
// ⚠ UND DIE SPIEL-ASSEMBLIES GEHOEREN GENAUSO HIERHIN - das war bis 2026-08-20 nicht der Fall.
// ScriptOne ist ein Wirt fuer BELIEBIGE Unity-Spiele; in jedem anderen Spiel als Schedule I
// fehlen Assembly-CSharp, Il2Cppmscorlib und UnityEngine.CoreModule in der Fassung, gegen die
// gebaut wurde - zu Recht, denn die eingebauten Schedule-I-Bindungen sind dort schlicht nicht
// anwendbar (GameGate faengt das zur Laufzeit ab). MelonLoader sieht davon nichts: sein
// Referenz-Scan ist statisch. Gemessen in Shadows of Doubt am 2026-08-20:
//     'ScriptOne' is missing the following dependencies:
//         'Assembly-CSharp' v0.0.0.0 / 'Il2Cppmscorlib' v4.0.0.0 / 'UnityEngine.CoreModule' v0.0.0.0
// Der Mod lief trotzdem - aber der Loader kuendigt in derselben Meldung an, dass er Melons mit
// fehlenden Abhaengigkeiten kuenftig NICHT MEHR LAEDT. Ohne diese Zeile waere ScriptOne also
// mit dem naechsten MelonLoader in jedem fremden Spiel tot, und zwar auf einen Schlag.
[assembly: MelonLoader.MelonOptionalDependencies(
    "MoonSharp.Interpreter",
    "Assembly-CSharp",
    "Il2Cppmscorlib",
    "UnityEngine.CoreModule",
    "Il2CppScheduleOne.Core",
    "Il2CppFishNet.Runtime")]

namespace ScriptOne
{
    /// <summary>
    /// ScriptOne - der Lua-Interpreter fuer Schedule I. KEIN Mod: Infrastruktur.
    /// </summary>
    /// <remarks>
    /// WARUM PLUGIN UND NICHT MOD
    /// Ein Mod liegt in Mods\ und ist eine Erweiterung des Spiels. Dieses Ding ist eine
    /// LAUFZEIT - es erweitert das Spiel um gar nichts, es fuehrt fremde Skripte aus. Die
    /// Erweiterungen sind die .lua-Dateien in LuaScripts\. Deshalb Plugins\:
    ///   * gemessen wird Plugins\ rund 2 Sekunden VOR Mods\ geladen
    ///     (Latest.log 2026-08-17: 20:23:34.7 gegen 20:23:36.3)
    ///   * Mods\ bleibt frei fuer das, was wirklich ein Mod ist
    ///   * die Trennung ist auch fuer den Nutzer sichtbar richtig: eine Datei, die man
    ///     einmal hinlegt und nie wieder anfasst, gehoert nicht zwischen die Mods
    ///
    /// WAS EIN PLUGIN NICHT HAT
    /// Ein MelonPlugin kann OnSceneWasInitialized NICHT ueberschreiben - das ist
    /// MelonMod-spezifisch. Der Weg fuehrt ueber MelonEvents (dieselbe Loesung wie in
    /// ModProfiler, der aus demselben Grund Plugin ist).
    ///
    /// Diese Klasse darf KEINEN MoonSharp-Typ nennen: der JIT wuerde die Assembly sonst
    /// schon beim Betreten der Methode suchen, also bevor der Resolver haengt.
    /// </remarks>
    public sealed class ScriptOnePlugin : MelonPlugin
    {
        /// <summary>Skriptordner NEBEN der exe - nicht unter UserData, nicht unter Mods.</summary>
        private const string ScriptFolderName = "LuaScripts";

        private object _engine;
        private string _scriptFolder;

        public override void OnApplicationStarted()
        {
            // ⚠ Seit die Standalone-Installation die Plugin-Kopie VORSORGLICH mitlegt (damit
            //   ScriptOne einen spaeter installierten Lader ueberlebt), koennen zwei
            //   Einstiegspunkte gleichzeitig scharf sein. Frueher wurde das durch Verschieben
            //   der Datei verhindert - das loeste aber das falsche Problem und kostete genau
            //   die Eigenschaft, die man braucht. Jetzt entscheidet der ERSTE, der da ist.
            string ersterWar;
            if (!ScriptOne.Host.HostGuard.Beanspruche("MelonLoader plugin", out ersterWar))
            {
                LoggerInstance.Warning("another ScriptOne host is already running in this process ("
                                       + ersterWar + ") - standing down. This one does nothing.");
                return;
            }
            EmbeddedAssemblies.Install();

            // MelonEnvironment.GameRootDirectory ist der Ordner der exe.
            _scriptFolder = Path.Combine(MelonEnvironment.GameRootDirectory, ScriptFolderName);
            // Der Zustand der Skripte gehoert NICHT neben die .lua-Dateien: die kopiert ein
            // Nutzer weiter, den Spielstandbezug will er dabei nicht mitnehmen.
            var stateFolder = Path.Combine(MelonEnvironment.UserDataDirectory, "ScriptOne");

            // Die Referenz gehoert NEBEN das Spiel, nicht unter UserData: sie beantwortet
            // "was kann ich hier modden", und wer das fragt, sucht im Spielordner - nicht in
            // einem Loader-Unterordner, dessen Existenz er nicht kennt. Damit liegt sie im
            // Plugin-Bau am selben Ort wie im Standalone-Bau.
            var docFolder = Path.Combine(MelonEnvironment.GameRootDirectory, "ScriptOne", "documentation");
            // DIESELBE Konfigurationsdatei wie im Standalone-Bau.
            var cfg = Path.Combine(MelonEnvironment.GameRootDirectory, "ScriptOne", "ScriptOne-Starter.cfg");

            // ⚠ DIE DIAGNOSE MUSS DEN ABSTURZ UEBERLEBEN. Vorher stand die LastError-Zeile NACH
            //   dem Bootaufruf - genau der Fall, in dem sie gebraucht wird (der Boot wirft), war
            //   der einzige, in dem sie nie lief. Gemessen am 2026-08-19 in einem fremden Spiel:
            //   im Log stand eine TypeLoadException und sonst nichts, waehrend der Wirt die
            //   Antwort kannte. Ausserdem beendet ein Wurf hier den Plugin-Start - ein fremdes
            //   Spiel soll aber weiterlaufen, auch wenn ScriptOne nicht kann.
            try
            {
                _engine = LuaBoot.Create(LoggerInstance, _scriptFolder, stateFolder, docFolder, cfg);
            }
            catch (System.Exception ex)
            {
                // ⚠ DIE INNERE KETTE IST DIE AUSSAGE. Eine TypeInitializationException nennt nur
                //   den Typ, dessen Initialisierung scheiterte - WARUM steht erst in der inneren.
                //   Ohne sie las das Log wie "List`1 ist kaputt", waehrend die Ursache eine
                //   Assembly zwei Ebenen tiefer war.
                for (var e2 = ex; e2 != null; e2 = e2.InnerException)
                    LoggerInstance.Error("ScriptOne could not start: " + e2.GetType().Name + ": " + e2.Message);
                Diagnose();
                LoggerInstance.Error("The game keeps running; no scripts are executed.");
                return;
            }
            Diagnose();

            // Plugins haben kein OnSceneWasInitialized-Override - siehe Klassenkommentar.
            // Beim Verlassen eines Spielstands raeumt das Spiel auf und setzt dabei u. a.
            // PlayerMovement.StaticMoveSpeedMultiplier auf 1f zurueck (LoadManager.cs:887);
            // ein neu geladener Spielstand muss 'game_ready' deshalb erneut bekommen.
            try { MelonEvents.OnSceneWasInitialized.Subscribe(OnSceneInitialized); } catch { }

            // ⚠ Auch DIESE Zeile nennt einen Spieltyp: GameBridge fuehrt Felder aus dem Spiel,
            //   und der JIT laedt sie beim Betreten. In einem fremden Spiel ist das die
            //   naechste TypeLoadException nach der behobenen - also fragen, nicht annehmen.
            // Ueber dasselbe Tor: ohne Spielschicht steht dort "core-only", nicht der Backendname.
            // Vorher meldete die Zeile "ScriptOne ready (Mono)" auch dann, wenn die Sonde
            // gerade das Gegenteil ins Log geschrieben hatte - zwei Zeilen, zwei Wahrheiten.
            var backend = GameGate.BackendName();
            // Gegenprobe zum Takt: wer nach dem Start nie ein OnUpdate sieht, soll es LESEN.
            MelonEvents.OnUpdate.Subscribe(Taktwaechter);

            LoggerInstance.Msg("ScriptOne ready (" + backend + ") - "
                               + LuaBoot.ScriptCount(_engine) + " script(s) from " + _scriptFolder);
        }

        /// <summary>Sagt im Log, was der Assembly-Haken erlebt hat.</summary>
        /// <remarks>
        /// Die beiden Faelle sehen von aussen gleich aus und verlangen gegensaetzliche Abhilfen:
        /// 0 Anfragen = die Laufzeit fragt uns gar nicht; Anfragen mit Fehler = die Nutzlast passt
        /// nicht. Deshalb steht beides im Log, auch im Erfolgsfall (eine Zeile).
        /// </remarks>
        private void Diagnose()
        {
            LoggerInstance.Msg("embedded MoonSharp: " +
                               (EmbeddedAssemblies.Vorgeladen ? "preloaded at startup" : "PRELOAD FAILED") +
                               ", resolve hook asked " + EmbeddedAssemblies.Anfragen + " time(s)" +
                               (EmbeddedAssemblies.Gesehen == null ? "" : " - " + EmbeddedAssemblies.Gesehen));
            if (EmbeddedAssemblies.LastError != null)
                LoggerInstance.Error("Embedded MoonSharp could not be loaded: " + EmbeddedAssemblies.LastError);
        }

        /// <summary>Meldet nach rund 300 Durchlaeufen, falls der eigene OnUpdate nie kam.</summary>
        /// <remarks>
        /// MelonEvents.OnUpdate feuert auch fuer Plugins - der Override OnUpdate nicht
        /// zwangslaeufig. Genau diese Differenz war der Fehler, und sie ist von aussen
        /// unsichtbar, weil beides "der Mod laeuft" bedeutet.
        /// </remarks>
        private void Taktwaechter()
        {
            if (_tickGesehen || _engine == null) return;
            if (++_tickWaechter < 300) return;
            _tickGesehen = true;
            LoggerInstance.Warning("no OnUpdate from the plugin base after 300 frames - timers would be dead. "
                                   + "Falling back to MelonEvents.OnUpdate.");
        }

        private void OnSceneInitialized(int buildIndex, string sceneName)
        {
            if (_engine == null) return;
            LuaBoot.ResetGameReady(_engine);
        }

        public override void OnApplicationQuit()
        {
            // ⚠ VOR dem Engine-Test: der zweite Meldeweg der Taktwache muss auch dann laufen,
            //   wenn gar kein Wirt zustande kam - sonst schweigt genau der Fall, der am
            //   ehesten schiefgeht.
            ScriptOne.Host.TaktWache.Schluss(LoggerInstance.Warning, "ScriptOne runs as a MelonLoader plugin. Its tick comes from MelonLoader's own"
                + " update loop; if that never reaches this plugin, MelonEvents.OnUpdate is"
                + " used as a fallback - if even that stays silent, the loader itself is not"
                + " pumping updates in this game.");
            if (_engine == null) return;
            // Ungesichertes Skriptgedaechtnis wegschreiben und die Ereignisabos loesen.
            LuaBoot.SaveAll(_engine);
            // ⚠ NICHT ungeschuetzt: Detach() nennt Spieltypen, und in einem FREMDEN Spiel gibt es
            //   die nicht - gemessen 2026-08-19 als TypeLoadException auf
            //   'ScheduleOne.PlayerScripts.Player' beim Beenden von No Knock. Der JIT loest die
            //   Typen erst beim Betreten der Methode auf, ein try um den AUFRUF faengt es also.
            // Ueber dasselbe Tor wie alles andere - GameGate kennt den Zustand der Sonde und
            // nennt den Spieltyp erst in einer eigenen NoInlining-Methode.
            GameGate.Abloesen();
        }

        private bool _tickGesehen;
        private int _tickWaechter;

        public override void OnUpdate()
        {
            if (_engine == null) return;

            // ⚠ "EINGEHAENGT" IST KEINE AUSSAGE UEBER "FEUERT". Ohne diese Zeile meldet ein toter
            //   Frame-Takt einen fehlerfreien Start: Skripte werden geladen und protokolliert,
            //   waehrend Zeitgeber und s1.every still nie laufen. Genau das war am 2026-08-19 im
            //   fremden Spiel der Fall - der Wirt stand, kein Zeitgeber feuerte, und im Log war
            //   kein Unterschied zu sehen.
            ScriptOne.Host.TaktWache.Bild();
            if (!_tickGesehen)
            {
                _tickGesehen = true;
                LoggerInstance.Msg("frame tick alive - timers and s1.every will run");
            }
            // Kostet nach dem einmaligen Feuern nur noch einen bool-Vergleich.
            LuaBoot.Poll(_engine);
            // Faellige Zeitgeber. Ohne Zeitgeber ist das eine Bereichspruefung auf einer leeren Liste.
            LuaBoot.Tick(_engine);
        }
    }
}
