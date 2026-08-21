using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using ScriptOne.Game;
using ScriptOne.Host;

namespace ScriptOne.Preloader
{
    /// <summary>
    /// Das Gegenstueck zu ScriptOnePlugin, nur ohne MelonLoader: startet den Lua-Wirt
    /// und besorgt ihm einen Frame-Takt.
    /// </summary>
    /// <remarks>
    /// OHNE LOADER GIBT ES KEIN OnUpdate. Der Takt kommt deshalb aus einem eigenen,
    /// injizierten MonoBehaviour (TickBehaviour) auf einem Traegerobjekt, das den
    /// Szenenwechsel ueberlebt. Der bequemere Weg ueber Application.onBeforeRender ist
    /// in diesem Build NICHT gangbar - er ist weggestrippt, Messung siehe TickBehaviour.
    ///
    /// Schlaegt das Einhaengen fehl, laeuft der Wirt trotzdem: 'game_ready' feuert,
    /// nur Zeitgeber und Ereignisabos bleiben dann tot. Das wird ausdruecklich
    /// GEMELDET statt still hingenommen - ein Wirt ohne Takt sieht sonst genauso aus
    /// wie einer mit.
    ///
    /// EINGEHAENGT IST NICHT GEFEUERT. 'add_onBeforeRender hat nicht geworfen' sagt
    /// nur, dass die Registrierung durchging - ob Unity den Rueckruf je aufruft, ist
    /// eine andere Frage, und ihre Antwort waere ohne Zutun STILL. Darum zwei
    /// getrennte Meldungen: 'attached' beim Einhaengen, 'alive' beim ERSTEN
    /// tatsaechlichen Bild. Bleibt das zweite aus, sagt es der Waechter nach ein paar
    /// Sekunden ausdruecklich - sonst laese sich ein toter Takt nicht von einem
    /// leeren Skriptordner unterscheiden.
    /// </remarks>
    internal static class StandaloneHost
    {
        private static LuaEngine _engine;
        private static FileLog _log;
        private static GameObject _traeger;   // haelt die Takt-Komponente, ueberlebt Szenenwechsel
        private static bool _taktLaeuft;
        private static long _bilder;          // Beweis, dass der Takt WIRKLICH laeuft
        private static bool _taktGestoppt;    // nach einem Fehler: nicht je Bild weiterwerfen

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Start(FileLog log, string skriptOrdner, string zustandOrdner)
        {
            string ersterWar;
            if (!ScriptOne.Host.HostGuard.Beanspruche("standalone host", out ersterWar))
            {
                log.Warn("another ScriptOne host is already running in this process ("
                         + ersterWar + ") - standing down. This one does nothing.");
                return;
            }
            _log = log;
            try
            {
                _engine = new LuaEngine(log, zustandOrdner,
                    System.IO.Path.Combine(System.IO.Path.GetDirectoryName(zustandOrdner) ?? zustandOrdner, "documentation"),
                    ScriptOne.Host.HostSchalter.LiesNebenZustand(zustandOrdner, log));
                _engine.LoadFolder(skriptOrdner);
                log.Info("scripts loaded from " + skriptOrdner + " (" + _engine.ScriptCount + ")");
                // Welcher Interpreter WIRKLICH laeuft - siehe EmbeddedAssemblies.Herkunft.
                log.Info(ScriptOne.Host.EmbeddedAssemblies.Herkunft());

                HaengeFrameTaktEin();

                // Erster Durchlauf sofort: die Szene ist gerade aktiv geworden.
                _engine.PollGameReady();
                if (_taktLaeuft) StarteTaktWaechter();
                else
                {
                    // Kein Takt heisst: die halbe Flaeche ist tot. Das gehoert nicht in eine
                    // Zeile, die man ueberliest.
                    log.Error("NO FRAME TICK - s1.after, s1.every and all game events are DEAD.");
                    log.Error("  Scripts still load and s1.log/s1.console still work.");
                    log.Error("  Cause is almost always ClassInjector.RegisterTypeInIl2Cpp failing;");
                    log.Error("  the exception above says why. Most likely the proxy assemblies do");
                    log.Error("  not match this game build - run: tools/Update-Interop.ps1 -Force");
                }
            }
            catch (Exception ex)
            {
                log.Exception("StandaloneHost.Start", ex);
            }
        }

