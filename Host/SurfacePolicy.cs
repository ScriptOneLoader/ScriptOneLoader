using System;
using System.IO;

namespace ScriptOne.Host
{
    /// <summary>
    /// Wie weit die ERZEUGTE Flaeche reichen darf. Dieselbe Mechanik wie
    /// <see cref="ConsolePolicy"/>, aus derselben Datei, mit denselben drei Stufen - denn es
    /// waere schwer erklaerbar, die Spielkonsole sorgfaeltig zu begrenzen und die halbe
    /// Spiel-API danebenzulegen.
    ///
    /// DIE STUFEN, und warum der Schnitt so und nicht anders liegt:
    ///   readonly  Nur Werte und argumentlose Methoden mit Rueckgabe. Eine Methode, die 'void'
    ///             zurueckgibt, ruft man DEFINITIONSGEMAESS nur ihrer Wirkung wegen - das ist
    ///             der einzige strukturelle Hinweis auf "veraendert etwas", den es gibt.
    ///             Gemessen an No Knock: 359 von 754 Bindungen bleiben.
    ///   normal    Alles, was der Scan gefunden hat. Vorgabe.
    ///   off       Gar keine erzeugte Flaeche; nur die handgeschriebenen Kernfunktionen.
    ///
    /// ⚠ WAS HIER BEWUSST NICHT STEHT: eine Sperrliste gefaehrlicher NAMEN ('Delete*', 'Reset*',
    /// 'Quit'). Ein Verb taugt nicht als Kriterium - dieselbe Erfahrung wie beim Klempnerei-Filter
    /// des Scans, wo 'Save' auf einem SaveManager gerade der Zweck ist und nicht der Ausschluss.
    /// Eine solche Liste waere je Spiel anders, nie vollstaendig, und wuerde Sicherheit vortaeuschen.
    /// Die wirksame Kontrolle ist eine andere: die Flaeche liegt als LESBARE DATEI da
    /// (ScriptOne\surface.txt), und wer eine Zeile nicht will, loescht sie.
    /// </summary>
    internal static class SurfacePolicy
    {
        internal const string NurLesen  = "readonly";
        internal const string Normal    = "normal";
        internal const string Aus       = "off";

        // Zweite, unabhaengige Achse: WOHER die Instanz kommt (siehe SurfaceScan.Ermittle).
        internal const string SzeneAuto = "auto";
        internal const string SzeneAn   = "on";
        internal const string SzeneAus  = "off";

        /// <summary>Liest die Stufe aus derselben <c>schluessel = wert</c>-Datei. Fehlt sie: normal.</summary>
        internal static string LiesStufe(string cfgPfad, IScriptLog log)
        {
            var w = LiesSchluessel(cfgPfad, "surface_policy", log);
            if (w == null) return Normal;
            if (w == NurLesen || w == Normal || w == Aus) return w;
            if (log != null)
                log.Warn("config: 'surface_policy' must be " + NurLesen + "|" + Normal + "|" +
                         Aus + ", got '" + w + "' - using " + Normal);
            return Normal;
        }

        /// <summary>
        /// Ob auch Objekte gebunden werden, die in der SZENE liegen statt hinter einem statischen
        /// 'Instance'. Vorgabe <c>auto</c>: nur dann, wenn die Singleton-Regel nichts gefunden hat.
        /// Warum dieser Schnitt gemessen und nicht geraten ist, steht bei
        /// <see cref="SurfaceScan"/>.
        /// </summary>
        internal static string LiesSzene(string cfgPfad, IScriptLog log)
        {
            var w = LiesSchluessel(cfgPfad, "scene_objects", log);
            if (w == null) return SzeneAuto;
            if (w == SzeneAuto || w == SzeneAn || w == SzeneAus) return w;
            if (log != null)
                log.Warn("config: 'scene_objects' must be " + SzeneAuto + "|" + SzeneAn + "|" +
                         SzeneAus + ", got '" + w + "' - using " + SzeneAuto);
            return SzeneAuto;
        }

        /// <summary>
        /// Zusatzmeldungen, die im Normalbetrieb nur stoeren wuerden. Vorgabe aus.
        /// ⚠ Was hinter diesen Schalter kommt, muss VERZICHTBAR sein. Ein Hinweis, den nur
        /// jemand braucht, der gerade an ScriptOne selbst arbeitet, gehoert hierher; eine
        /// Warnung, die dem NUTZER sagt, warum sein Skript nicht laeuft, gehoert es nicht -
        /// sonst schaltet man die Diagnose genau in dem Zustand ab, in dem sie gebraucht wird.
        /// </summary>
        internal static bool LiesDebug(string cfgPfad, IScriptLog log)
        {
            var w = LiesSchluessel(cfgPfad, "debug", log);
            return w == "true" || w == "1" || w == "yes" || w == "on";
        }

        /// <summary>
        /// Ein Schluessel aus der Datei, oder <c>null</c>, wenn er fehlt. EINE Stelle statt vier
        /// Kopien derselben Schleife - und damit auch eine Stelle, an der die Fehlerbehandlung
        /// nachweislich richtig ist.
        /// </summary>
        private static string LiesSchluessel(string cfgPfad, string schluessel, IScriptLog log)
        {
            try
            {
                if (string.IsNullOrEmpty(cfgPfad) || !File.Exists(cfgPfad)) return null;
                foreach (var roh in File.ReadAllLines(cfgPfad))
                {
                    var z = roh.Trim();
                    if (z.Length == 0 || z[0] == '#') continue;
                    var i = z.IndexOf('=');
                    if (i <= 0) continue;
                    if (z.Substring(0, i).Trim().ToLowerInvariant() != schluessel) continue;
                    return z.Substring(i + 1).Trim().ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                if (log != null)
                    log.Warn("config: could not read '" + schluessel + "' (" + ex.GetType().Name + ")");
            }
            return null;
        }

        /// <summary>
        /// Darf dieses Mitglied unter dieser Stufe gebunden werden?
        /// ⚠ 'readonly' ist eine Aussage ueber die SIGNATUR, nicht ueber das Verhalten: auch ein
        /// argumentloser Getter kann im Spiel etwas veraendern (Lazy-Initialisierung ist der
        /// Normalfall). Die Stufe schliesst aus, was NACHWEISLICH nur wegen seiner Wirkung
        /// gerufen wird - mehr kann man ohne Blick in den Rumpf nicht behaupten, und genau das
        /// gehoert in die Meldung, damit niemand sie fuer eine Garantie haelt.
        /// </summary>
        internal static bool Erlaubt(string stufe, SurfacePlan.Mitglied m)
        {
            if (stufe == Aus) return false;
            if (stufe != NurLesen) return true;
            if (m.Art != "call") return true;                       // Wert: lesend
            if (m.Rueckgabe == "void") return false;                // nur wegen der Wirkung
            return m.Args == null || m.Args.Length == 0;            // Argumente = Eingriff
        }

        internal static string Startmeldung(string stufe, int gebunden, int weggelassen)
        {
            if (stufe == Aus)
                return "surface policy: off - no generated surface, only the core functions";
            if (stufe == NurLesen)
                return "surface policy: readonly - " + gebunden + " bindings, " + weggelassen +
                       " left out because they return void or take arguments. Note this is a" +
                       " statement about signatures, not a guarantee that nothing changes state.";
            return "surface policy: normal - everything found (" + gebunden +
                   " bindings). Set surface_policy=readonly in ScriptOne-Starter.cfg to bind" +
                   " only readers, or delete lines from surface.txt.";
        }
    }
}
