using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
#if BEPINEX6
// ⚠ BepInEx 6 fuehrt DIESELBE Klassenform in einem ANDEREN Namensraum und einer anderen
//   Assembly: BepInEx.Unity.Mono.BaseUnityPlugin statt BepInEx.BaseUnityPlugin. Der Rumpf
//   ist identisch (MonoBehaviour, Awake), nur die Herkunft nicht - deshalb genuegt hier ein
//   bedingtes using und es braucht KEINE zweite Kopie dieser Datei. Eine zweite Kopie waere
//   die teuerste Loesung: zwei Wahrheiten, die beim naechsten Fix auseinanderlaufen.
using BepInEx.Unity.Mono;
#endif
// ⚠ NUR fuer MonoBehaviour als BASISTYP unseres Taktobjekts - eine Basisklasse laesst sich
//   nicht per Reflexion angeben. Alles ANDERE an Unity wird in dieser Datei bewusst reflexiv
//   angefasst, weil sie gegen drei verschiedene Referenzsaetze uebersetzt wird.
using UnityEngine;
using ScriptOne.Host;

namespace ScriptOne.Adapters.BepInEx5
{
    /// <summary>
    /// ScriptOne unter BepInEx 5 (Mono).
    /// </summary>
    /// <remarks>
    /// DIESE DATEI IST DER GANZE ADAPTER. Alles, was ScriptOne ausmacht, kommt per Link aus
    /// Host\ und Game\ - hier steht nur, wie BepInEx den Wirt startet, wo seine Ordner liegen
    /// und wie sein Protokoll angeschlossen wird. Waere hier mehr, gaebe es eine zweite
    /// Wahrheit neben dem MelonLoader-Plugin und dem Standalone-Preloader.
    ///
    /// VERTRAG, am Binaerstand nachgeschlagen (BepInEx.dll 5.4.23.5, per Cecil):
    ///   BepInEx.BaseUnityPlugin : UnityEngine.MonoBehaviour
    ///   BepInEx.BepInPlugin(string GUID, string Name, string Version)
    /// Die Basisklasse IST ein MonoBehaviour, ihr Update() ueberbrueckt also den Start.
    ///
    /// ⚠ HIER STAND "Update() genuegt - genau das ist der Unterschied zum Standalone-Zweig, der
    /// sich sein Traegerobjekt erst bauen muss". Beides ist widerlegt: gemessen wurde ein Spiel,
    /// in dem das Traegerobjekt des LADERS waehrend des ersten Szenenladens verschwindet und
    /// Update nie feuert. Dieser Zweig baut sich seitdem bei sceneLoaded ein EIGENES Objekt
    /// (EigenenTaktEinhaengen + BepTick) - also genau das, was der Standalone-Zweig tut, und am
    /// selben Wegpunkt. Der Unterschied ist damit verschwunden.
    ///
    /// ⚠ DER ZEITPUNKT IST HIER UNKRITISCH, anders als im eigenen Lader: BepInEx instanziiert
    /// Plugins per AddComponent, also aus dem laufenden Unity-Player heraus. Die internen
    /// Aufrufe sind da laengst angemeldet - der ganze Wartemechanismus des Preloaders entfaellt.
    ///
    /// ⚠ WAS NICHT HIERHER GEHOERT: eine eigene Spielschicht. Ob dieses Spiel die Bindungen
    /// traegt, entscheidet GameGate zur Laufzeit (Sonde in GameLayer), und zwar fuer alle drei
    /// Einstiege gleich.
    /// </remarks>
    [BepInPlugin(GUID, "ScriptOne", BepVersion.Version)]
    public sealed class Bep5Plugin : BaseUnityPlugin
    {
        internal const string GUID = "virtunerd.scriptone";

        private object _engine;
        private bool _tickGesehen;
        private bool _eigenerTakt;

        /// <summary>Der 'debug'-Schalter, gemerkt fuer Stellen ausserhalb von <see cref="Starte"/>.</summary>
        private bool _debug;
        private Action _abmelden;

