using System;
using System.Collections.Generic;
using System.IO;

namespace ScriptOne.Host
{
    /// <summary>
    /// Entscheidet, welche Zeile ein Skript per <c>s1.console(...)</c> an die
    /// Entwicklerkonsole des Spiels schicken darf.
    /// </summary>
    /// <remarks>
    /// WARUM ES DAS BRAUCHT. Bis hierher reichte <c>s1.console</c> jede Zeile durch; die
    /// einzige Pruefung war "nicht leer". Fuer ein Experiment war das richtig. Fuer eine
    /// Auslieferung nicht: die Konsole des Spiels setzt Geld, Rang, Questzustaende, Zeit,
    /// Wetter und Gesundheit, und sie kann den Spielstand speichern.
    ///
    /// ⚠ DIE WICHTIGSTE EINZELHEIT - <c>bind</c> HEBELT JEDE LISTE AUS.
    /// <c>bind &lt;taste&gt; &lt;befehl...&gt;</c> legt die restliche Zeile in
    /// <c>Console.keyBindings</c> ab. <c>Console.Update()</c> prueft die gebundenen Tasten
    /// je Bild und ruft dann selbst <c>SubmitCommand(...)</c> - vollstaendig ausserhalb von
    /// ScriptOne. Ein Skript, das <c>bind t changecash 999999</c> senden darf, hat damit
    /// Zugriff auf ALLE Befehle, obwohl die Liste <c>changecash</c> sperrt. Eine Positivliste
    /// ohne diese Sperre ist keine.
    ///
    /// WARUM POSITIVLISTE UND NICHT NEGATIVLISTE: eine Sperrliste muss vollstaendig sein,
    /// eine Erlaubnisliste nur richtig. Nach einem Spiel-Update kommen neue Befehle hinzu -
    /// die sind dann automatisch verboten statt automatisch erlaubt.
    ///
    /// DIE EINTEILUNG IST GEMESSEN, nicht geschaetzt: 63 Befehle sind in
    /// <c>Console.Awake()</c> registriert (ein 64. ist definiert, aber nie registriert).
    /// Die Zuordnung entstand durch Lesen jedes Befehlsrumpfes im Mono-Dekompilat.
    /// Zwei Beispiele, warum das noetig war:
    ///   * <c>triggerlightning</c> sieht nach Wetteroptik aus, endet aber in
    ///     <c>CombatManager.CreateExplosion</c> - Schaden an Spielern und NPCs.
    ///   * <c>hideui</c> hat KEIN Gegenstueck; es gibt kein <c>showui</c>. Ein Skript
    ///     koennte den Spieler dauerhaft ohne HUD zuruecklassen.
    ///
    /// Nutzersichtbare Meldungen sind ENGLISCH.
    /// </remarks>
    internal static class ConsolePolicy
    {
        internal const string Sicher      = "safe";
        internal const string Erweitert   = "extended";
        internal const string Unbegrenzt  = "unrestricted";

        /// <summary>Der Befehl, der jede Liste aushebelt. Nur unter 'unrestricted' erlaubt.</summary>
        private const string Bindebefehl = "bind";

        /// <summary>
        /// Uneingeschraenkt harmlos: nur Sicht, Kamera und Bewegungsparameter. Jeder hat ein
        /// Gegenstueck oder ist ein Umschalter, keiner beruehrt Spielstand oder Wirtschaft.
        /// </summary>
        private static readonly string[] KernListe =
        {
            "setmovespeed", "setjumpforce", "setgravitymultiplier", "freecam",
            "showfps", "hidefps",
            "enableocclusionculling", "disableocclusionculling",
            "enableterrain", "disableterrain",
            "enableinstancing", "disableinstancing",
            "setemotion", "triggerdistantthunder",
        };

        /// <summary>
        /// Vertretbar, aber nicht als Vorgabe - jeder hat eine Nebenwirkung, die man kennen muss.
        /// </summary>
        /// <remarks>
        /// <c>enablephysics</c>/<c>disablephysics</c>: im Aus-Zustand friert die ganze Welt.
        /// <c>enable</c>/<c>disable</c> &lt;label&gt;: wirkt auf eine Liste, die im Console-Prefab
        /// gepflegt wird - ihr Inhalt ist NICHT gemessen.
        /// <c>setweather</c>: wirkt netzwerkweit, nicht nur lokal.
        /// <c>setstaminareserve</c>: setzt zusaetzlich die aktuelle Ausdauer.
        /// </remarks>
        private static readonly string[] ZweiteStufe =
        {
            "enablephysics", "disablephysics", "enable", "disable",
            "setweather", "setstaminareserve",
        };

