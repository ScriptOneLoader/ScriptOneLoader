using System;
using System.IO;
using System.Runtime.CompilerServices;

#if IL2CPP
using GamePlayer = Il2CppScheduleOne.PlayerScripts.Player;
#else
using GamePlayer = ScheduleOne.PlayerScripts.Player;
#endif

namespace ScriptOne.Host
{
    /// <summary>
    /// Beantwortet EINMAL beim Start die Frage: liegt hinter uns ueberhaupt die Spielschicht,
    /// oder laufen wir in einem Spiel, das Assembly-CSharp von Schedule I gar nicht hat?
    /// </summary>
    /// <remarks>
    /// WARUM ES DAS GIBT
    /// Der Mono-Zweig lief am 2026-08-19 erstmals in einem FREMDEN Spiel (No Knock) und starb an
    /// <c>ScheduleOne.PlayerScripts.Player</c>. Ein Typ, den es dort nicht gibt, ist kein Fehler,
    /// den man abfangen kann, wo er auftritt - er ist die Frage, ob dieser ganze Zweig ueberhaupt
    /// gemeint ist.
    ///
    /// ⚠ WARUM DIE SONDE EINE EIGENE METHODE MIT NoInlining IST - das ist der ganze Trick:
    /// Der JIT loest die Typen einer Methode auf, BEVOR er sie ausfuehrt. Stuende
    /// <c>typeof(GamePlayer)</c> direkt im try-Block von <see cref="Pruefe"/>, muesste die
    /// Laufzeit den Spieltyp schon beim BETRETEN von Pruefe finden - also bevor der try-Block
    /// gilt. Die TypeLoadException fiele dann NEBEN dem catch an und riss den Aufrufer mit.
    /// Deshalb liegt der Zugriff in <see cref="Sonde"/>, und das try steht um den AUFRUF.
    /// Ohne <c>NoInlining</c> darf der JIT Sonde nach Pruefe hineinziehen und stellt damit
    /// genau den Zustand wieder her, den diese Datei verhindern soll - das Attribut ist keine
    /// Optimierungsbremse, es ist die Zusicherung. Gleiche Bauart wie <see cref="LuaBoot"/>.
    ///
    /// WAS ER NICHT PRUEFT
    /// Ob das SPIEL bereit ist - das macht <c>GameBridge.IsGameReady()</c>. Hier geht es nur um
    /// die Frage, ob die Typen ueberhaupt geladen werden koennen. Deshalb <c>typeof</c> und kein
    /// Zugriff auf eine Instanz oder einen statischen Zustand: eine Sonde, die eine noch nicht
    /// initialisierte Spielklasse anfasst, wuerde im RICHTIGEN Spiel einen Fehlalarm ausloesen
    /// und die Spielschicht faelschlich abschalten.
    /// </remarks>
    internal static class GameLayer
    {
        private static bool _gefragt;
        private static bool _vorhanden;
        private static string _grund = "not probed yet";

        /// <summary>
        /// Liegt die Spielschicht vor? Gueltig erst nach <see cref="Pruefe"/>; vorher <c>false</c>.
        /// </summary>
        internal static bool Vorhanden { get { return _vorhanden; } }

        /// <summary>Warum das Ergebnis so ausfiel - fertig formuliert fuer das Protokoll.</summary>
        internal static string Grund { get { return _grund; } }

        /// <summary>Wurde die Frage schon gestellt? Der Aufrufer soll sie nicht zweimal stellen.</summary>
        internal static bool Gefragt { get { return _gefragt; } }

        /// <summary>
        /// Fragt einmal und merkt sich das Ergebnis. Weitere Aufrufe kosten nichts und melden
        /// nichts erneut. Gibt zurueck, ob die Spielschicht vorliegt.
        /// </summary>
        internal static bool Pruefe(IScriptLog log)
        {
            if (_gefragt) return _vorhanden;
            _gefragt = true;

            try
            {
                // ⚠ Das try steht um den AUFRUF, nicht im Rumpf der Sonde. Siehe Klassenkommentar.
                var name = Sonde();
                _vorhanden = !string.IsNullOrEmpty(name);
                _grund = _vorhanden
                    ? "game layer present (" + name + ")"
                    : "game type resolved but reported no name - treating the game layer as absent";
            }
            catch (TypeLoadException ex)      { _vorhanden = false; _grund = Kurz("game type not found", ex); }
            catch (FileNotFoundException ex)  { _vorhanden = false; _grund = Kurz("game assembly not found", ex); }
            catch (BadImageFormatException ex){ _vorhanden = false; _grund = Kurz("game assembly unreadable", ex); }
            catch (MissingMemberException ex) { _vorhanden = false; _grund = Kurz("game type changed", ex); }
            // Der breite Fang ist Absicht: was hier sonst noch fliegt, ist immer noch besser als
            // ein Wirt, der beim Start stirbt. Die Ausnahmeart steht im Grund, also geht nichts
            // verloren - im Gegensatz zu einem stillen catch.
            catch (Exception ex)              { _vorhanden = false; _grund = Kurz("unexpected", ex); }

            // Einmal melden, in JEDEM Fall. Ein Schalter, der still umlegt, ist ein Schalter, den
            // spaeter niemand in einem Nutzerprotokoll wiederfindet.
            if (log != null)
            {
                if (_vorhanden) log.Info("Game layer: available - " + _grund);
                else            log.Info("Built-in Schedule I bindings: not applicable here - " + _grund +
                                         ". This is not an error; the surface for THIS game is found at startup instead.");
            }
            return _vorhanden;
        }

        /// <summary>
        /// Der einzige Ort, der einen Spieltyp nennt. Muss eine eigene Methode bleiben, und
        /// <c>NoInlining</c> muss dranbleiben - beides begruendet im Klassenkommentar.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string Sonde()
        {
            return typeof(GamePlayer).FullName;
        }

        private static string Kurz(string was, Exception ex)
        {
            var m = ex == null ? string.Empty : ex.Message;
            if (m.Length > 160) m = m.Substring(0, 160) + "...";
            return was + " (" + (ex == null ? "?" : ex.GetType().Name) + ": " + m + ")";
        }

        /// <summary>
        /// NUR fuer den Pruefstand: setzt den gemerkten Zustand zurueck, damit ein Harness beide
        /// Faelle in EINEM Lauf messen kann. Im Spiel wird das nie gerufen.
        /// </summary>
        internal static void ZuruecksetzenFuerTest()
        {
            _gefragt = false;
            _vorhanden = false;
            _grund = "not probed yet";
        }
    }
}
