using System;

#if IL2CPP
using GameConsole = Il2CppScheduleOne.Console;
using GamePlayer = Il2CppScheduleOne.PlayerScripts.Player;
using GamePlayerMovement = Il2CppScheduleOne.PlayerScripts.PlayerMovement;
#endif
#if MONO
using GameConsole = ScheduleOne.Console;
using GamePlayer = ScheduleOne.PlayerScripts.Player;
using GamePlayerMovement = ScheduleOne.PlayerScripts.PlayerMovement;
#endif

namespace ScriptOne.Game
{
    /// <summary>
    /// Die EINZIGE Stelle im Mod, die das Spiel kennt. Alles darueber liegende arbeitet nur
    /// mit Zahlen, Zeichenketten und Wahrheitswerten - so kann kein Il2Cpp-Proxy die
    /// Lua-Grenze erreichen, und die ganze Interop-Fallenklasse entfaellt.
    /// </summary>
    /// <remarks>
    /// Warum hier nur Typaliase und kein weiteres #if noetig ist:
    /// Il2CppInterop macht aus oeffentlichen FELDERN Properties. Im Mono-Dekompilat steht
    ///   ScheduleOne.PlayerScripts.Player.cs:74          public static Player Local;
    ///   ScheduleOne.PlayerScripts.PlayerMovement.cs:24  public static float StaticMoveSpeedMultiplier = 1f;
    /// im Il2Cpp-Proxy dagegen
    ///   Il2CppScheduleOne.PlayerScripts.Player.cs:1982        public unsafe static Player Local { get; set; }
    ///   Il2CppScheduleOne.PlayerScripts.PlayerMovement.cs:754 public unsafe static float StaticMoveSpeedMultiplier { get; set; }
    /// Feld und Property werden im C#-QUELLTEXT gleich geschrieben - der Unterschied faellt
    /// also nur auf, wenn man ueber Reflection zugreift (dann gibt GetField() unter Il2Cpp
    /// zuverlaessig null). Wir greifen direkt zu, deshalb genuegt der Namensraumtausch.
    /// </remarks>
    internal static class GameBridge
    {
        internal static string BackendName
        {
#if IL2CPP
            get { return "Il2Cpp"; }
#else
            get { return "Mono"; }
#endif
        }

        /// <summary>
        /// Ist ein Spielstand so weit, dass Konsolenbefehle wirken?
        /// Zwei Bedingungen, beide aus dem Spielquelltext belegt:
        ///   * Console.Awake() fuellt die Befehlstabelle (Console.cs:1636-1712). Vorher meldet
        ///     SubmitCommand nur "Command 'setmovespeed' not found."
        ///   * Ein lokaler Spieler muss existieren, sonst gibt es niemanden zu beschleunigen.
        /// </summary>
        internal static bool IsGameReady()
        {
            try
            {
                if (GamePlayer.Local == null) return false;
                var cmds = GameConsole.Commands;
                return cmds != null && cmds.Count > 0;
            }
            catch
            {
                // Waehrend des Szenenwechsels koennen die Proxies kurz ins Leere zeigen.
                return false;
            }
        }

        /// <summary>
        /// Reicht eine Zeile an die Entwicklerkonsole des Spiels weiter - ohne deren UI.
        /// ScheduleOne.Console.SubmitCommand(string) ist public static (Console.cs:1773).
        ///
        /// ACHTUNG, stille Sperre: SubmitCommand(List) fuehrt NUR aus, wenn
        ///   InstanceFinder.IsHost || Application.isEditor || Debug.isDebugBuild
        /// (Console.cs:1754). Als Mehrspieler-GAST passiert wortlos nichts. Deshalb prueft
        /// dieser Mod den Erfolg hinterher am Messwert statt dem Aufruf zu glauben.
        /// </summary>
        internal static void SubmitConsoleCommand(string line)
        {
            GameConsole.SubmitCommand(line);
        }

        /// <summary>
        /// Die Befehlsworte, die das Spiel WIRKLICH kennt - flach als Zeichenketten.
        /// </summary>
        /// <remarks>
        /// Damit kann die Positivliste gegen den Ist-Zustand gehalten werden, statt zu
        /// hoffen, dass ein Spiel-Update nichts umbenannt hat. Der Rueckgabetyp ist
        /// bewusst string[] und nicht die Spielliste: ueber diese Grenze geht kein Spieltyp.
        /// </remarks>
        internal static string[] GetConsoleCommandWords()
        {
            try
            {
                var cmds = GameConsole.Commands;
                if (cmds == null) return new string[0];
                // ⚠ KEIN cmds[i]: der Il2Cpp-Proxy der Liste hat keinen Indexer (CS0021),
                // der Mono-Typ schon. foreach traegt auf beiden Backends.
                var l = new System.Collections.Generic.List<string>();
                foreach (var c in cmds)
                {
                    if (c != null && !string.IsNullOrEmpty(c.CommandWord)) l.Add(c.CommandWord);
                }
                return l.ToArray();
            }
            catch { return new string[0]; }
        }

        /// <summary>Liest den Multiplikator zurueck, den 'setmovespeed' setzt (Console.cs:253).</summary>
        internal static float GetMoveSpeedMultiplier()
        {
            return GamePlayerMovement.StaticMoveSpeedMultiplier;
        }
    }
}
