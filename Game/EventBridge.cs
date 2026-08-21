using System;

#if IL2CPP
using Il2CppInterop.Runtime;
using GamePlayer = Il2CppScheduleOne.PlayerScripts.Player;
using GameAction = Il2CppSystem.Action;
#endif
#if MONO
using GamePlayer = ScheduleOne.PlayerScripts.Player;
using GameAction = System.Action;
#endif

namespace ScriptOne.Game
{
    /// <summary>
    /// Die Gegenrichtung: Spielereignisse -> Lua. Zusammen mit GameBridge (Lua -> Spiel)
    /// ist das die vollstaendige Bruecke.
    /// </summary>
    /// <remarks>
    /// KEIN HARMONY NOETIG. Die urspruengliche Annahme war, das Spiel habe fast keine Haken
    /// und der Wirt muesse sie sich per Patch selbst setzen. Nachgemessen am Mono-Dekompilat
    /// stimmt das nicht: 74 oeffentliche Events und 11 oeffentliche statische Action-Felder in
    /// ScheduleOne.*. Abonnieren ist billiger, robuster und ueberlebt Spiel-Updates besser als
    /// ein Patch in einen Methodenrumpf.
    ///
    /// DREI REGELN, alle teuer gelernt:
    ///   1. Unter Il2Cpp ist ein 'Action'-Feld ein Il2CppSystem.Action. Eine Methodengruppe
    ///      passt dort NICHT (CS0019) - es braucht DelegateSupport.ConvertDelegate.
    ///   2. Das konvertierte Delegat muss ZWISCHENGESPEICHERT werden. Jede Konvertierung
    ///      erzeugt eine neue Instanz, und '-=' findet dann nichts zum Abmelden - der Handler
    ///      bliebe fuer immer haengen und feuerte nach jedem Szenenwechsel oefter.
    ///   3. Ein statisches Spiel-Event GEHOERT ALLEN. Wer das Feld auf null setzt, loescht die
    ///      Abos aller anderen Mods mit. Deshalb nur '-=' und '+=', nie eine Zuweisung.
    ///
    /// Die Nutzlast bleibt FLACH: Ereignisname, eine Zeichenkette, eine Zahl. Es gibt keinen
    /// Weg, ueber den ein Spielobjekt nach Lua gelangt.
    /// </remarks>
    internal static class EventBridge
    {
        /// <summary>Wird bei jedem Spielereignis gerufen. Nutzlast ausschliesslich flach.</summary>
        internal static Action<string, string, double> Raised;

        /// <summary>
        /// Ausgabe fuer den Anschlussbericht. PFLICHT, nicht Kuer: die erste Fassung dieser
        /// Klasse schluckte jede Ausnahme aus Attach() ohne Logzeile - ein fehlgeschlagenes
        /// Il2Cpp-Abo haette dann exakt so ausgesehen wie ein gelungenes. Genau die Sorte
        /// stilles Gruen, gegen die die ganze Werkstatt sonst Positivkontrollen baut.
        /// </summary>
        internal static Action<string> Report;

        private static void Say(string m) { var r = Report; if (r != null) r(m); }

        /// <summary>Alle bekannten Ereignisnamen - fuer die Pruefung in s1.on().</summary>
        internal static readonly string[] Known =
        {
            "game_ready",
            "player_spawned", "player_arrested", "player_freed",
            "player_tased", "player_tased_end", "player_struck_by_lightning",
        };

        private static int _fehlschlaege;
        private static bool _staticAttached;
        private static object _attachedTo;

        private static void Fire(string name)
        {
            var h = Raised;
            if (h != null) h(name, string.Empty, 0);
        }

        /// <summary>Managed Delegat -> Spiel-Delegat. Unter Mono ist das die Identitaet.</summary>
        private static GameAction Conv(Action m)
        {
#if IL2CPP
            return DelegateSupport.ConvertDelegate<GameAction>(m);
#else
            return m;
#endif
        }

