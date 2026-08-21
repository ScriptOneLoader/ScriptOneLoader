using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using MoonSharp.Interpreter;
using ScriptOne.Game;

namespace ScriptOne.Host
{
    /// <summary>
    /// Der Lua-Wirt: ein MoonSharp-Interpreter je Skriptdatei, eine winzige feste Flaeche
    /// unter dem Global "s1", und kein einziger Spieltyp jenseits von GameBridge.
    /// </summary>
    internal sealed class LuaEngine
    {
        private const long StepBudget = 2000000;

        private readonly IScriptLog _log;
        private readonly List<LoadedScript> _scripts = new List<LoadedScript>();
        private readonly Timers _timers = new Timers();
        private readonly string _stateFolder;
        private readonly string _docFolder;
        private readonly string _konsolenStufe;
        private readonly string _flaechenStufe;
        private readonly string _szeneStufe;

        /// <summary>Zusatzmeldungen fuer Fachleute - siehe <see cref="HostSchalter.Debug"/>.</summary>
        internal bool Debug { get; private set; }
        private bool _gameReadyFired;
        private bool _docsGeschrieben;
        private bool _listeGeprueft;

        /// <param name="docFolder">
        /// Wohin die erzeugte Referenz geht. <c>null</c> schaltet sie ab - der Harness
        /// braucht sie nicht, und ein Testlauf soll nichts in einen Spielordner schreiben.
        /// </param>
        /// <param name="konsolenStufe">
        /// safe | extended | unrestricted. Vorgabe ist die SICHERE - eine Auslieferung, die
        /// im Zweifel mehr erlaubt, ist die falsche Vorgabe.
        /// </param>
        /// <summary>
        /// Die Flaechendatei liegt in der WURZEL des Wirts, nicht im documentation-Ordner:
        /// documentation\ wird bei jedem Start neu geschrieben, die Flaeche aber beim Setup
        /// erzeugt. Zwei verschiedene Lebensdauern gehoeren nicht in denselben Ordner.
        /// </summary>
        private string Flaechendatei()
        {
            if (string.IsNullOrEmpty(_docFolder)) return null;
            var getrimmt = _docFolder.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                                              System.IO.Path.AltDirectorySeparatorChar);
            var wurzel = System.IO.Path.GetDirectoryName(getrimmt);
            if (string.IsNullOrEmpty(wurzel)) return null;
            return System.IO.Path.Combine(wurzel, "surface.txt");
        }

        /// <summary>
        /// Der Weg fuer die Testbank und fuer Aufrufer ohne Konfigurationsdatei.
        /// </summary>
        internal LuaEngine(IScriptLog log, string stateFolder, string docFolder = null)
            : this(log, stateFolder, docFolder, HostSchalter.Vorgabe) { }

        internal LuaEngine(IScriptLog log, string stateFolder, string docFolder, HostSchalter schalter)
        {
            if (schalter == null) schalter = HostSchalter.Vorgabe;
            _log = log;
            _stateFolder = stateFolder;
            _docFolder = docFolder;
            _konsolenStufe = string.IsNullOrEmpty(schalter.Konsole) ? ConsolePolicy.Sicher : schalter.Konsole;
            _flaechenStufe = string.IsNullOrEmpty(schalter.Flaeche) ? SurfacePolicy.Normal : schalter.Flaeche;
            _szeneStufe    = string.IsNullOrEmpty(schalter.Szene)   ? SurfacePolicy.SzeneAuto : schalter.Szene;
            Debug = schalter.Debug;
            _log.Info(ConsolePolicy.Startmeldung(_konsolenStufe));
            // Gegenrichtung anschliessen: Spielereignisse kommen flach herein und werden an
            // die Skripte verteilt. Genau EIN Abo fuer alle Skripte.
            // ⚠ ERST FRAGEN, OB ES DIE SPIELSCHICHT HIER GIBT - bevor irgendetwas sie anfasst.
            //   Vorher stand hier direkt 'EventBridge.Raised = ...', und weil der JIT die Typen
            //   einer Methode BEIM BETRETEN aufloest, starb dieser Konstruktor in einem fremden
            //   Spiel, bevor seine erste Zeile lief.
            // ⚠ NUR EINE MELDUNG. Die Sonde in GameLayer schreibt bereits den vollstaendigen
            //   Satz samt Grund; hier stand dieselbe Aussage ein zweites Mal, mit demselben
            //   angehaengten Grund. Zwei Warnungen fuer EINEN Sachverhalt lesen sich wie zwei
            //   Probleme - gemeldet vom Autor am 2026-08-19.
            GameGate.Pruefe(_log);

            GameGate.Anschliessen(Dispatch, m => _log.Info(m));
        }

