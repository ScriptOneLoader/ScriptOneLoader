using System.Runtime.CompilerServices;
using MelonLoader;

namespace ScriptOne.Host
{
    /// <summary>
    /// Schleuse zwischen dem MelonMod und allem, was MoonSharp anfasst.
    /// </summary>
    /// <remarks>
    /// Der JIT loest die Typen einer Methode auf, BEVOR er sie ausfuehrt. Stuende der Aufbau des
    /// Interpreters direkt in OnInitializeMelon, muesste die Laufzeit MoonSharp schon beim
    /// Betreten dieser Methode finden - also bevor EmbeddedAssemblies.Install() den Resolver
    /// eingehaengt hat. Deshalb liegt jeder Sprung in MoonSharp-Land hinter einem eigenen
    /// [MethodImpl(NoInlining)]-Aufruf, und der Mod haelt den Wirt nur als 'object'.
    /// </remarks>
    internal static class LuaBoot
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static object Create(MelonLogger.Instance log, string scriptFolder, string stateFolder,
                                     string docFolder, string cfgPfad)
        {
            var wirtLog = new MelonLog(log);
            // Die Konsolenstufe wird HIER gelesen, weil hier der Logger schon steht - und aus
            // DERSELBEN Datei wie im Standalone-Bau. Zwei Mechanismen fuer dieselbe Frage
            // laufen auseinander; einer, den beide lesen, kann das nicht.
            var schalter = HostSchalter.LiesUndLege(cfgPfad, wirtLog);
            var engine = new LuaEngine(wirtLog, stateFolder, docFolder, schalter);
            if (schalter.Debug) VerwaisterWirt.Melde(stateFolder, wirtLog);
            engine.LoadFolder(scriptFolder);
            // Welcher Interpreter WIRKLICH laeuft. Neben einem anderen Lua-Mod, der MoonSharp
            // als eigene Datei in UserLibs\ mitbringt (z. B. ifBars' S1Lua), benutzen wir
            // DESSEN Kopie - unser Resolver ist nur Rueckfallebene und feuert dann nie.
            // Begruendung ausfuehrlich in EmbeddedAssemblies.Herkunft().
            wirtLog.Info(EmbeddedAssemblies.Herkunft());
            return engine;
        }

        /// <summary>Bindet IScriptLog an den MelonLogger. Die einzige Stelle, die beide kennt.</summary>
        private sealed class MelonLog : IScriptLog
        {
            private readonly MelonLogger.Instance _inner;
            internal MelonLog(MelonLogger.Instance inner) { _inner = inner; }
            public void Info(string message) { _inner.Msg(message); }
            public void Warn(string message) { _inner.Warning(message); }
            public void Error(string message) { _inner.Error(message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Tick(object engine)
        {
            var e = engine as LuaEngine;
            if (e != null) e.Tick();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SaveAll(object engine)
        {
            var e = engine as LuaEngine;
            if (e != null) e.SaveAll();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Poll(object engine)
        {
            var e = engine as LuaEngine;
            if (e != null) e.PollGameReady();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ResetGameReady(object engine)
        {
            var e = engine as LuaEngine;
            if (e != null) e.ResetGameReady();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int ScriptCount(object engine)
        {
            var e = engine as LuaEngine;
            return e == null ? 0 : e.ScriptCount;
        }
    }
}
