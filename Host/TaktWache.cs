using System;
using System.Threading;

namespace ScriptOne.Host
{
    /// <summary>
    /// Beantwortet die eine Frage, die ein fehlerfreier Start NICHT beantwortet: laeuft der
    /// Frame-Takt wirklich?
    ///
    /// WARUM ES DAS GIBT
    /// Ein Wirt kann vollstaendig hochkommen - Konfiguration gelesen, Flaeche ermittelt, Skripte
    /// geladen, "ready" gemeldet - und trotzdem NIE ein Bild sehen. Dann sind s1.after, s1.every
    /// und alle Spielereignisse tot, waehrend das Protokoll makellos aussieht. Gemessen am
    /// 2026-08-20 in einem Spiel unter BepInEx 5: 388 gerenderte Bilder, Awake() gelaufen,
    /// Update() kein einziges Mal. Ohne eine Wache erfaehrt das niemand.
    ///
    /// ⚠ ZWEI MELDEWEGE, UND ZWAR AUS MESSUNG, NICHT AUS VORSICHT.
    /// Ein blosser Zeitgeber genuegt NICHT, weil sich die beiden Fehlerfaelle in der Zeit
    /// ueberlappen:
    ///   * Das betroffene Spiel war nach rund 6 s wieder zu - ein Waechter mit 8 s haette
    ///     geschwiegen und den Fall verpasst.
    ///   * Ein anderes, gesundes Spiel brauchte 6,9 s vom "ready" bis zum ersten Bild - ein
    ///     Waechter mit 5 s haette dort FALSCHEN Alarm geschlagen.
    /// Es gibt also keinen Schwellwert, der beides richtig macht. Deshalb meldet die Wache
    /// zweimal: spaet genug per Zeitgeber (kein Fehlalarm) UND beim HERUNTERFAHREN (kein
    /// verpasster Fall, egal wie kurz die Sitzung war). Der zweite Weg ist der wertvollere:
    /// wird das Traegerobjekt weggeraeumt, laeuft genau dann sein OnDestroy.
    ///
    /// ⚠ STATISCH IST HIER RICHTIG, nicht bequem: <see cref="HostGuard"/> laesst genau EINEN
    /// Wirt je Prozess hochkommen. Zwei Zaehler koennte es also gar nicht geben.
    /// </summary>
    internal static class TaktWache
    {
        private static long _bilder;
        private static int _gestartet;
        private static int _gemeldet;

        /// <summary>Je Bild einmal - aus dem Takt des jeweiligen Wirts.</summary>
        internal static void Bild() { Interlocked.Increment(ref _bilder); }

        internal static long Bilder { get { return Interlocked.Read(ref _bilder); } }

        /// <summary>Fuer die Testbank: Zaehler und Sperren zuruecksetzen.</summary>
        internal static void Zuruecksetzen()
        {
            Interlocked.Exchange(ref _bilder, 0);
            Interlocked.Exchange(ref _gestartet, 0);
            Interlocked.Exchange(ref _gemeldet, 0);
        }

        /// <summary>
        /// Haengt den Zeitgeber ein. <paramref name="hinweis"/> ist wirtsspezifisch - eine
        /// Diagnose, die auf den falschen Mechanismus zeigt, schickt den Leser in die falsche
        /// Richtung und ist schlechter als keine.
        /// </summary>
        /// <param name="sekunden">
        /// Grosszuegig waehlen. Der Zeitgeber ist die Haelfte gegen FEHLALARM; die Haelfte gegen
        /// VERPASSEN ist <see cref="Schluss"/>.
        /// </param>
        internal static void Starte(IScriptLog log, string hinweis, int sekunden = 15)
        {
            if (log == null) return;
            Starte(log.Warn, hinweis, sekunden);
        }