        private void Awake()
        {
            // ⚠ ERST DIE ASSEMBLY-BRUECKE, DANN ALLES ANDERE. Gemessen unter Mono: die
            //   eingebettete Fassung des Interpreters wird NICHT gefunden - die Laufzeit fragt
            //   den AssemblyResolve-Haken gar nicht erst. Der Installer legt MoonSharp deshalb
            //   als DATEI neben dieses Plugin; der Haken bleibt nur als zweite Sicherung.
            EmbeddedAssemblies.Install();

            var spiel = Path.GetDirectoryName(Paths.GameRootPath ?? Paths.PluginPath);
            if (string.IsNullOrEmpty(Paths.GameRootPath) == false) spiel = Paths.GameRootPath;

            var log = new BepLog(Logger);
            try
            {
                _engine = Starte(spiel, log);
            }
            catch (Exception ex)
            {
                // Die ganze Kette - eine TypeInitializationException nennt nur den Typ, nicht
                // den Grund. Ohne das las das Log frueher "List`1 ist kaputt".
                for (var e = ex; e != null; e = e.InnerException)
                    Logger.LogError("ScriptOne could not start: " + e.GetType().FullName + ": " + e.Message);
                Logger.LogError("The GAME is unaffected - BepInEx and your other plugins keep running.");
                return;
            }

            // ⚠ DIE BEREITSCHAFTSMELDUNG GEHOERT IN DEN BLOCK, NICHT DAVOR. Starte() gibt bei
            //   belegtem HostGuard null zurueck - das ist KEINE Ausnahme, der catch-Zweig greift
            //   also nicht, und der Ablauf fiel bis hierher durch und meldete "ScriptOne ready"
            //   unmittelbar nach "standing down. This one does nothing." Seit der Uebernahme ist
            //   genau das der NORMALFALL neben BepInEx: unser Standalone-Wirt haelt den Prozess,
            //   BepInEx laedt das Vorsorge-Plugin, und dieses legt sich schlafen. Bep6Plugin.cs
            //   macht es an derselben Stelle richtig und steigt nach der Warnung aus.
            //
            // ⚠ ERST JETZT, und nur wenn wirklich ein Wirt steht: eine Wache ueber einen Takt,
            //   den es mangels Engine gar nicht geben soll, meldet einen Fehler, der keiner ist.
            if (_engine != null)
            {
                Logger.LogInfo("ScriptOne ready (" + GameGate.BackendName() + ") under BepInEx "
                               + typeof(BaseUnityPlugin).Assembly.GetName().Version);

                TaktWache.Starte(log, TaktWache.HinweisPlugin("BepInEx"));
                EigenenTaktEinhaengen();
            }
        }

        /// <summary>
        /// Der Start in einer eigenen Methode - sie nennt die Wirtstypen, der Aufrufer nicht.
        /// </summary>
        /// <remarks>
        /// Dieselbe Regel wie ueberall im Projekt: der JIT loest die Typen einer Methode beim
        /// BETRETEN auf. Stuende das im Awake-Rumpf, wuerde ein fehlender Wirtstyp das Plugin
        /// werfen lassen, bevor das try ueberhaupt greift - und BepInEx meldet dann nur, dass
        /// das Plugin nicht geladen werden konnte.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private object Starte(string spiel, IScriptLog log)
        {
            var skripte  = Path.Combine(spiel, "LuaScripts");
            var wurzel   = Path.Combine(spiel, "ScriptOne");
            var zustand  = Path.Combine(wurzel, "state");
            var doku     = Path.Combine(wurzel, "documentation");
            var cfg      = Path.Combine(wurzel, "ScriptOne-Starter.cfg");

            Directory.CreateDirectory(skripte);
            Directory.CreateDirectory(zustand);

            string ersterWar;
            if (!HostGuard.Beanspruche("BepInEx plugin", out ersterWar))
            {
                Logger.LogWarning("another ScriptOne host is already running in this process ("
                                  + ersterWar + ") - standing down. This one does nothing.");
                // ⚠ Die Methode gibt die Engine zurueck - ein blankes 'return' ist hier CS0126.
                return null;
            }

            var schalter = HostSchalter.LiesUndLege(cfg, log);
            // Fuer EigenenTaktEinhaengen, das den Schalter sonst nicht sieht.
            _debug = schalter.Debug;
            var engine = new LuaEngine(log, zustand, doku, schalter);
            if (schalter.Debug) VerwaisterWirt.Melde(zustand, log);
            engine.LoadFolder(skripte);
            log.Info(EmbeddedAssemblies.Herkunft());
            return engine;
        }

