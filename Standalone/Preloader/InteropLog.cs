using System;
using Microsoft.Extensions.Logging;

namespace ScriptOne.Preloader
{
    /// <summary>
    /// Leitet Il2CppInterops eigene Meldungen in unser Protokoll.
    /// </summary>
    /// <remarks>
    /// ⚠ WARUM DAS NICHT OPTIONAL IST - teuerster Einzelbefund der Recherche vom 2026-08-18.
    ///
    /// Ohne diesen Logger benutzt Il2CppInterop einen NullLogger, und genau die zwei Meldungen
    /// verschwinden, die einen veralteten Proxy-Satz benennen wuerden:
    ///     "Field X was not found on class Y"          (LogError)
    ///     "Unable to find method X::Token"            (LogTrace)
    ///
    /// Das ist deshalb so teuer, weil ein Spiel-Update den Bruch NICHT anderswo zeigt:
    ///   - Der Bau bleibt gruen. Er uebersetzt gegen denselben Ordner, der auch laeuft;
    ///     ein veralteter Proxysatz ist mit sich selbst widerspruchsfrei.
    ///   - Es gibt keine Ausnahme. Methoden loesen im Proxy ueber einen einbetonierten
    ///     Metadaten-TOKEN auf; ist der tot, kommt keine Exception, sondern eine ATTRAPPEN-
    ///     MethodInfo ohne Funktionszeiger zurueck. (Klassen und Felder loesen dagegen ueber
    ///     Namen auf - deshalb ueberleben sie ein Update haeufiger.)
    /// Zusammen heisst das: gruener Bau, kein Fehler, und der Mod tut nichts mehr. Die
    /// einzige Spur waere die Meldung, die wir hier einschalten.
    ///
    /// ZUR ABWAEGUNG, die frueher zum Weglassen fuehrte: die Erweiterungsmethode AddLogger
    /// zieht Microsoft.Extensions.Logging.Abstractions herein. Die liegt ohnehin neben uns -
    /// Il2CppInterop.Common bringt sie als Abhaengigkeit mit. Es kostet also keine Datei.
    /// </remarks>
    internal sealed class InteropLog : ILogger
    {
        private readonly FileLog _ziel;
        private readonly LogLevel _ab;

        internal InteropLog(FileLog ziel, string stufe)
        {
            _ziel = ziel;
            switch (stufe)
            {
                case "all": _ab = LogLevel.Trace;   break;
                case "off": _ab = LogLevel.None;    break;
                default:    _ab = LogLevel.Warning; break;
            }
        }

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

        /// <summary>
        /// ⚠ Vorgabe ist Warnung aufwaerts, NICHT alles. Begruendung in HostConfig.InteropLog:
        /// 'all' erzeugt 17 KB je sauberem Start und begraebt damit die Meldungen, wegen derer
        /// man den Logger ueberhaupt einhaengt. Die Staleness-Frage beantwortet ohnehin
        /// InteropStamp genauer und billiger.
        /// </summary>
        public bool IsEnabled(LogLevel logLevel) => _ab != LogLevel.None && logLevel >= _ab;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                                Exception exception, Func<TState, Exception, string> formatter)
        {
            if (_ziel == null || !IsEnabled(logLevel)) return;
            string text;
            try { text = formatter != null ? formatter(state, exception) : Convert.ToString(state); }
            catch { text = Convert.ToString(state); }
            if (string.IsNullOrEmpty(text) && exception == null) return;

            var zeile = "[interop] " + text;
            if (exception != null) zeile += " | " + exception.GetType().Name + ": " + exception.Message;

            // Die Abbildung ist mit Absicht streng: was Il2CppInterop als Warnung oder Fehler
            // meldet, ist fuer uns fast immer ein veralteter Proxy - das gehoert nicht in die
            // Masse der Info-Zeilen.
            switch (logLevel)
            {
                case LogLevel.Critical:
                case LogLevel.Error:   _ziel.Error(zeile); break;
                case LogLevel.Warning: _ziel.Warn(zeile);  break;
                default:               _ziel.Info(zeile);  break;
            }
        }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new NullScope();
            public void Dispose() { }
        }
    }
}
