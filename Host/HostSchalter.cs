namespace ScriptOne.Host
{
    /// <summary>
    /// Alle Schalter, die der WIRT aus <c>ScriptOne-Starter.cfg</c> braucht - einmal gelesen,
    /// als ein Wert weitergereicht.
    ///
    /// WARUM ES DAS GIBT
    /// Vorher bekam <see cref="LuaEngine"/> jede Stufe als eigenen Parameter, und jede der
    /// fuenf Einstiegsstellen (MelonLoader, BepInEx 5, BepInEx 6, Mono-Standalone,
    /// Il2Cpp-Standalone) baute den cfg-Pfad selbst zusammen und rief die Leser einzeln.
    /// Bei zwei Stufen ging das; bei vier waren es 20 Aufrufe, die alle dasselbe tun und von
    /// denen jeder einzeln vergessen werden kann. Genau so entsteht ein Schalter, der in drei
    /// von fuenf Ladern wirkt und in den anderen beiden lautlos auf der Vorgabe steht.
    ///
    /// ⚠ ZUSTAENDIGKEIT: <see cref="HostConfig"/> besitzt die DATEI - anlegen, unbekannte
    /// Schluessel aufheben, mit der aktuellen Beschreibung neu schreiben. Diese Klasse besitzt
    /// die BEDEUTUNG - pruefen, bei Unsinn warnen, auf die Vorgabe zurueckfallen. Beide lesen
    /// dieselbe Datei; das ist der kleinere Preis gegenueber zwei Wahrheiten.
    /// </summary>
    internal sealed class HostSchalter
    {
        internal string Konsole = ConsolePolicy.Sicher;
        internal string Flaeche = SurfacePolicy.Normal;
        internal string Szene   = SurfacePolicy.SzeneAuto;

        /// <summary>
        /// Zusatzmeldungen fuer den, der an ScriptOne selbst arbeitet. Vorgabe aus.
        /// </summary>
        internal bool Debug;

        /// <summary>Vorgaben - fuer Aufrufer ohne Konfigurationsdatei (Testbank).</summary>
        internal static HostSchalter Vorgabe { get { return new HostSchalter(); } }

        /// <summary>
        /// Legt die Konfigurationsdatei an, falls sie fehlt, schreibt sie mit der aktuellen
        /// Beschreibung neu - und liest dann die Schalter daraus.
        ///
        /// ⚠ DAS ANLEGEN GEHOERT HIERHER UND NICHT NUR IN DEN STANDALONE-ZWEIG. Vorher tat es
        /// nur der Preloader; eine Plugin-Installation hatte deshalb ueberhaupt keine
        /// Konfigurationsdatei, und damit auch keine Schalter - nicht weil sie nicht wirken
        /// wuerden, sondern weil der Nutzer nie erfaehrt, dass es sie gibt.
        /// </summary>
        internal static HostSchalter LiesUndLege(string cfgPfad, IScriptLog log)
        {
            try
            {
                var cfg = HostConfig.LiesOderLege(cfgPfad);
                if (log != null)
                    foreach (var m in cfg.Meldungen) log.Info(m);
            }
            catch (System.Exception ex)
            {
                // Eine Konfiguration, die nicht geschrieben werden kann (schreibgeschuetzter
                // Spielordner), ist kein Grund, den Wirt nicht zu starten - gelesen wird
                // gleich ohnehin mit Vorgaben.
                if (log != null) log.Warn("config: could not be created (" + ex.GetType().Name + ")");
            }
            return Lies(cfgPfad, log);
        }

        internal static HostSchalter Lies(string cfgPfad, IScriptLog log)
        {
            return new HostSchalter
            {
                Konsole = ConsolePolicy.LiesStufe(cfgPfad, log),
                Flaeche = SurfacePolicy.LiesStufe(cfgPfad, log),
                Szene   = SurfacePolicy.LiesSzene(cfgPfad, log),
                Debug   = SurfacePolicy.LiesDebug(cfgPfad, log),
            };
        }

        /// <summary>
        /// Der uebliche Fall: der Zustandsordner ist <c>&lt;spiel&gt;\ScriptOne\state</c>, die
        /// Konfiguration liegt eine Ebene darueber. Diese Rechnung stand an fuenf Stellen
        /// wortgleich; hier steht sie einmal.
        /// </summary>
        internal static HostSchalter LiesNebenZustand(string zustandOrdner, IScriptLog log)
        {
            var wurzel = System.IO.Path.GetDirectoryName(
                             (zustandOrdner ?? "").TrimEnd(System.IO.Path.DirectorySeparatorChar,
                                                           System.IO.Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(wurzel)) return Vorgabe;
            return Lies(System.IO.Path.Combine(wurzel, HostConfig.Dateiname), log);
        }
    }
}
