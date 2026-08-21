using System;
using System.Runtime.CompilerServices;
using MoonSharp.Interpreter;
using ScriptOne.Game;

namespace ScriptOne.Host
{
    /// <summary>
    /// Die EINZIGE Naht zwischen dem Wirt und der Schicht, die das Spiel kennt.
    /// </summary>
    /// <remarks>
    /// WARUM ES DIESE DATEI GIBT
    /// Der Wirt soll in JEDEM Unity-Spiel hochkommen. Die Bindungen sind aber fuer genau ein
    /// Spiel erzeugt: gemessen 2026-08-19 nennen 55 von 55 Tabellen der erzeugten Flaeche
    /// (460 Funktionen) Typen aus dessen Assembly-CSharp - keine einzige kommt ohne aus
    /// (docs\CORE-VS-GAME.md). In einem fremden Spiel gibt es diese Typen nicht.
    ///
    /// ⚠ DER JIT LOEST TYPEN BEIM BETRETEN EINER METHODE AUF, nicht beim Erreichen der Zeile.
    /// Eine Methode, die irgendwo in ihrem Rumpf einen Spieltyp nennt, wirft also schon vor
    /// ihrer ersten Anweisung - ein try INNERHALB dieser Methode faengt nichts, und eine
    /// Abfrage wie 'if (spielVorhanden)' davor rettet sie auch nicht. Deshalb steht JEDER
    /// Zugriff hier in einer EIGENEN, kleinen Methode mit <c>NoInlining</c>: der Aufrufer
    /// nennt nur diese Klasse, geladen wird der Spieltyp erst beim tatsaechlichen Aufruf.
    ///
    /// ⚠ <c>NoInlining</c> ist hier KEINE Optimierungsbremse, sondern die Zusicherung selbst.
    /// Ohne das Attribut darf der JIT den Rumpf in den Aufrufer ziehen und stellt genau den
    /// Zustand wieder her, den diese Datei verhindert.
    ///
    /// Gemessen hat das seinen Ursprung in No Knock (Unity 6, Mono, MelonLoader 0.7.3): der
    /// Wirt starb dort im LuaEngine-Konstruktor, weil dessen Rumpf EventBridge nennt.
    ///
    /// Die Sonde selbst steht in <see cref="GameLayer"/> (eine Frage, einmal, gemerkt).
    /// </remarks>
    internal static class GameGate
    {
        private static bool _bereit;
        private static bool _gefragt;

        /// <summary>Traegt dieses Spiel die Bindungen? Einmal gefragt, danach gemerkt.</summary>
        internal static bool Bereit { get { return _bereit; } }

        /// <summary>Warum nicht - fuer die eine Logzeile, die der Nutzer sehen soll.</summary>
        internal static string Grund { get { return GameLayer.Grund; } }

        internal static void Pruefe(IScriptLog log)
        {
            if (_gefragt) return;
            _gefragt = true;
            _bereit = GameLayer.Pruefe(log);
        }

        // ------------------------------------------------------------------ Zugriffe
        // Jeder Zugriff: eigene Methode, NoInlining, und ein Rueckfall, der ohne Spiel gilt.

        internal static string BackendName()
        {
            // WARNUNG: Hier stand "core-only (no game bindings)", und das war ab dem Moment
            //   falsch, in dem die Flaeche aus der Datei/dem Scan kam: der Wirt meldete
            //   "keine Spielbindungen", waehrend zwei Zeilen darueber 2459 entstanden waren
            //   (gemessen 2026-08-20, No Knock). Der Zustand heisst jetzt, was er ist - die
            //   EINKOMPILIERTEN Bindungen fehlen, die erzeugten nicht.
            if (!_bereit) return "generic (surface found at startup)";
            return BackendNameIntern();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string BackendNameIntern() { return GameBridge.BackendName; }

        internal static void Anschliessen(Action<string, string, double> dispatch, Action<string> report)
        {
            if (!_bereit) return;
            AnschliessenIntern(dispatch, report);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AnschliessenIntern(Action<string, string, double> dispatch, Action<string> report)
        {
            EventBridge.Raised = dispatch;
            EventBridge.Report = report;
        }

        /// <summary>Gibt <c>false</c> zurueck, wenn es in diesem Spiel keine Konsole gibt.</summary>
        internal static bool SubmitConsole(string line)
        {
            if (!_bereit) return false;
            SubmitConsoleIntern(line);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SubmitConsoleIntern(string line) { GameBridge.SubmitConsoleCommand(line); }

        internal static double MoveSpeed()
        {
            if (!_bereit) return 0d;
            return MoveSpeedIntern();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double MoveSpeedIntern() { return GameBridge.GetMoveSpeedMultiplier(); }

        /// <summary>
        /// Ohne Spielschicht bleibt genau EIN Ereignis uebrig - und das gehoert dem Wirt.
        /// </summary>
        /// <remarks>
        /// ⚠ Gemessen 2026-08-19 im fremden Spiel: 's1.on("game_ready", ...)' wurde mit
        /// "unknown event (known: none in this game)" abgelehnt. Falsch - 'game_ready' feuert
        /// der WIRT selbst (LuaEngine.PollGameReady), nicht das Spiel. Wer die Liste einfach
        /// leert, nimmt dem Kern seinen einzigen Einstiegspunkt.
        /// </remarks>
        private static readonly string[] NurWirtsEreignis = new[] { "game_ready" };

        internal static string[] BekannteEreignisse()
        {
            if (!_bereit) return NurWirtsEreignis;
            return BekannteEreignisseIntern();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string[] BekannteEreignisseIntern() { return EventBridge.Known; }

        private static readonly string[] KeineWoerter = new string[0];

        internal static string[] Konsolenwoerter()
        {
            if (!_bereit) return KeineWoerter;
            return KonsolenwoerterIntern();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string[] KonsolenwoerterIntern() { return GameBridge.GetConsoleCommandWords(); }

        internal static int InstalliereErzeugteFlaeche(Script s, Table api)
        {
            if (!_bereit) return 0;
            return InstalliereErzeugteFlaecheIntern(s, api);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int InstalliereErzeugteFlaecheIntern(Script s, Table api)
        {
            return Generated.GeneratedSurface.Install(s, api);
        }

        /// <summary>
        /// Ohne Spielschicht gilt der Wirt als bereit, sobald er steht.
        /// </summary>
        /// <remarks>
        /// Sonst feuerte 'game_ready' nie, und ein Skript, das seine Zeitgeber dort aufzieht,
        /// bliebe fuer immer stumm - der Kern (Zeitgeber, Zustand, Log) waere damit in einem
        /// fremden Spiel wertlos, obwohl er dort vollstaendig funktioniert.
        /// </remarks>
        internal static bool IsGameReady()
        {
            if (!_bereit) return true;
            return IsGameReadyIntern();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsGameReadyIntern() { return GameBridge.IsGameReady(); }

        internal static void EreignisseAnschliessen()
        {
            if (!_bereit) return;
            EreignisseAnschliessenIntern();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EreignisseAnschliessenIntern() { EventBridge.Attach(); }

        internal static void InstanzbindungZuruecksetzen()
        {
            if (!_bereit) return;
            InstanzbindungZuruecksetzenIntern();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InstanzbindungZuruecksetzenIntern() { EventBridge.ResetInstanceBinding(); }

        internal static void Abloesen()
        {
            if (!_bereit) return;
            AbloesenIntern();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AbloesenIntern() { EventBridge.Detach(); }
    }
}
