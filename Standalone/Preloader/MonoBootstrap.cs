#if MONO
using System;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;
using ScriptOne.Host;

namespace ScriptOne.Preloader
{
    /// <summary>
    /// Das Mono-Gegenstück zu <see cref="Il2CppBootstrap"/>: startet den Lua-Wirt auf einem
    /// Unity-Spiel, das mit dem MONO-Backend läuft.
    /// </summary>
    /// <remarks>
    /// WARUM DAS SO VIEL KÜRZER IST ALS DER IL2CPP-ZWEIG. Der ganze teure Unterbau dort -
    /// Proxy-Assemblies, Il2CppInterop, Detour-Provider, Klasseninjektion - existiert nur, um
    /// eine verwaltete Sicht auf eine NICHT verwaltete Laufzeit herzustellen. Auf Mono ist die
    /// Laufzeit bereits verwaltet: die Spiel-DLLs SIND die Referenz, ein MonoBehaviour ist ein
    /// MonoBehaviour, und ein Delegat braucht keine Umwandlung.
    ///
    /// Doorstop trägt beide Wege in DERSELBEN winhttp.dll (per Bytesuche belegt:
    /// mono_jit_init_version @0x4530 gegen coreclr_initialize @0x48B8) und ruft in beiden
    /// Fällen 'Doorstop.Entrypoint:Start'. Welcher Weg läuft, entscheidet das Spiel.
    ///
    /// ⚠ DER ZEITPUNKT IST DER GANZE PUNKT - und der Sofortversuch war toedlich.
    /// Gemessen 2026-08-19 (No Knock, Unity 6000.0.46f1): `new GameObject(...)` direkt aus
    /// dem Doorstop-Einstieg laeuft INNERHALB von mono_jit_init_version, also bevor Unitys
    /// native Seite ihre internen Aufrufe angemeldet hat. Der Typinitialisierer von
    /// UnityEngine.Object ruft dort GetOffsetOfInstanceIDInCPlusPlusObject - einen icall mit
    /// RVA 0 - und scheitert.
    ///
    /// ⚠ UND EIN GESCHEITERTER TYPINITIALISIERER IST ENDGUELTIG. Mono merkt sich den
    /// Fehlschlag; jeder spaetere Zugriff wirft dieselbe Ausnahme, auch lange nachdem der
    /// icall angemeldet ist. Damit war UnityEngine.Object fuer den GANZEN PROZESS tot - auch
    /// fuer das Spiel. Der Player-Log jenes Laufs: 810 982 Zeilen, 119 094 Vorkommen, und die
    /// Stapel gehoeren dem Spiel (Bloom..ctor, MotionBlur..ctor, GraphicRegistry,
    /// VolumeProfile.OnDisable). Das war der schwarze Bildschirm.
    ///
    /// ⚠ EIN try/catch HILFT DABEI NICHT. Der Schaden kommt von der werfenden ANWEISUNG, nicht
    /// von einer unbehandelten Ausnahme - "probieren und notfalls zurueckfallen" gibt es hier
    /// nicht. Deshalb wird jetzt GAR NICHT probiert, sondern gewartet.
    ///
    /// SO MACHEN ES DIE ETABLIERTEN LADER (beide Quelltexte gelesen): die frueheste Assembly
    /// referenziert UnityEngine gar nicht, und gestartet wird erst aus einem Ereignis, das der
    /// MOTOR treibt. MelonLoader haengt sich per Harmony vor
    /// SceneManager.Internal_ActiveSceneChanged, sobald UnityEngine.CoreModule geladen wird.
    /// Wir nehmen denselben Wegpunkt eine Stufe hoeher - SceneManager.sceneLoaded - und
    /// beruehren VORHER nichts, was von UnityEngine.Object abstammt.
    /// </remarks>
    internal static class MonoBootstrap
    {
        private static FileLog _log;
        private static bool _gestartet;