        /// <summary>
        /// Haengt einen EIGENEN Taktgeber ein, statt sich auf das Traegerobjekt des Laders zu
        /// verlassen.
        /// </summary>
        /// <remarks>
        /// ⚠ WARUM UEBERHAUPT: das Objekt des Laders ist nicht garantiert. Gemessen 2026-08-20
        /// in Landlord Simulator - BepInEx erzeugt seinen BepInEx_Manager dort, BEVOR die erste
        /// Szene existiert (sein Chainloader haengt im statischen Konstruktor von
        /// UnityEngine.Application, und wann der feuert, entscheidet das SPIEL). Das Objekt
        /// ueberlebt den ersten Szenenladevorgang nicht, und mit ihm stirbt der Takt JEDES
        /// Plugins dieses Laders. Awake lief, Update kein einziges Mal - ueber 388 Bilder.
        ///
        /// ⚠ UND DESHALB ERST BEI sceneLoaded, nicht sofort. Ein 'new GameObject(...)' zu frueh
        /// ist unter Mono nicht bloss wirkungslos, sondern zerstoerend: der Typinitialisierer
        /// von UnityEngine.Object scheitert an einem noch nicht angemeldeten icall, Mono merkt
        /// sich den Fehlschlag DAUERHAFT, und danach ist UnityEngine.Object fuer den ganzen
        /// Prozess tot - auch fuer das Spiel (schwarzer Bildschirm, gemessen am 2026-08-19).
        /// Der Standalone-Zweig wartet aus genau diesem Grund auf denselben Wegpunkt.
        /// </remarks>
        private void EigenenTaktEinhaengen()
        {
            // ⚠ KEIN UNITY-TYP IM QUELLTEXT. Diese eine Datei wird gegen DREI verschiedene
            //   Referenzsaetze uebersetzt (BepInEx 5 und 6, Mono und Il2Cpp). Unter BepInEx 6
            //   zieht das Paket zusaetzlich UnityEngine.CoreModule herein, und dann sind
            //   Scene, LoadSceneMode und MonoBehaviour in ZWEI Assemblies vorhanden - CS0433,
            //   dreimal. Wer das mit einem Alias in einer der csproj loest, hat die Loesung an
            //   der falschen Stelle: derselbe Quelltext muss unter allen dreien uebersetzen.
            //   Ueber Reflexion gibt es keinen mehrdeutigen Namen.
            try
            {
                var sm = FindeTyp("UnityEngine.SceneManagement.SceneManager");
                if (sm == null) { Logger.LogWarning("SceneManager not found - relying on the loader's own tick."); return; }
                var ev = sm.GetEvent("sceneLoaded", BindingFlags.Public | BindingFlags.Static);
                if (ev == null) { Logger.LogWarning("sceneLoaded not found - relying on the loader's own tick."); return; }
                var h = Delegate.CreateDelegate(ev.EventHandlerType, this,
                            GetType().GetMethod("BeiSzene", BindingFlags.NonPublic | BindingFlags.Instance));
                ev.GetAddMethod().Invoke(null, new object[] { h });
                _abmelden = () => { try { ev.GetRemoveMethod().Invoke(null, new object[] { h }); } catch { } };
            }
            catch (Exception ex)
            {
                Logger.LogWarning("could not subscribe to sceneLoaded (" + ex.GetType().Name
                                  + ") - relying on the loader's own tick.");
            }
        }