        /// <summary>
        /// Besorgt den Frame-Takt. Primaerweg ist ein injizierter MonoBehaviour; die
        /// beiden bequemeren BCL-Ereignisse sind in diesem Build weggestrippt
        /// (Begruendung und Messung: siehe TickBehaviour).
        /// </summary>
        /// <remarks>
        /// ⚠ WARUM ES HIER KEINE RUECKFALLEBENE GIBT - gemessen am 2026-08-19, nicht vermutet.
        ///
        /// Der naheliegende zweite Weg waere PlayerLoop: <c>GetCurrentPlayerLoop()</c> holen,
        /// ein <c>PlayerLoopSystem</c> mit eigenem <c>updateDelegate</c> einhaengen,
        /// <c>SetPlayerLoop()</c>. Beide Methoden tragen im eigenen Proxy-Satz einen echten
        /// Rumpf, sind also nicht gestrippt - der Weg SIEHT gangbar aus.
        ///
        /// Er ist es nicht, und zwar aus einem Grund, den man nur durch Nachsehen findet:
        /// ein <c>updateDelegate</c> muss per <c>DelegateSupport.ConvertDelegate</c> in die
        /// Il2Cpp-Welt gebracht werden, und dessen IL ruft
        /// <c>ClassInjector.RegisterTypeInIl2Cpp&lt;Il2CppToMonoDelegateReference&gt;()</c> -
        /// also GENAU den Aufruf, an dem der Primaerweg scheitert. Die "Rueckfallebene" teilt
        /// den Fehlermodus mit dem Weg, den sie absichern soll.
        ///
        /// Eine Attrappe, die im Ernstfall mitfaellt, ist schlimmer als keine: sie kostet Code,
        /// und wer sie im Quelltext sieht, haelt den Fall fuer abgesichert. Deshalb stattdessen
        /// eine LAUTE, handlungsfaehige Fehlermeldung (siehe Start()).
        /// </remarks>
        private static void HaengeFrameTaktEin()
        {
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<TickBehaviour>();

                // Eigener Traeger, der Szenenwechsel ueberlebt. HideAndDontSave haelt ihn
                // aus Szenenlisten heraus - ein Mod-Objekt gehoert dem Spiel nicht.
                _traeger = new GameObject("ScriptOne");
                UnityEngine.Object.DontDestroyOnLoad(_traeger);
                try { _traeger.hideFlags = HideFlags.HideAndDontSave; } catch { }
                _traeger.AddComponent(Il2CppType.Of<TickBehaviour>());

                _taktLaeuft = true;
                _log.Info("frame tick attached (injected MonoBehaviour on 'ScriptOne')");
            }
            catch (Exception ex)
            {
                _log.Exception("frame tick", ex);
            }
        }

        /// <summary>
        /// Meldet nach ein paar Sekunden, wenn KEIN einziges Bild angekommen ist.
        /// Ohne ihn waere ein eingehaengter, aber nie gerufener Takt vollkommen
        /// unauffaellig - und Zeitgeber wie Ereignisse blieben ohne jede Spur tot.
        /// </summary>
        private static void StarteTaktWaechter()
        {
            var t = new Thread(() =>
            {
                try
                {
                    Thread.Sleep(8000);
                    if (Interlocked.Read(ref _bilder) == 0)
                    {
                        _log.Warn("frame tick attached but NEVER fired in 8s - " +
                                  "s1.after/s1.every and game events will NOT work");
                        // ⚠ Diese Zeile nannte bis zum 2026-08-19 'Application.onBeforeRender'.
                        // Das ist NICHT der Mechanismus: der Takt haengt seit dem Umbau an einem
                        // injizierten MonoBehaviour. Eine Diagnose, die auf den falschen
                        // Mechanismus zeigt, ist schlimmer als keine - sie schickt den Leser
                        // stundenlang in die falsche Richtung.
                        _log.Warn("the injected MonoBehaviour on GameObject 'ScriptOne' exists " +
                                  "but its Update is not being driven - check that the object " +
                                  "still lives (DontDestroyOnLoad) and was not destroyed by a scene change");
                    }
                }
                catch { }
            });
            t.IsBackground = true;
            t.Name = "ScriptOne tick watchdog";
            t.Start();
        }

        /// <summary>Ruft der injizierte MonoBehaviour je Bild.</summary>
        internal static void OnFrame()
        {
            if (_taktGestoppt) return;

            // Erstes Bild ausdruecklich belegen - s. Anmerkung oben.
            ScriptOne.Host.TaktWache.Bild();
            if (Interlocked.Increment(ref _bilder) == 1)
                _log.Info("frame tick alive - first frame observed");

            try
            {
                _engine.PollGameReady();
                _engine.Tick();
            }
            catch (Exception ex)
            {
                // Ein Fehler je Bild wuerde das Log fluten - einmal melden, dann Takt anhalten.
                _log.Exception("frame tick", ex);
                _taktGestoppt = true;
            }
        }
    }
}