        /// <summary>Liest die Stufe aus einer <c>schluessel = wert</c>-Datei. Fehlt sie: sicher.</summary>
        /// <remarks>
        /// EINE Quelle fuer beide Zweige. Der Plugin-Bau hat kein MelonPreferences und der
        /// Standalone-Bau seine eigene Konfiguration; beide auf dieselbe Datei zu lassen ist
        /// billiger als zwei Mechanismen, die auseinanderlaufen.
        /// </remarks>
        internal static string LiesStufe(string cfgPfad, IScriptLog log)
        {
            try
            {
                if (!File.Exists(cfgPfad)) return Sicher;
                foreach (var roh in File.ReadAllLines(cfgPfad))
                {
                    var z = roh.Trim();
                    if (z.Length == 0 || z[0] == '#' || z[0] == ';') continue;
                    var i = z.IndexOf('=');
                    if (i <= 0) continue;
                    if (z.Substring(0, i).Trim().ToLowerInvariant() != "console_policy") continue;

                    var w = z.Substring(i + 1).Trim().ToLowerInvariant();
                    if (w == Sicher || w == Erweitert || w == Unbegrenzt) return w;
                    log.Warn("config: 'console_policy' must be " + Sicher + "|" + Erweitert + "|" +
                             Unbegrenzt + ", got '" + w + "' - using " + Sicher);
                    return Sicher;
                }
            }
            catch (Exception ex)
            {
                log.Warn("config: could not read 'console_policy' (" + ex.GetType().Name + ") - using " + Sicher);
            }
            return Sicher;
        }

        /// <summary>Die erlaubten Befehle einer Stufe. <c>null</c> heisst "alle".</summary>
        private static HashSet<string> Erlaubte(string stufe)
        {
            if (stufe == Unbegrenzt) return null;
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in KernListe) s.Add(c);
            if (stufe == Erweitert) foreach (var c in ZweiteStufe) s.Add(c);
            return s;
        }

        /// <summary>
        /// Prueft eine Zeile. Gibt <c>null</c> zurueck, wenn sie durchgehen darf, sonst den
        /// Grund - fertig formuliert fuer das Protokoll.
        /// </summary>
        internal static string Ablehnungsgrund(string zeile, string stufe)
        {
            if (string.IsNullOrEmpty(zeile)) return "empty line";

            // Das Spiel schreibt jedes Argument klein und trennt an Leerzeichen
            // (Console.SubmitCommand). Genauso wird hier zerlegt - eine andere Zerlegung
            // wuerde etwas anderes pruefen, als das Spiel dann ausfuehrt.
            var teile = zeile.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (teile.Length == 0) return "empty line";
            var befehl = teile[0].ToLowerInvariant();

            // ⚠ VOR der Listenpruefung, und auch unter 'extended': bind wuerde die Liste
            // aushebeln, indem es einen beliebigen Befehl an eine Taste haengt.
            if (befehl == Bindebefehl && stufe != Unbegrenzt)
                return "'bind' is never allowed below policy '" + Unbegrenzt +
                       "' - it stores an arbitrary command for the game to run later, " +
                       "which would bypass this allow-list entirely";

            var erlaubt = Erlaubte(stufe);
            if (erlaubt == null) return null;                 // unrestricted
            if (erlaubt.Contains(befehl)) return null;

            return "'" + befehl + "' is not in the '" + stufe + "' allow-list " +
                   "(set console_policy in ScriptOne-Starter.cfg to change this)";
        }

        /// <summary>
        /// Haelt die Liste gegen die ECHTE Befehlstabelle des Spiels und meldet Namen, die es
        /// nicht mehr gibt.
        /// </summary>
        /// <remarks>
        /// Ohne das faellt eine Umbenennung im Spiel nur dem Nutzer auf, und zwar als
        /// "geht nicht mehr" ohne Grund. <paramref name="istBefehle"/> kommt aus
        /// <c>Console.Commands</c> - der Aufrufer holt es, damit diese Datei backendfrei bleibt.
        /// </remarks>
        internal static void PruefeGegenSpiel(IEnumerable<string> istBefehle, string stufe, IScriptLog log)
        {
            if (istBefehle == null) return;
            var ist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in istBefehle) if (!string.IsNullOrEmpty(c)) ist.Add(c);
            if (ist.Count == 0) return;

            var fehlend = new List<string>();
            foreach (var c in KernListe) if (!ist.Contains(c)) fehlend.Add(c);
            if (stufe == Erweitert)
                foreach (var c in ZweiteStufe) if (!ist.Contains(c)) fehlend.Add(c);

            if (fehlend.Count == 0)
            {
                log.Info("console allow-list: " + (stufe == Unbegrenzt ? "not applied (policy '" + Unbegrenzt + "')"
                                                                       : "all entries exist in this game build"));
                return;
            }
            log.Warn("console allow-list: " + fehlend.Count + " command(s) no longer exist in this game build: " +
                     string.Join(", ", fehlend.ToArray()));
            log.Warn("  scripts using them will be rejected. The game likely renamed or removed them.");
        }

        /// <summary>Eine Zeile fuer das Protokoll beim Start - damit die Stufe nie stillschweigend gilt.</summary>
        internal static string Startmeldung(string stufe)
        {
            if (stufe == Unbegrenzt)
                return "console policy: UNRESTRICTED - scripts may run any of the game's console " +
                       "commands, including money, rank, quests and save. Only run scripts you trust.";
            var n = stufe == Erweitert ? KernListe.Length + ZweiteStufe.Length : KernListe.Length;
            return "console policy: " + stufe + " (" + n + " commands allowed, 'bind' blocked)";
        }
    }
}