        /// <summary>Wird per Reflexion an sceneLoaded gehaengt - die Signatur muss passen.</summary>
        private void BeiSzene(object szene, object modus)
        {
            if (_eigenerTakt) return;
            try
            {
                if (_abmelden != null) _abmelden();

                var go = FindeTyp("UnityEngine.GameObject");
                var obj = FindeTyp("UnityEngine.Object");
                if (go == null || obj == null) return;

                var traeger = Activator.CreateInstance(go, new object[] { "ScriptOne" });
                var ddol = obj.GetMethod("DontDestroyOnLoad", BindingFlags.Public | BindingFlags.Static);
                if (ddol != null) ddol.Invoke(null, new[] { traeger });

                var add = go.GetMethod("AddComponent", BindingFlags.Public | BindingFlags.Instance,
                                       null, new[] { typeof(Type) }, null);
                if (add == null) return;
                var tick = add.Invoke(traeger, new object[] { typeof(BepTick) }) as BepTick;
                if (tick == null) return;
                tick.Wirt = this;
                _eigenerTakt = true;
                Logger.LogInfo("frame tick moved to our own object - it no longer depends on the loader's.");

                // ⚠ DIAGNOSE, weil "erzeugt" NICHT "wird getaktet" heisst - und genau dieser
                //   Unterschied ist hier gemessen aufgetreten: das Objekt entstand, Update lief
                //   nie. Was hier ausgegeben wird, entscheidet zwischen den moeglichen Ursachen:
                //   inaktives Objekt, abgeschaltete Komponente, Objekt in keiner Szene, oder ein
                //   Typ, den Unity gar nicht als Skript fuehrt.
                //
                // ⚠ HINTER 'debug', NICHT IM AUSLIEFERUNGSSTAND. Das ist eine Zustandsbeschreibung
                //   aus der Fehlersuche, und der Plugin-Weg ist seit der Uebernahme ohnehin nur
                //   noch Rueckfall und Vorsorge. Wer sie braucht, setzt debug = true - genau dafuer
                //   gibt es den Schalter.
                if (!_debug) return;
                try
                {
                    var goT = traeger.GetType();
                    var aktiv = goT.GetProperty("activeInHierarchy");
                    var szeneP = goT.GetProperty("scene");
                    var beT   = tick.GetType();
                    var an    = beT.GetProperty("isActiveAndEnabled");
                    var en    = beT.GetProperty("enabled");
                    Logger.LogInfo("  tick object: type=" + beT.FullName
                        + " activeInHierarchy=" + (aktiv == null ? "?" : aktiv.GetValue(traeger, null))
                        + " enabled=" + (en == null ? "?" : en.GetValue(tick, null))
                        + " isActiveAndEnabled=" + (an == null ? "?" : an.GetValue(tick, null))
                        + " scene=" + (szeneP == null ? "?" : szeneP.GetValue(traeger, null)));
                }
                catch (Exception exD) { Logger.LogInfo("  tick object: could not be described (" + exD.GetType().Name + ")"); }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("could not create our own tick object (" + ex.GetType().Name
                                  + ") - relying on the loader's own tick.");
            }
        }