        // Einmal konvertiert, fuer immer dieselbe Instanz - sonst schlaegt '-=' fehl (Regel 2).
        //
        // ⚠ NICHT als Feldinitialisierer. Das war der Entwurfsfehler:
        // Feldinitialisierer laufen im STATISCHEN KONSTRUKTOR, und der laeuft, sobald irgendwer
        // die Klasse zum ersten Mal beruehrt - hier schon beim Anlegen der LuaEngine. Wirft
        // dort etwas, gibt es keine TypeInitializationException zum Abfangen, sondern die
        // KLASSE ist fuer den Rest des Prozesses tot. Gemessen im ersten Standalone-Lauf:
        // DelegateSupport.ConvertDelegate braucht Klasseninjektion und damit einen
        // IDetourProvider; ohne den warf der statische Konstruktor - und riss den GANZEN
        // Lua-Wirt mit, obwohl Ereignisse nur EIN Teilsystem von sechs sind.
        //
        // Jetzt traege und gekapselt: die Umwandlung passiert beim ersten Attach(), Fehlschlag
        // schaltet nur die Ereignisse ab und wird GEMELDET.
        private static GameAction A_Spawned, A_Arrested, A_Freed, A_Tased, A_TasedEnd, A_Lightning;
        private static bool _delegateBereit;
        private static bool _delegateAufgegeben;

        private static bool DelegateVorbereiten()
        {
            if (_delegateBereit) return true;
            if (_delegateAufgegeben) return false;
            try
            {
                A_Spawned   = Conv(() => Fire("player_spawned"));
                A_Arrested  = Conv(() => Fire("player_arrested"));
                A_Freed     = Conv(() => Fire("player_freed"));
                A_Tased     = Conv(() => Fire("player_tased"));
                A_TasedEnd  = Conv(() => Fire("player_tased_end"));
                A_Lightning = Conv(() => Fire("player_struck_by_lightning"));
                _delegateBereit = true;
                return true;
            }
            catch (Exception ex)
            {
                _delegateAufgegeben = true;
                Say("event bridge DISABLED - could not build game delegates: "
                    + ex.GetType().Name + ": " + ex.Message);
                Say("  everything else keeps working: s1.console, the generated surface, "
                    + "timers and state. Only s1.on(player_*) will never fire.");
                return false;
            }
        }

        /// <summary>
        /// Haengt die Abos ein. Darf beliebig oft gerufen werden - jeder Szenenwechsel tauscht
        /// die Player-Instanz aus, und dann muessen die Instanz-Events neu haengen.
        /// </summary>
        internal static void Attach()
        {
            if (!DelegateVorbereiten()) return;
            try
            {
                if (!_staticAttached)
                {
                    GamePlayer.onLocalPlayerSpawned -= A_Spawned;
                    GamePlayer.onLocalPlayerSpawned += A_Spawned;
                    _staticAttached = true;
                    Say("event bridge: static hook attached (Player.onLocalPlayerSpawned)");
                }

                var p = GamePlayer.Local;
                if (p == null || ReferenceEquals(p, _attachedTo)) return;

                p.onArrested          -= A_Arrested;  p.onArrested          += A_Arrested;
                p.onFreed             -= A_Freed;     p.onFreed             += A_Freed;
                p.onTased             -= A_Tased;     p.onTased             += A_Tased;
                p.onTasedEnd          -= A_TasedEnd;  p.onTasedEnd          += A_TasedEnd;
                p.onStruckByLightning -= A_Lightning; p.onStruckByLightning += A_Lightning;

                _attachedTo = p;
                Say("event bridge: 5 player hooks attached (arrested, freed, tased, tased_end, lightning)");
            }
            catch (Exception ex)
            {
                // Waehrend eines Szenenwechsels koennen die Proxies kurz ins Leere zeigen, und
                // der naechste Poll versucht es erneut - deshalb kein Abbruch. Aber GEMELDET
                // wird es: ein dauerhaft fehlschlagendes Abo waere sonst nicht von einem
                // funktionierenden zu unterscheiden.
                _fehlschlaege++;
                if (_fehlschlaege == 1 || _fehlschlaege == 10)
                    Say("event bridge: attach failed (" + _fehlschlaege + "x) - "
                        + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Beim Beenden abmelden - NIE das Feld auf null setzen (Regel 3).</summary>
        internal static void Detach()
        {
            if (!_delegateBereit) return;
            try
            {
                GamePlayer.onLocalPlayerSpawned -= A_Spawned;
                var p = GamePlayer.Local;
                if (p != null)
                {
                    p.onArrested          -= A_Arrested;
                    p.onFreed             -= A_Freed;
                    p.onTased             -= A_Tased;
                    p.onTasedEnd          -= A_TasedEnd;
                    p.onStruckByLightning -= A_Lightning;
                }
            }
            catch { }
            _staticAttached = false;
            _attachedTo = null;
        }

        /// <summary>Nach einem Szenenwechsel muss die Instanzbindung neu gesucht werden.</summary>
        internal static void ResetInstanceBinding()
        {
            _attachedTo = null;
        }
    }
}
