namespace ScriptOne.Host
{
    /// <summary>
    /// Die einzige Ausgabe des Wirts. Bewusst eine eigene Schnittstelle statt MelonLogger:
    /// so haengt der ganze Lua-Teil weder am Loader noch am Spiel und laesst sich mit einer
    /// Attrappe vollstaendig durchmessen. (Dieselbe Trennung, die S1Lua ueber IS1LuaHost zieht -
    /// der Grund, warum dessen Kern ohne Spiel testbar ist.)
    /// </summary>
    internal interface IScriptLog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }
}