        private static Type FindeTyp(string voll)
        {
            var t = Type.GetType(voll, false);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType(voll, false); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>Fuer das Taktobjekt: welche Unity-Nachricht es ueberhaupt erreicht.</summary>
        internal void MeldeNachricht(string was)
        {
            Logger.LogInfo("  tick object received: " + was);
        }

        /// <summary>Fuer das Taktobjekt: der Schluss-Meldeweg der Wache.</summary>
        /// <remarks>
        /// ⚠ EIGENER TRAEGER, also der ANDERE Hinweistext. Diese Methode wird ausschliesslich aus
        /// <c>BepTick.OnDestroy</c> gerufen und feuert damit nur, WENN wir uns ein eigenes
        /// Taktobjekt gebaut hatten. <c>HinweisPlugin</c> haette dem Nutzer das Traegerobjekt des
        /// LADERS als Ursache genannt - genau die, die in diesem Fall ausgeschlossen ist. Der
        /// zweite Aufrufer weiter unten ist dagegen richtig: er ist auf
        /// <c>_abmelden == null &amp;&amp; !_eigenerTakt</c> begrenzt.
        /// </remarks>
        internal void MeldeSchluss()
        {
            TaktWache.Schluss(new BepLog(Logger), TaktWache.HinweisEigenerTraeger("BepInEx"));
        }

        internal void Bild()
        {
            if (_engine == null) return;

            TaktWache.Bild();
            if (!_tickGesehen)
            {
                _tickGesehen = true;
                Logger.LogInfo("frame tick alive - timers and s1.every will run");
            }

            var e = _engine as LuaEngine;
            if (e == null) return;
            e.PollGameReady();
            e.Tick();
        }

        private void Update()
        {
            if (_eigenerTakt) return;   // unser eigenes Objekt taktet - siehe Bild()
            Bild();
        }


        private void OnDestroy()
        {
            var e = _engine as LuaEngine;
            if (e == null) return;

            // ⚠ HIER NICHT MEHR MELDEN, WENN EINE UEBERGABE LAEUFT. Dieses OnDestroy gehoert dem
            //   Traegerobjekt des LADERS - und genau dass es stirbt, ist ja der Anlass fuer
            //   unser eigenes Objekt. Gemessen 2026-08-20: die Warnung "NO FRAME TICK for the
            //   whole session" erschien, und DREI Zeilen spaeter meldete derselbe Lauf
            //   "frame tick moved to our own object". Beides stimmte fuer sich, zusammen war es
            //   ein Fehlalarm. Den Schluss meldet jetzt unser eigenes Objekt (BepTick.OnDestroy),
            //   und wo es gar nicht erst entsteht, bleibt der Zeitgeber der Wache.
            if (_abmelden == null && !_eigenerTakt)
                TaktWache.Schluss(new BepLog(Logger), TaktWache.HinweisPlugin("BepInEx"));

            try { e.SaveAll(); } catch (Exception ex) { Logger.LogWarning("save on exit failed: " + ex.Message); }
            GameGate.Abloesen();
        }

        /// <summary>Bindet IScriptLog an BepInEx' Protokoll. Die einzige Stelle, die beide kennt.</summary>
        private sealed class BepLog : IScriptLog
        {
            private readonly ManualLogSource _inner;
            internal BepLog(ManualLogSource inner) { _inner = inner; }
            public void Info(string message)  { _inner.LogInfo(message); }
            public void Warn(string message)  { _inner.LogWarning(message); }
            public void Error(string message) { _inner.LogError(message); }
        }
    }

    /// <summary>
    /// Unser eigenes Taktobjekt - unabhaengig vom Traegerobjekt des Laders.
    /// </summary>
    /// <remarks>
    /// ⚠ AUF OBERSTER EBENE, NICHT VERSCHACHTELT. Als Klasse INNERHALB von Bep5Plugin entstand
    /// die Komponente zwar (AddComponent gab sie zurueck, und die Uebergabe wurde protokolliert),
    /// aber Unity rief ihr Update NIE - der Waechter meldete nach 15 s weiterhin null Bilder.
    /// Der Gegenbeleg stand im selben Spiel: der Standalone-Zweig baut sein Taktobjekt genauso,
    /// nur ist seine Komponente eine Klasse auf oberster Ebene, und dort feuert Update ab dem
    /// ersten Bild. Gemessen 2026-08-20, Landlord Simulator.
    ///
    /// ⚠ Der Basistyp MonoBehaviour ist der EINZIGE Unity-Name in dieser Datei; alles andere
    /// wird reflexiv angefasst, weil sie gegen drei verschiedene Referenzsaetze uebersetzt wird.
    /// Eine Basisklasse laesst sich nicht per Reflexion angeben.
    /// </remarks>
    internal sealed class BepTick : MonoBehaviour
    {
        internal Bep5Plugin Wirt;

        // ⚠ DIAGNOSE: unterscheidet "bekommt GAR KEINE Unity-Nachrichten" von "bekommt sie,
        //   nur Update nicht". Ohne diese Trennung raet man zwischen zwei ganz verschiedenen
        //   Ursachen - Typbindung gegen Update-Verteilung.
        private void Awake() { Melde("Awake"); }
        private void Start() { Melde("Start"); }
        private void OnEnable() { Melde("OnEnable"); }

        private void Melde(string was)
        {
            try { if (Wirt != null) Wirt.MeldeNachricht(was); } catch { }
        }

        // Unity findet die Methode per NAMEN - deshalb exakt so geschrieben.
        private void Update() { if (Wirt != null) Wirt.Bild(); }

        private void OnDestroy()
        {
            if (Wirt != null) Wirt.MeldeSchluss();
        }
    }

}