        /// <summary>Der EINE Versuch - als Feld, damit er sich selbst wieder abmelden kann.</summary>
        private static UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene,
                                                     UnityEngine.SceneManagement.LoadSceneMode> _versuch;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Start(string spielOrdner, FileLog log, HostConfig cfg)
        {
            _log = log;
            log.Info("backend: Mono - no interop layer needed, the game's own assemblies are the reference");

            // ⚠ KEIN SOFORTVERSUCH. Siehe Klassenkommentar: er kostete das Spiel, nicht nur uns.
            //   Erst wenn UnityEngine.CoreModule geladen IST, haengen wir uns an ein Ereignis,
            //   das der Motor treibt - und beruehren bis dahin keinen Unity-Typ.
            if (CoreModuleDa())
            {
                log.Info("UnityEngine.CoreModule is already loaded - waiting for the first scene");
                HaengeAnSzene(spielOrdner, cfg);
                return;
            }

            log.Info("waiting for UnityEngine.CoreModule to load...");
            AppDomain.CurrentDomain.AssemblyLoad += (s2, e2) =>
            {
                try
                {
                    if (_gehaengt) return;
                    var n = e2.LoadedAssembly.GetName().Name;
                    if (n != "UnityEngine.CoreModule" && n != "UnityEngine") return;
                    _log.Info("UnityEngine loaded (" + n + ") - waiting for the first scene");
                    HaengeAnSzene(spielOrdner, cfg);
                }
                catch (Exception ex) { _log.Exception("MonoBootstrap (AssemblyLoad)", ex); }
            };
        }

        private static bool _gehaengt;

