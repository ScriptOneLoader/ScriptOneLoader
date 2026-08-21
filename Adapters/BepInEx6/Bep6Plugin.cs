using System;
using System.IO;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using ScriptOne.Host;
using UnityEngine;

namespace ScriptOne.Adapters.BepInEx6
{
    /// <summary>
    /// ScriptOne unter BepInEx 6 auf einem Il2Cpp-Spiel.
    ///
    /// ⚠ WARUM EIN DRITTER BAU UND NICHT DER BepInEx-5-ADAPTER: dessen Basisklasse
    /// <c>BaseUnityPlugin</c> IST ein MonoBehaviour aus der monolithischen UnityEngine.dll -
    /// unter Il2Cpp gibt es die so nicht. BepInEx 6 traegt stattdessen
    /// <c>BepInEx.Unity.IL2CPP.BasePlugin</c>, das KEIN MonoBehaviour ist und
    /// <c>Load()</c> statt <c>Awake()</c> ruft. Und die beiden Chainloader filtern gegen
    /// verschiedene Assemblynamen, eine Datei kann also gar nicht beiden genuegen.
    /// </summary>
    [BepInPlugin("virtunerd.scriptone", "ScriptOne", BepVersion.Version)]
    public sealed class Bep6Plugin : BasePlugin
    {
        private static LuaEngine _engine;
        private static ManualLog _log;

        public override void Load()
        {
            _log = new ManualLog(Log);

            string ersterWar;
            if (!HostGuard.Beanspruche("BepInEx 6 plugin", out ersterWar))
            {
                Log.LogWarning("another ScriptOne host is already running in this process ("
                               + ersterWar + ") - standing down. This one does nothing.");
                return;
            }

            try { Starte(); }
            catch (Exception ex)
            {
                // Ein Wirt, der nicht starten kann, darf das SPIEL nicht mitreissen - dieselbe
                // Regel wie in allen anderen Einstiegen.
                Log.LogError("ScriptOne could not start: " + ex);
            }
        }

        /// <summary>
        /// ⚠ EIGENE METHODE MIT NoInlining. Der JIT loest die Typen einer Methode beim BETRETEN
        /// auf; stuende das im Load()-Rumpf, wuerde ein fehlender Wirtstyp das Plugin werfen
        /// lassen, bevor das try ueberhaupt greift - und BepInEx meldete dann nur, dass das
        /// Plugin nicht geladen werden konnte.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Starte()
        {
            var spiel   = Paths.GameRootPath;
            var wurzel  = Path.Combine(spiel, "ScriptOne");
            var skripte = Path.Combine(spiel, "LuaScripts");
            var cfg     = Path.Combine(wurzel, "ScriptOne-Starter.cfg");

            Directory.CreateDirectory(skripte);
            Directory.CreateDirectory(Path.Combine(wurzel, "state"));

            var schalter = HostSchalter.LiesUndLege(cfg, _log);
            _engine = new LuaEngine(_log,
                                    Path.Combine(wurzel, "state"),
                                    Path.Combine(wurzel, "documentation"),
                                    schalter);
            if (schalter.Debug) VerwaisterWirt.Melde(Path.Combine(wurzel, "state"), _log);
            _engine.LoadFolder(skripte);

            // ⚠ DEN FRAME-TAKT GIBT ES HIER NUR UEBER EINE INJIZIERTE KOMPONENTE. BasePlugin ist
            //   kein MonoBehaviour und hat kein OnUpdate. AddComponent<T>() erledigt die
            //   Il2Cpp-Typregistrierung selbst - genau die Stelle, an der der Standalone-Wirt
            //   sonst ClassInjector.RegisterTypeInIl2Cpp von Hand ruft und an der es dort am
            //   haeufigsten schiefgeht.
            AddComponent<Bep6Tick>();

            Log.LogInfo("ScriptOne ready (" + GameGate.BackendName() + ") under BepInEx 6");
            TaktWache.Starte(_log, TaktWache.HinweisPlugin("BepInEx 6"));
        }

        private static bool _taktGesehen;

        /// <summary>Zweiter Meldeweg der Taktwache - vom OnDestroy der Takt-Komponente.</summary>
        internal static void Ende()
        {
            if (_log == null) return;
            TaktWache.Schluss(_log, TaktWache.HinweisPlugin("BepInEx 6"));
            var e = _engine;
            if (e == null) return;
            try { e.SaveAll(); } catch { }
        }

        internal static void Frame()
        {
            if (_engine == null) return;

            // ⚠ "EINGEHAENGT" IST KEINE AUSSAGE UEBER "FEUERT". Eine Zeile beim ERSTEN Bild,
            //   sonst meldet ein toter Takt einen fehlerfreien Start - und genau das war hier
            //   der Fall: der erste Lauf in einem echten Il2Cpp-Spiel band 46 Tabellen, aber
            //   'hello.lua' feuerte nie, weil PollGameReady fehlte und niemand es bemerkte.
            TaktWache.Bild();
            if (!_taktGesehen)
            {
                _taktGesehen = true;
                _log.Info("frame tick alive - timers and s1.every will run");
            }

            try
            {
                // ⚠ POLLGAMEREADY GEHOERT DAZU. Ohne den Aufruf laedt der Wirt die Skripte und
                //   bindet die Flaeche - aber kein Skript bekommt jemals 'game_ready', also
                //   laeuft nichts. Der Start sieht dabei vollstaendig fehlerfrei aus.
                _engine.PollGameReady();
                _engine.Tick();
            }
            catch (Exception ex) { _log.Error("tick: " + ex.Message); }
        }

        /// <summary>Bindeglied zwischen BepInEx' Protokoll und dem Wirt.</summary>
        private sealed class ManualLog : IScriptLog
        {
            private readonly BepInEx.Logging.ManualLogSource _q;
            internal ManualLog(BepInEx.Logging.ManualLogSource q) { _q = q; }
            public void Info(string m)  { _q.LogInfo(m); }
            public void Warn(string m)  { _q.LogWarning(m); }
            public void Error(string m) { _q.LogError(m); }
        }
    }

    /// <summary>
    /// Der Frame-Takt. Unity ruft <c>Update</c> je Bild auf dem Hauptfaden; die beiden
    /// Konstruktoren sind fuer einen injizierten Typ Pflicht - ein blankes <c>: base()</c>
    /// genuegt nicht.
    /// </summary>
    public sealed class Bep6Tick : MonoBehaviour
    {
        public Bep6Tick(IntPtr zeiger) : base(zeiger) { }

        public Bep6Tick() : base(ClassInjector.DerivedConstructorPointer<Bep6Tick>())
        {
            ClassInjector.DerivedConstructorBody(this);
        }

        // Wird von Unity per NAMEN gefunden - deshalb public und exakt so geschrieben.
        public void Update() => Bep6Plugin.Frame();

        // Wird von Unity per NAMEN gefunden. Zweiter Meldeweg der Taktwache - siehe dort.
        public void OnDestroy() => Bep6Plugin.Ende();
    }
}