        private sealed class LoadedScript
        {
            internal string Name;
            internal Script Script;
            internal ExecutionBudget Budget;
            /// <summary>Ereignisname -> Rueckrufe. Ein Skript darf mehrere je Ereignis haben.</summary>
            internal readonly Dictionary<string, List<DynValue>> Handlers =
                new Dictionary<string, List<DynValue>>(StringComparer.Ordinal);
            internal bool Broken;
            internal ScriptStore Store;
        }

        internal int ScriptCount
        {
            get { return _scripts.Count; }
        }

        // ---------------------------------------------------------------- laden

        internal void LoadFolder(string folder)
        {
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                _log.Info("Created script folder: " + folder);
                return;
            }

            var files = Directory.GetFiles(folder, "*.lua", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var name = MakeRelative(folder, file);
                try
                {
                    var entry = new LoadedScript { Name = name };
                    entry.Budget = new ExecutionBudget(StepBudget);

                    // Preset_HardSandbox allein ist ZU hart: gemessen fehlen dort pcall/xpcall,
                    // setmetatable/getmetatable und die raw*-Funktionen. Ohne Metatabellen kann
                    // ein Skriptautor keine eigenen Typen bauen, ohne pcall keine Fehler abfangen.
                    // Metatables|ErrorHandling ergaenzen genau das - gemessen ohne dass io, os,
                    // load, loadfile, dofile, require, json, dynamic oder coroutine zurueckkommen.
                    entry.Script = new Script(CoreModules.Preset_HardSandbox
                                              | CoreModules.Metatables
                                              | CoreModules.ErrorHandling);
                    entry.Script.AttachDebugger(entry.Budget);
                    entry.Store = new ScriptStore(_stateFolder, name);
                    InstallSurface(entry);

                    var source = File.ReadAllText(file);
                    RunGuarded(entry, () => entry.Script.DoString(source, null, name));

                    if (!entry.Broken)
                    {
                        _scripts.Add(entry);
                        _log.Info("Loaded script: " + name);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error("Failed to load script '" + name + "': " + ex.Message);
                }
            }

            // ⚠ DIE DOKUMENTATION DARF NICHT AN EINEM SKRIPT HAENGEN. Sie entstand bisher in
            //   InstallSurface, also nur, wenn schon eine .lua-Datei da war - genau umgekehrt,
            //   als sie gebraucht wird: wer noch nichts geschrieben hat, bekommt nichts zu lesen,
            //   und wer schon schreibt, braucht sie am wenigsten. Gemeldet vom Autor nach einem
            //   sauberen Standalone-Start (2026-08-19): "mir fehlt die documentation".
            //   Also notfalls eine Flaeche NUR fuer die Doku bauen und wieder wegwerfen.
            if (!_docsGeschrieben && _docFolder != null)
            {
                try
                {
                    var nur = new LoadedScript { Name = "(documentation)" };
                    nur.Budget = new ExecutionBudget(StepBudget);
                    nur.Script = new Script(CoreModules.Preset_HardSandbox
                                            | CoreModules.Metatables
                                            | CoreModules.ErrorHandling);
                    nur.Store = new ScriptStore(_stateFolder, nur.Name);
                    InstallSurface(nur);
                }
                catch (Exception ex)
                {
                    _log.Warn("could not write the API reference: " + ex.Message);
                }
            }

            if (files.Length == 0)
            {
                // KEINE Warnung: ein leerer Skriptordner ist der AUSLIEFERUNGSZUSTAND, kein
                // Fehler. Der Installer legt ihn absichtlich leer an.
                _log.Info("No .lua files yet - put yours into " + folder);
                if (_docFolder != null)
                    _log.Info("What you can call in THIS game is written to " + _docFolder);
            }
        }

