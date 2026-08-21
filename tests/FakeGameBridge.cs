using System;
using System.Collections.Generic;
using System.Globalization;

namespace ScriptOne.Game
{
    /// <summary>
    /// Attrappe des Spiels. Bildet die beiden Eigenschaften nach, auf die es ankommt:
    ///   * setmovespeed setzt PlayerMovement.StaticMoveSpeedMultiplier (Console.cs:253)
    ///   * die Zahl wird mit float.TryParse in der Kultur der Laufzeit gelesen (Console.cs:247)
    /// </summary>
    internal static class GameBridge
    {
        internal static readonly List<string> Submitted = new List<string>();
        internal static bool Ready = false;
        internal static bool IsHost = true;          // Console.cs:1754 - nur der Host fuehrt aus
        internal static float MoveSpeed = 1f;

        internal static string BackendName { get { return "Attrappe"; } }

        internal static bool IsGameReady() { return Ready; }

        /// <summary>Attrappe der Befehlstabelle. Enthaelt ABSICHTLICH nicht die ganze
        /// Positivliste - so laesst sich pruefen, dass die Gegenprobe fehlende Namen MELDET
        /// statt zu schweigen.</summary>
        internal static string[] ConsoleCommandWords = { "setmovespeed", "freecam", "changecash", "bind" };

        internal static string[] GetConsoleCommandWords() { return ConsoleCommandWords; }

        internal static void SubmitConsoleCommand(string line)
        {
            Submitted.Add(line);
            if (!IsHost) return;                      // schweigt, genau wie das Spiel

            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            if (!string.Equals(parts[0].ToLower(), "setmovespeed", StringComparison.Ordinal)) return;

            float v;
            // BEWUSST die Kultur der Laufzeit, wie das Spiel es tut.
            if (parts.Length < 2 || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.CurrentCulture, out v) || v < 0f)
                return;
            MoveSpeed = v;
        }

        internal static float GetMoveSpeedMultiplier() { return MoveSpeed; }
    }
}

namespace ScriptOne.Game
{
    /// <summary>Attrappe der EventBridge: der Pruefstand hat kein Spiel zum Abonnieren.</summary>
    internal static class EventBridge
    {
        internal static System.Action<string, string, double> Raised;
        internal static System.Action<string> Report;
        internal static readonly string[] Known =
        {
            "game_ready",
            "player_spawned", "player_arrested", "player_freed",
            "player_tased", "player_tased_end", "player_struck_by_lightning",
        };
        internal static int AttachCount;
        internal static void Attach() { AttachCount++; }
        internal static void Detach() { }
        internal static void ResetInstanceBinding() { }
        /// <summary>Nur im Pruefstand: ein Spielereignis von Hand ausloesen.</summary>
        internal static void Simulate(string evt) { var h = Raised; if (h != null) h(evt, "", 0); }
    }
}

namespace ScriptOne.Host.Generated
{
    /// <summary>
    /// Attrappe der erzeugten Flaeche. Die echte GeneratedSurface.g.cs traegt
    /// backend-spezifische using-Aliase und laesst sich ohne IL2CPP/MONO nicht uebersetzen -
    /// sie wird ohnehin durch den ECHTEN Bau geprueft (dass sie kompiliert, IST der Beweis,
    /// dass alle erzeugten Bindungen auf beiden Backends aufloesen). Hier geht es um die Wirtslogik.
    /// </summary>
    internal static class GeneratedSurface
    {
        /// <remarks>
        /// EINE Tabelle statt gar keiner - sonst laeuft der Teil des Wirts, der die erzeugte
        /// Flaeche ablaeuft (ApiReference), im Test NIE, und ein Fehler darin faellt erst im
        /// Spiel auf. Der Inhalt ist bewusst erkennbar erfunden.
        /// </remarks>
        internal static int Install(MoonSharp.Interpreter.Script script,
                                    MoonSharp.Interpreter.Table s1)
        {
            var t = new MoonSharp.Interpreter.Table(script);
            t.Set("do_something", MoonSharp.Interpreter.DynValue.NewCallback(
                (c, a) => MoonSharp.Interpreter.DynValue.NewBoolean(true)));
            t.Set("a_value", MoonSharp.Interpreter.DynValue.NewNumber(42));
            s1.Set("fake_manager", MoonSharp.Interpreter.DynValue.NewTable(t));
            return 1;
        }
    }
}

namespace ScriptOne.Host
{
    /// <summary>
    /// Attrappe der Sonde. Die echte <c>GameLayer</c> nennt absichtlich einen Spieltyp
    /// (<c>ScheduleOne.PlayerScripts.Player</c>) und laesst sich deshalb hier gar nicht
    /// uebersetzen - genau das ist ihr Zweck.
    ///
    /// WARNUNG: Ohne diese Attrappe war der HARNESS SEIT DER EINFUEHRUNG VON GameGate.cs
    /// KAPUTT (12x CS0103). Aufgefallen ist das erst beim Doku-Audit am 2026-08-19, nicht
    /// beim Bauen des Mods: der Harness haengt an KEINEM der beiden Backend-Baue, also
    /// bleibt sein Bruch unsichtbar, solange ihn niemand von Hand startet. Wer eine neue
    /// Datei in die Naht legt, traegt sie hier mit ein.
    /// </summary>
    internal static class GameLayer
    {
        /// <summary>Im Harness ist die Spielschicht da - die Attrappe IST sie.</summary>
        internal static bool Vorhanden = true;
        internal static string Grund { get { return Vorhanden ? "stub" : "stub: switched off"; } }
        internal static bool Pruefe(IScriptLog log)
        {
            if (log != null) log.Info("Game layer: stub (test harness)");
            return Vorhanden;
        }
    }
}