        /// <summary>
        /// Dieselbe Wache ohne <see cref="IScriptLog"/>. ⚠ Nicht Bequemlichkeit: die Wirte
        /// haben verschiedene Logger, und die Wache soll AUCH dann melden koennen, wenn gar
        /// kein Wirt zustande kam - dann gibt es namentlich keinen IScriptLog. Ein Waechter,
        /// der genau im schlimmsten Fall keinen Kanal hat, ist keiner.
        /// </summary>
        internal static void Starte(Action<string> warn, string hinweis, int sekunden = 15)
        {
            if (warn == null) return;
            if (Interlocked.Exchange(ref _gestartet, 1) != 0) return;   // genau einer
            try
            {
                var t = new Thread(() =>
                {
                    try
                    {
                        Thread.Sleep(sekunden * 1000);
                        if (Bilder == 0) Melde(warn, "in the first " + sekunden + " seconds", hinweis);
                    }
                    catch { }
                });
                t.IsBackground = true;
                t.Name = "ScriptOne tick watchdog";
                t.Start();
            }
            catch
            {
                // Kein Thread zu bekommen ist kein Grund, den Wirt zu gefaehrden - die
                // Schluss-Meldung traegt den Fall dann allein.
            }
        }

        /// <summary>
        /// Beim Herunterfahren des Wirts oder beim Wegraeumen seines Traegerobjekts aufrufen.
        /// Meldet, wenn bis dahin KEIN Bild kam.
        /// </summary>
        internal static void Schluss(IScriptLog log, string hinweis)
        {
            if (log == null) return;
            Schluss(log.Warn, hinweis);
        }

        internal static void Schluss(Action<string> warn, string hinweis)
        {
            if (warn == null || Bilder != 0) return;
            Melde(warn, "for the whole session", hinweis);
        }

        private static void Melde(Action<string> warn, string wann, string hinweis)
        {
            if (Interlocked.Exchange(ref _gemeldet, 1) != 0) return;    // genau einmal
            try
            {
                warn("NO FRAME TICK " + wann + " - s1.after, s1.every and every game event" +
                     " are DEAD in this session.");
                warn("  Everything else worked: scripts loaded, s1.log and the generated" +
                     " surface are fine. That is why the start looks flawless.");
                if (!string.IsNullOrEmpty(hinweis)) warn("  " + hinweis);
            }
            catch { }
        }

        /// <summary>
        /// Der Hinweis fuer einen PLUGIN-Wirt. Was hier steht, ist gemessen und nicht geraten -
        /// siehe den Klassenkopf.
        /// </summary>
        /// <summary>
        /// Wie <see cref="HinweisPlugin"/>, aber fuer den Fall, dass der Adapter sich bereits ein
        /// EIGENES Traegerobjekt gebaut hat.
        /// </summary>
        /// <remarks>
        /// ⚠ SONST NENNT DIE MELDUNG DIE FALSCHE URSACHE. HinweisPlugin behauptet woertlich, der
        /// Takt sei "the loader's own game object" - genau der Meldeweg aus dem eigenen Taktobjekt
        /// heraus feuert aber nur, WENN der Adapter sein eigenes gebaut hat. Der Nutzer bekam damit
        /// eine Ursache genannt, die in seinem Fall ausgeschlossen war, und haette dort gesucht.
        /// </remarks>
        internal static string HinweisEigenerTraeger(string lader)
        {
            return "ScriptOne built its OWN tick object here instead of relying on " + lader +
                   "'s - and it still never received a frame. That rules out the usual cause (the" +
                   " loader's carrier object being removed during the first scene load) and points" +
                   " at the object never becoming active in a scene Unity drives. Your scripts are" +
                   " intact - but anything time- or event-based will not run in this game." +
                   " Please report this with the log: it is the case that is not yet understood.";
        }

        internal static string HinweisPlugin(string lader)
        {
            return "ScriptOne runs as a " + lader + " plugin, so its frame tick is the loader's own" +
                   " game object. In some games that object is created before the first scene" +
                   " exists, and then nothing drives it - this was measured in one game on" +
                   " 2026-08-20 and it is not specific to ScriptOne: every plugin of that loader" +
                   " would lose its Update there. Nothing you did is wrong, and your scripts are" +
                   " intact - but anything time- or event-based will not run in this game.";
        }
    }
}