        /// <summary>Ist UnityEngine schon in der Domaene? Ohne einen Unity-Typ zu nennen.</summary>
        /// <remarks>
        /// Reflexion ueber die geladenen Assemblies - dabei wird NICHTS initialisiert. Ein
        /// `typeof(...)` waere hier schon zu viel Naehe: es zieht zwar keinen cctor, aber es
        /// bindet die Methode an den Typ, und diese Datei soll frueh gar nichts von Unity wollen.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool CoreModuleDa()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = a.GetName().Name;
                if (n == "UnityEngine.CoreModule" || n == "UnityEngine") return true;
            }
            return false;
        }

        /// <summary>
        /// Haengt den EINEN Startversuch an das erste Szenenladen.
        /// </summary>
        /// <remarks>
        /// ⚠ Eigene Methode mit NoInlining, weil hier zum ersten Mal Unity-Typen im Rumpf
        /// stehen: der JIT loest sie beim Betreten auf, und das darf erst passieren, wenn
        /// CoreModule wirklich da ist. SceneManager, Scene und LoadSceneMode stammen NICHT von
        /// UnityEngine.Object ab - das Abonnieren allein zieht dessen cctor also nicht.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void HaengeAnSzene(string spielOrdner, HostConfig cfg)
        {
            if (_gehaengt) return;
            _gehaengt = true;
            try
            {
                _versuch = (szene, modus) =>
                {
                    UnityEngine.SceneManagement.SceneManager.sceneLoaded -= _versuch;
                    if (_gestartet) return;
                    try
                    {
                        StarteWirt(spielOrdner, cfg);
                    }
                    catch (Exception e2)
                    {
                        _log.Exception("MonoBootstrap (first scene)", e2);
                        _log.Error("ScriptOne gives up on this game - the host will not start.");
                        _log.Error("The GAME is unaffected: nothing of ours stays subscribed.");
                    }
                };
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += _versuch;
            }
            catch (Exception ex)
            {
                _log.Exception("MonoBootstrap: cannot reach the Unity main thread", ex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StarteWirt(string spielOrdner, HostConfig cfg)
        {
            if (_gestartet) return;

            // Ein Trägerobjekt, das den Szenenwechsel überlebt - wie im Il2Cpp-Zweig, nur ohne
            // Injektion: unter Mono ist MonoTick eine ganz gewöhnliche Komponente.
            // ⚠ DIE ERSTE BERUEHRUNG DER UNITY-WELT. Wenn hier eine TypeInitializationException
            //   auf UnityEngine.Object fliegt, ist die Frage NICHT "warum GameObject", sondern
            //   "welche UnityEngine-Assembly ist das eigentlich" - deshalb vorher festhalten,
            //   woher sie kommt. Ein zweites, aus dem Managed-Ordner nachgeladenes CoreModule
            //   haette keine native Bindung und genau dieses Symptom.
            try
            {
                var t = typeof(UnityEngine.Object).Assembly;
                _log.Info("UnityEngine.Object comes from: " + (string.IsNullOrEmpty(t.Location)
                          ? "(memory)" : t.Location));
                _log.Info("  identity: " + t.FullName);
            }
            catch (Exception exU) { _log.Exception("could not even name the Unity assembly", exU); }

            var traeger = new GameObject("ScriptOne");
            UnityEngine.Object.DontDestroyOnLoad(traeger);
            try { traeger.hideFlags = HideFlags.HideAndDontSave; } catch { }

            var skripte  = Path.Combine(spielOrdner, "LuaScripts");
            var zustand  = Path.Combine(spielOrdner, "ScriptOne", "state");

            var tick = traeger.AddComponent<MonoTick>();
            tick.Einrichten(_log, skripte, zustand);

            _gestartet = true;
            _log.Info("frame tick attached (MonoBehaviour on 'ScriptOne', no injection needed)");
        }
    }

    /// <summary>
    /// Frame-Takt und Wirt in einem - auf Mono braucht es dafür keine Klasseninjektion.
    /// </summary>
    internal sealed class MonoTick : MonoBehaviour
    {
        private LuaEngine _engine;
        private FileLog _log;
        private bool _gestoppt;
        private long _bilder;

        internal void Einrichten(FileLog log, string skriptOrdner, string zustandOrdner)
        {
            _log = log;
            string ersterWar;
            if (!ScriptOne.Host.HostGuard.Beanspruche("standalone host", out ersterWar))
            {
                log.Warn("another ScriptOne host is already running in this process ("
                         + ersterWar + ") - standing down. This one does nothing.");
                _gestoppt = true;
                return;
            }
            _engine = new LuaEngine(log, zustandOrdner,
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(zustandOrdner) ?? zustandOrdner, "documentation"),
                ScriptOne.Host.HostSchalter.LiesNebenZustand(zustandOrdner, log));
            _engine.LoadFolder(skriptOrdner);
            log.Info("scripts loaded from " + skriptOrdner + " (" + _engine.ScriptCount + ")");
            log.Info(EmbeddedAssemblies.Herkunft());
            ScriptOne.Host.TaktWache.Starte(log, "The tick hangs on an injected MonoBehaviour on a DontDestroyOnLoad object."
                + " If it never fires, that object was removed by a scene change or never"
                + " became part of the running scene.");
        }

        // Unity findet die Methode per Namen. Zweiter Meldeweg der Taktwache - siehe dort:
        // wird das Traegerobjekt weggeraeumt, laeuft genau jetzt sein OnDestroy.
        public void OnDestroy()
        {
            if (_log == null) return;
            ScriptOne.Host.TaktWache.Schluss(_log, "The tick hangs on an injected MonoBehaviour on a DontDestroyOnLoad object."
                + " If it never fires, that object was removed by a scene change or never"
                + " became part of the running scene.");
        }

        // Unity findet die Methode per Namen.
        public void Update()
        {
            if (_gestoppt || _engine == null) return;

            // "Eingehaengt" ist keine Aussage ueber "feuert" - deshalb das erste Bild belegen.
            ScriptOne.Host.TaktWache.Bild();
            if (++_bilder == 1) _log.Info("frame tick alive - first frame observed");

            try
            {
                _engine.PollGameReady();
                _engine.Tick();
            }
            catch (Exception ex)
            {
                // Ein Fehler je Bild wuerde das Protokoll fluten.
                _log.Exception("frame tick", ex);
                _gestoppt = true;
            }
        }
    }
}
#endif