        private static string MakeRelative(string root, string file)
        {
            if (file.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return file.Substring(root.Length).TrimStart('\\', '/');
            return Path.GetFileName(file);
        }

        // ---------------------------------------------------------------- Flaeche

        private void InstallSurface(LoadedScript entry)
        {
            var s = entry.Script;
            var api = new Table(s);

            api.Set("backend", DynValue.NewString(GameGate.BackendName()));

            api.Set("log", DynValue.NewCallback((ctx, a) =>
            {
                _log.Info("[" + entry.Name + "] " + Text(a, 0));
                return DynValue.Nil;
            }));

            api.Set("warn", DynValue.NewCallback((ctx, a) =>
            {
                _log.Warn("[" + entry.Name + "] " + Text(a, 0));
                return DynValue.Nil;
            }));

            // Eine Zeile Lua geht in die Entwicklerkonsole des Spiels - aber nur, wenn die
            // Positivliste sie durchlaesst. Begruendung der Einteilung: ConsolePolicy.
            api.Set("console", DynValue.NewCallback((ctx, a) =>
            {
                var line = Text(a, 0);
                var grund = ConsolePolicy.Ablehnungsgrund(line, _konsolenStufe);
                if (grund != null)
                {
                    // MELDEN, nicht still verschlucken. Ein Skript, dessen Befehl wortlos
                    // wirkungslos bleibt, sieht fuer den Nutzer wie ein Spielfehler aus -
                    // genau die Verwechslung, die die stille Host-Sperre des Spiels schon
                    // einmal erzeugt (dort tut SubmitCommand fuer Gaeste wortlos nichts).
                    _log.Warn("[" + entry.Name + "] console command rejected: " + grund);
                    return DynValue.NewBoolean(false);
                }
                if (!GameGate.SubmitConsole(line))
                {
                    _log.Warn("[" + entry.Name + "] s1.console is not available in this game");
                    return DynValue.NewBoolean(false);
                }
                return DynValue.NewBoolean(true);
            }));

            api.Set("move_speed", DynValue.NewCallback((ctx, a) =>
                DynValue.NewNumber(GameGate.MoveSpeed())));

            api.Set("on", DynValue.NewCallback((ctx, a) =>
            {
                var evt = Text(a, 0);
                var fn = a.Count > 1 ? a[1] : DynValue.Nil;
                if (fn.Type != DataType.Function)
                {
                    _log.Warn("[" + entry.Name + "] s1.on('" + evt + "', ...) needs a function");
                    return DynValue.NewBoolean(false);
                }
                var bekannt = GameGate.BekannteEreignisse();
                if (Array.IndexOf(bekannt, evt) < 0)
                {
                    _log.Warn("[" + entry.Name + "] unknown event '" + evt + "' (known: "
                              + (bekannt.Length == 0 ? "none in this game" : string.Join(", ", bekannt)) + ")");
                    return DynValue.NewBoolean(false);
                }
                List<DynValue> liste;
                if (!entry.Handlers.TryGetValue(evt, out liste))
                {
                    liste = new List<DynValue>();
                    entry.Handlers[evt] = liste;
                }
                liste.Add(fn);
                return DynValue.NewBoolean(true);
            }));

            // ---- Zeit ----------------------------------------------------------
            api.Set("after", DynValue.NewCallback((ctx, a) => Timer(entry, a, false)));
            api.Set("every", DynValue.NewCallback((ctx, a) => Timer(entry, a, true)));
            api.Set("cancel", DynValue.NewCallback((ctx, a) =>
                DynValue.NewBoolean(_timers.Cancel(entry, (int)Number(a, 0)))));

            // ---- Gedaechtnis ---------------------------------------------------
            api.Set("get", DynValue.NewCallback((ctx, a) =>
            {
                var key = Text(a, 0);
                var fallback = a.Count > 1 ? a[1] : DynValue.Nil;
                var v = entry.Store.Get(key, null);
                if (v == null) return fallback;
                if (v is bool)   return DynValue.NewBoolean((bool)v);
                if (v is double) return DynValue.NewNumber((double)v);
                return DynValue.NewString(Convert.ToString(v, CultureInfo.InvariantCulture));
            }));

            api.Set("set", DynValue.NewCallback((ctx, a) =>
            {
                var key = Text(a, 0);
                var v = a.Count > 1 ? a[1] : DynValue.Nil;
                // Nur flache Werte - dieselbe Grenzregel wie ueberall im Wirt.
                if (v.Type == DataType.Number)       entry.Store.Set(key, v.Number);
                else if (v.Type == DataType.Boolean) entry.Store.Set(key, v.Boolean);
                else if (v.Type == DataType.String)  entry.Store.Set(key, v.String);
                else
                {
                    _log.Warn("[" + entry.Name + "] s1.set('" + key + "', ...): only numbers, "
                              + "strings and booleans can be stored (got " + v.Type + ")");
                    return DynValue.NewBoolean(false);
                }
                return DynValue.NewBoolean(true);
            }));

            api.Set("save", DynValue.NewCallback((ctx, a) =>
            {
                try { return DynValue.NewBoolean(entry.Store.Save()); }
                catch (Exception ex)
                {
                    _log.Error("[" + entry.Name + "] could not save state: " + ex.Message);
                    return DynValue.NewBoolean(false);
                }
            }));

            // Erzeugte Flaeche anhaengen: s1.<manager>.<member>, Umfang siehe docs\API.md, aus
            // Host\Generated\GeneratedSurface.g.cs (tools\Gen-Bindings.ps1).
            var gebunden = GameGate.InstalliereErzeugteFlaeche(s, api);

            // Und die Flaeche als DATEN, aus der Datei, die beim NUTZER aus SEINEM Spiel
            // entstanden ist.
            // WARNUNG: Dieser Aufruf steht ABSICHTLICH NICHT hinter GameGate/_bereit. Die Sonde
            //   beantwortet "ist SCHEDULE I da" - in jedem anderen Spiel sagt sie nein, und
            //   haenge man die Datei-Flaeche daran, waere sie genau dort tot, wo sie gebraucht
            //   wird. Gemessen 2026-08-20 in No Knock: "tables: 0 | members: 0", waehrend die
            //   Assembly-CSharp.dll des Spiels danebenlag.
            var ausDatei = RuntimeSurface.Install(s, api, Flaechendatei(), _log, _flaechenStufe, _szeneStufe);

            api.Set("surface_size", DynValue.NewNumber(gebunden + ausDatei));
            PruefeKernNamen(api);

            // EINMAL je Lauf, aus der ERSTEN fertig gebauten Tabelle. Jedes Skript bekommt
            // seine eigene, aber alle sind gleich gebaut - N-mal zu schreiben kostete nur
            // Zeit und liefe Gefahr, dass zwei Laeufe einander die Datei wegschreiben.
            if (_docFolder != null && !_docsGeschrieben)
            {
                _docsGeschrieben = true;
                var backend = api.Get("backend");
                ApiReference.Schreibe(api, _docFolder,
                    backend.Type == DataType.String ? backend.String : "unknown", _log);
            }

            s.Globals.Set("s1", DynValue.NewTable(api));
        }

        /// <summary>
        /// Sichert zu, dass die erzeugte Flaeche keinen Kernnamen verdeckt hat.
        /// </summary>
        /// <remarks>
        /// ⚠ WARUM DAS NOETIG IST - belegter Fall vom 2026-08-18.
        /// Die Flaeche wird NACH dem Kern installiert und ueberschreibt ihn stillschweigend.
        /// Der Manager 'SaveManager' wurde zu 's1.save' und verdeckte damit die Kernfunktion
        /// s1.save(). Wer der README folgte, bekam 'attempt to call a table value' - in einem
        /// LUA-Skript, also erst zur Laufzeit, und ohne jeden Hinweis auf die Ursache.
        ///
        /// Der Erzeuger reserviert die Kernnamen inzwischen. Diese Pruefung ist die zweite
        /// Sperre: sie haengt nicht am Erzeuger, sondern am fertigen Ergebnis - und faengt
        /// deshalb auch den Fall, dass jemand die Namenskarte von Hand aendert.
        /// </remarks>
        private void PruefeKernNamen(Table api)
        {
            string[] kern = { "log", "warn", "console", "move_speed", "backend", "on",
                              "after", "every", "cancel", "get", "set", "save", "surface_size" };
            foreach (var name in kern)
            {
                var wert = api.Get(name);
                // 'backend' und 'surface_size' sind Werte, alles andere muss aufrufbar sein.
                var sollFunktion = name != "backend" && name != "surface_size";
                if (wert.IsNil())
                    _log.Error("core API '" + name + "' is missing - the generated surface overwrote it");
                else if (sollFunktion && wert.Type != DataType.ClrFunction && wert.Type != DataType.Function)
                    _log.Error("core API 's1." + name + "' is a " + wert.Type +
                               ", not a function - the generated surface overwrote it");
            }
        }

        private static double Number(CallbackArguments a, int i)
        {
            if (a.Count <= i) return 0;
            var v = a[i];
            return v.Type == DataType.Number ? v.Number : 0;
        }

        /// <summary>s1.after / s1.every - legt einen Zeitgeber an und gibt seine Kennung zurueck.</summary>
        private DynValue Timer(LoadedScript entry, CallbackArguments a, bool repeat)
        {
            var sec = Number(a, 0);
            var fn = a.Count > 1 ? a[1] : DynValue.Nil;
            if (fn.Type != DataType.Function)
            {
                _log.Warn("[" + entry.Name + "] s1." + (repeat ? "every" : "after") + "(sec, fn) needs a function");
                return DynValue.NewNumber(0);
            }
            if (_timers.CountFor(entry) >= Timers.MaxPerScript)
            {
                _log.Warn("[" + entry.Name + "] timer limit reached (" + Timers.MaxPerScript
                          + ") - ignoring. Cancel timers you no longer need.");
                return DynValue.NewNumber(0);
            }
            var id = _timers.Add(entry, fn, sec, repeat ? sec : 0);
            return DynValue.NewNumber(id);
        }

        /// <summary>Je Frame vom Wirt gerufen: faellige Zeitgeber ausfuehren.</summary>
        internal void Tick()
        {
            _timers.Tick((owner, handler) =>
            {
                var entry = owner as LoadedScript;
                var fn = handler as DynValue;
                if (entry == null || fn == null || entry.Broken) return;
                RunGuarded(entry, () => entry.Script.Call(fn));
            });
        }

        /// <summary>Beim Beenden: alles Ungesicherte wegschreiben.</summary>
        internal void SaveAll()
        {
            foreach (var entry in _scripts)
            {
                try { if (entry.Store != null && entry.Store.Save()) _log.Info("Saved state: " + entry.Name); }
                catch (Exception ex) { _log.Error("[" + entry.Name + "] state not saved: " + ex.Message); }
            }
        }

        private static string Text(CallbackArguments a, int i)
        {
            if (a.Count <= i) return string.Empty;
            var v = a[i];
            return v.IsNil() ? string.Empty : v.CastToString();
        }

        // ---------------------------------------------------------------- Ereignisse

        internal void PollGameReady()
        {
            if (_gameReadyFired) return;
            if (!GameGate.IsGameReady()) return;

            _gameReadyFired = true;
            // Erst die Spielereignisse anschliessen, dann 'game_ready' melden: ein Skript,
            // das in seinem game_ready-Rueckruf weitere Ereignisse abonniert, soll sie
            // nicht schon verpasst haben.
            GameGate.EreignisseAnschliessen();

            // Erst JETZT ist die Befehlstabelle des Spiels gefuellt (Console.Awake). Vorher
            // gaebe die Gegenprobe eine leere Liste - und damit eine falsche Entwarnung.
            if (!_listeGeprueft)
            {
                _listeGeprueft = true;
                ConsolePolicy.PruefeGegenSpiel(GameGate.Konsolenwoerter(), _konsolenStufe, _log);
            }

            _log.Info("Game is ready - dispatching to " + _scripts.Count + " script(s).");
            Dispatch("game_ready", string.Empty, 0);
        }

        /// <summary>Verteilt ein flaches Spielereignis an alle Skripte, die es abonniert haben.</summary>
        private void Dispatch(string evt, string text, double number)
        {
            foreach (var entry in _scripts)
            {
                if (entry.Broken) continue;
                List<DynValue> liste;
                if (!entry.Handlers.TryGetValue(evt, out liste)) continue;

                foreach (var fn in liste)
                {
                    var handler = fn;
                    RunGuarded(entry, () => entry.Script.Call(handler,
                        DynValue.NewString(text), DynValue.NewNumber(number)));
                }
            }
        }

        /// <summary>Nach Verlassen des Spielstands darf 'game_ready' erneut feuern.</summary>
        internal void ResetGameReady()
        {
            // ⚠ OHNE SPIELSCHICHT BEDEUTET 'game_ready' etwas ANDERES: dort heisst es "der Wirt
            //   steht", und das passiert genau einmal. Mit Spiel heisst es "ein Spielstand ist
            //   geladen" - das darf und soll sich beim Szenenwechsel wiederholen.
            //   Gemessen 2026-08-19 im fremden Spiel: game_ready feuerte beim Start ZWEIMAL
            //   (15:05:35.923 und 15:05:36.408), weil beim Laden zwei Szenen durchliefen und
            //   IsGameReady() ohne Spiel immer true ist. Ein Skript, das dort seine Zeitgeber
            //   aufzieht, haette sie doppelt gehabt.
            if (!GameGate.Bereit) return;

            _gameReadyFired = false;
            // Der Szenenwechsel tauscht die Player-Instanz aus - die Instanz-Abos muessen neu.
            GameGate.InstanzbindungZuruecksetzen();
        }

        // ---------------------------------------------------------------- Ausfuehrung

        /// <summary>
        /// Fuehrt Skriptcode aus und faengt alles ab, was daraus hochkommt.
        /// </summary>
        /// <remarks>
        /// Die InvariantCulture-Klammer ist keine Vorsichtsmassnahme, sondern eine Reparatur.
        /// Gemessen auf einem de-DE-Rechner (MoonSharp 2.0.0):
        ///   tostring(1234.5)      -> "1234.5"   (kulturneutral)
        ///   '' .. 1234.5          -> "1234,5"   (KULTURABHAENGIG)
        ///   tonumber('' .. 1234.5) -> 12345     (stiller Faktor 10)
        /// Ein Skript, das eine Kommazahl per '..' in einen Konsolenbefehl schreibt, wuerde also
        /// auf deutschen Rechnern etwas anderes senden als auf englischen - ohne Fehlermeldung.
        /// Die Klammer wird um JEDEN Sprung ins Skript gelegt und danach zurueckgenommen, damit
        /// die Kultur des Spiels unangetastet bleibt.
        /// </remarks>
        private void RunGuarded(LoadedScript entry, Action body)
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            entry.Budget.Reset();
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                body();
            }
            catch (ScriptBudgetExceededException ex)
            {
                entry.Broken = true;
                _timers.CancelAll(entry);   // sonst feuert ein abgeschaltetes Skript weiter
                _log.Error("[" + entry.Name + "] " + ex.Message + " - script disabled.");
            }
            catch (SyntaxErrorException ex)
            {
                entry.Broken = true;
                _log.Error("[" + entry.Name + "] syntax error: " + ex.DecoratedMessage);
            }
            catch (ScriptRuntimeException ex)
            {
                _log.Error("[" + entry.Name + "] runtime error: " + ex.DecoratedMessage);
            }
            catch (Exception ex)
            {
                _log.Error("[" + entry.Name + "] " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }
    }
}
