using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ScriptOne.Host
{
    /// <summary>
    /// Die Konfiguration: <c>ScriptOne\ScriptOne-Starter.cfg</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ DIESE KLASSE LAG BIS 2026-08-20 IM PRELOADER - und damit lag auch die einzige Stelle
    /// dort, die die Datei ANLEGT. Wer ScriptOne als PLUGIN installierte (MelonLoader oder
    /// BepInEx), bekam deshalb nie eine Konfigurationsdatei: gemessen in Cocaine Dealer, wo
    /// unter ScriptOne\ nur documentation, licenses und surface.txt standen. Fuer den Nutzer
    /// hiess das, dass es die Schalter nicht gibt - denn was nicht in der Datei steht,
    /// existiert fuer ihn nicht (Regel 1 unten sagt genau das). Ein Schalter, den drei von
    /// fuenf Ladern nie anbieten, ist kein Schalter. Deshalb liegt die Klasse jetzt im Kern,
    /// den alle fuenf uebersetzen.
    /// </remarks>
    /// <remarks>
    /// ENTWURFSREGELN, die hier absichtlich gelten:
    ///
    /// 1. FEHLT die Datei, wird sie mit den Vorgaben ANGELEGT - mit Kommentaren. Eine
    ///    Konfiguration, die man erst schreiben muss, um zu sehen was es gibt, wird nicht
    ///    benutzt.
    /// 2. Ein UNBEKANNTER Schluessel wird BEHALTEN und gemeldet, nicht weggeworfen. Sonst
    ///    loescht ein Downgrade still die Einstellungen des Nutzers.
    /// 3. Ein UNLESBARER Wert faellt auf die Vorgabe zurueck und wird GEMELDET. Still auf
    ///    den Vorgabewert zurueckzufallen ist derselbe Fehler wie ein leeres catch.
    /// 4. Gelesen wird VOR allem anderen - die Konsole muss stehen, bevor die erste
    ///    Meldung faellt, sonst sieht der Nutzer den Start nicht.
    /// 5. Die Datei wird nach dem Lesen IMMER neu geschrieben. Sonst sieht ein Nutzer,
    ///    der sie einmal hat, keine einzige spaetere Verbesserung der Beschreibung -
    ///    genau so entstand eine Konfiguration ohne jeden Kommentar, waehrend die
    ///    Vorlage im Quelltext laengst welche trug. Werte und unbekannte Schluessel
    ///    ueberleben das (Regel 2).
    ///
    /// Kommentare in der Datei sind ENGLISCH: sie landen beim Nutzer.
    /// </remarks>
    internal sealed class HostConfig
    {
        /// <summary>
        /// Der feste Titel des Konsolenfensters. Steht HIER, weil ihn zwei Stellen brauchen:
        /// das Fenster setzt ihn, und die Konfigurationsdatei verspricht ihn dem Nutzer, damit
        /// er das Fenster nach Namen wiederfindet. Zwei Literale waeren zwei Wahrheiten.
        /// </summary>
        internal const string Fenstertitel = "ScriptOne Lua Loader";

        /// <summary>Der Dateiname - eine Quelle fuer alle fuenf Wirte.</summary>
        internal const string Dateiname = "ScriptOne-Starter.cfg";

        /// <summary>
        /// Ein eigenes Konsolenfenster mit dem Protokoll: <c>auto</c> | <c>true</c> | <c>false</c>.
        /// </summary>
        /// <remarks>
        /// ⚠ DREIWERTIG, UND ZWAR AUS EINEM GRUND, DEN EIN BOOL NICHT TRAGEN KANN. Vorgabe war
        /// <c>false</c>. Laeuft ScriptOne aber ALLEIN - kein MelonLoader, kein BepInEx -, gibt es
        /// im ganzen Spiel kein Fenster, das irgendetwas anzeigt: der Nutzer sieht nichts, bis er
        /// von sich aus in eine Logdatei sieht, von der er nichts weiss. Genau dieser Fall
        /// entsteht auch von SELBST, ohne dass jemand den Installer noch einmal startet - naemlich
        /// wenn der Fremdlader spaeter geloescht wird; die Konfigurationsdatei steht dann laengst
        /// mit ihrem alten Wert da. Eine feste Vorgabe kann das nicht abbilden.
        ///
        /// Mit einem BOOL waere „hat der Nutzer bewusst false gesetzt?" nicht von „stand halt so
        /// in der Vorlage" zu unterscheiden - der Installer schreibt die Zeile ja immer. Der
        /// dritte Wert ist genau diese Unterscheidung, und er ist im Projekt schon eingefuehrt
        /// (<c>scene_objects = auto</c>).
        /// </remarks>
        internal string Console = Auto;

        internal const string Auto = "auto";

        /// <summary>
        /// Soll DIESER Wirt ein Fenster oeffnen? Bei <c>auto</c> entscheidet die Lage.
        /// </summary>
        /// <param name="alleine">
        /// Ist ScriptOne der einzige Wirt hier - also kein Fremdlader, den es selbst nachgestartet
        /// hat und der seine eigene Ausgabe mitbringt?
        /// </param>
        internal bool KonsoleAn(bool alleine)
        {
            if (string.Equals(Console, "true", StringComparison.OrdinalIgnoreCase))  return true;
            if (string.Equals(Console, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return alleine;
        }

        /// <summary>
        /// Wie viel von Il2CppInterops eigenen Meldungen durchkommt: off | warn | all.
        /// </summary>
        /// <remarks>
        /// ⚠ 'all' ist ABSICHTLICH nicht die Vorgabe. Il2CppInterop protokolliert seine
        /// gesamte Methodenaufloesung mit, und Zeilen wie „Unable to find method
        /// GameObject::GetComponent" sind dabei NORMAL - generische Methoden werden immer so
        /// aufgeloest. Gemessen 17 KB fuer einen fehlerfreien Lauf. Wer alles durchlaesst,
        /// ersaeuft genau die Meldungen, wegen derer man den Logger eingeschaltet hat.
        /// 'warn' laesst Warnungen und Fehler durch - darunter „Field X was not found on
        /// class Y", die Meldung fuer veraltete Proxies.
        /// </remarks>
        internal string InteropLog = "warn";

        /// <summary>Beim Start pruefen, ob die Proxy-Assemblies zum Spielstand passen.</summary>
        internal bool CheckInterop = true;

        /// <summary>safe | extended | unrestricted.</summary>
        /// <remarks>
        /// ⚠ ZWEI LESER, EINE DATEI - und das ist Absicht. GEWERTET wird der Wert von
        /// <see cref="ScriptOne.Host.ConsolePolicy"/> bzw. <see cref="HostSchalter"/>, weil dort
        /// auch die Pruefung und die Warnung bei einem unsinnigen Wert liegen. Hier steht er
        /// nur, damit das Neuschreiben ihn nicht verliert und er nicht als unbekannter
        /// Schluessel gemeldet wird. Diese Klasse besitzt die DATEI, nicht die Bedeutung.
        /// </remarks>
        internal string ConsolePolicy = ScriptOne.Host.ConsolePolicy.Sicher;

        /// <summary>Wie weit die erzeugte Flaeche reicht - siehe <see cref="ScriptOne.Host.SurfacePolicy"/>.</summary>
        internal string SurfacePolicy = ScriptOne.Host.SurfacePolicy.Normal;

        /// <summary>auto | on | off - siehe <see cref="ScriptOne.Host.SurfaceScan"/>.</summary>
        internal string SceneObjects = ScriptOne.Host.SurfacePolicy.SzeneAuto;

        /// <summary>Zusatzmeldungen fuer Fachleute. Wie oben: gewertet wird der Wert im Host.</summary>
        internal bool Debug;

        internal readonly List<string> Meldungen = new List<string>();

        /// <summary>Zeilen mit unbekannten Schluesseln - werden beim Neuschreiben angehaengt (Regel 2).</summary>
        private readonly List<string> _fremd = new List<string>();

        /// <summary>
        /// Der Text der Datei. ENGLISCH - er landet beim Nutzer, der kein Deutsch kann und
        /// auch keinen Quelltext liest. Was hier nicht steht, existiert fuer ihn nicht.
        /// </summary>
        private const string Vorlage =
@"# ScriptOne - configuration
#
# Rewritten on every start, so it always shows the options this build has.
# Your values are kept, and so are keys this build does not know about.
# Delete the file to get the defaults back.
#
# Switches take true/false (1/0, yes/no, on/off work too). Everything after the
# '=' is the value - do not put a comment there. An unreadable value falls back
# to the default AND says so in the log; it is never silently ignored.
#
# The log is always written and nothing here turns it off: the current run is
# ScriptOne\ScriptOne.log, the five before it are in ScriptOne\logs\.
# What the other folders are for is explained in ScriptOne\documentation\.

#=== console ============================================ default: {8}
#    auto | true | false
#    A window of its own showing the log while you play. It opens before the
#    game does, so you also see the startup.
#    ""auto"" means: on when ScriptOne runs on its own, off when it starts another
#    loader (BepInEx) that brings its own output. That also covers the case where
#    you remove that loader later - ScriptOne then runs alone and shows the window
#    by itself, without you reinstalling anything.
#    Under MelonLoader or BepInEx as a plugin, their console is used and this does
#    nothing either way.
console = {0}

#=== interop_log ======================================== default: {9}
#    off | warn | all
#    How much of Il2CppInterop's own output reaches the log. Il2Cpp games only.
#    'all' is about 17 KB per clean start and most of it is normal chatter, so
#    it buries what you turned the log on for. 'off' hides the one message that
#    tells you the proxies no longer fit the game.
interop_log = {1}

#=== check_interop ====================================== default: {10}
#    true | false
#    Compare the proxy assemblies against the installed game on every start.
#    Leave it on: after a game update nothing else notices that they no longer
#    match - the host starts normally and calls go to addresses that moved.
check_interop = {2}

#=== console_policy ===================================== default: {11}
#    safe | extended | unrestricted
#    What a script may send to the game's own developer console via s1.console.
#    An ALLOW-list, so a game update cannot quietly widen it.
#      safe          view, camera and movement only - nothing that touches a save
#      extended      plus six with a side effect worth knowing about
#      unrestricted  everything; only for scripts you wrote yourself
#    'bind' is refused below 'unrestricted' whatever else is allowed.
console_policy = {3}

#=== surface_policy ===================================== default: {12}
#    normal | readonly | off
#    How far the surface found in YOUR game may reach.
#      normal    everything that was found
#      readonly  only values and argument-less methods that return something
#      off       no generated surface, only the hand-written core functions
#    The real control is the file: open ScriptOne\surface.txt and delete the
#    lines you do not want.
surface_policy = {4}

#=== scene_objects ====================================== default: {13}
#    auto | on | off
#    Games hand out their managers in two ways: behind a static 'Instance', or
#    as plain components sitting in the scene. 'auto' binds the second kind only
#    when the first found nothing - in a game that uses singletons it would
#    otherwise add hundreds of sample and helper components.
scene_objects = {5}

#=== debug ============================================== default: {14}
#    true | false
#    Extra lines for people working on ScriptOne itself. Not warnings, not
#    errors - nothing is wrong when they appear.
debug = {6}
{7}";

        internal static HostConfig LiesOderLege(string pfad)
        {
            var c = new HostConfig();
            try
            {
                if (!File.Exists(pfad))
                {
                    c.Schreibe(pfad);
                    c.Meldungen.Add("config created with defaults: " + pfad);
                    return c;
                }

                foreach (var roh in File.ReadAllLines(pfad))
                {
                    var zeile = roh.Trim();
                    if (zeile.Length == 0 || zeile[0] == '#' || zeile[0] == ';') continue;
                    var i = zeile.IndexOf('=');
                    if (i <= 0) { c.Meldungen.Add("config: ignoring unparsable line: " + zeile); continue; }

                    var schluessel = zeile.Substring(0, i).Trim().ToLowerInvariant();
                    var wert = zeile.Substring(i + 1).Trim();

                    switch (schluessel)
                    {
                        case "console":       c.Console      = c.AutoBool(schluessel, wert, c.Console); break;
                        case "console_title":
                            // Abgeschafft: der Fenstertitel ist fest, damit das Fenster
                            // nach Namen auffindbar bleibt. Nicht als "unbekannt" melden -
                            // der Schluessel war einmal gueltig, das ist keine Verirrung des Nutzers.
                            c.Meldungen.Add("config: 'console_title' is no longer used - the console window is always titled \"" + Fenstertitel + "\"");
                            break;
                        case "interop_log":
                            var stufe = wert.ToLowerInvariant();
                            if (stufe == "off" || stufe == "warn" || stufe == "all") c.InteropLog = stufe;
                            else if (stufe == "true")  c.InteropLog = "warn";   // alte Schreibweise
                            else if (stufe == "false") c.InteropLog = "off";
                            else c.Meldungen.Add("config: 'interop_log' must be off|warn|all, got '" + wert + "' - using " + c.InteropLog);
                            break;
                        case "check_interop": c.CheckInterop = c.Bool(schluessel, wert, c.CheckInterop); break;
                        case "console_policy":
                            if (wert.Length > 0) c.ConsolePolicy = wert.ToLowerInvariant();
                            break;
                        case "surface_policy":
                            if (wert.Length > 0) c.SurfacePolicy = wert.ToLowerInvariant();
                            break;
                        case "scene_objects":
                            if (wert.Length > 0) c.SceneObjects = wert.ToLowerInvariant();
                            break;
                        case "debug":         c.Debug         = c.Bool(schluessel, wert, c.Debug); break;
                        default:
                            // Regel 2: behalten und melden, nicht wegwerfen. Weil die Datei
                            // gleich neu geschrieben wird, muss die Zeile dafuer aufgehoben
                            // werden - sonst loescht ausgerechnet das Neuschreiben sie.
                            c._fremd.Add(zeile);
                            c.Meldungen.Add("config: unknown key kept as-is: " + schluessel);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                c.Meldungen.Add("config: could not be read (" + ex.GetType().Name + ": " + ex.Message + ") - using defaults");
                return c;   // NICHT neu schreiben - sonst ueberschreibt ein Lesefehler die Datei des Nutzers.
            }

            // Regel 5: mit der aktuellen Beschreibung neu schreiben. Ohne das sieht ein
            // Nutzer, der die Datei schon hat, keine einzige spaetere Verbesserung.
            try { c.Schreibe(pfad); }
            catch (Exception ex) { c.Meldungen.Add("config: could not be rewritten (" + ex.GetType().Name + ") - keeping the file as it is"); }
            return c;
        }

        /// <summary>
        /// Wie <see cref="Bool"/>, aber mit <c>auto</c> als drittem gueltigen Wert - und es gibt
        /// die Schreibweise des Nutzers NORMALISIERT zurueck, damit die Datei beim Neuschreiben
        /// nicht seine Eingabe veraendert und nicht ploetzlich etwas anderes behauptet.
        /// </summary>
        private string AutoBool(string schluessel, string wert, string vorgabe)
        {
            switch (wert.ToLowerInvariant())
            {
                case Auto:                                    return Auto;
                case "true": case "1": case "yes": case "on":  return "true";
                case "false": case "0": case "no": case "off": return "false";
                default:
                    Meldungen.Add(string.Format(CultureInfo.InvariantCulture,
                        "config: '{0}' has unreadable value '{1}' - using default {2}", schluessel, wert, vorgabe));
                    return vorgabe;
            }
        }

        /// <summary>Regel 3: unlesbarer Wert -&gt; Vorgabe UND Meldung.</summary>
        private bool Bool(string schluessel, string wert, bool vorgabe)
        {
            switch (wert.ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on":  return true;
                case "false": case "0": case "no": case "off": return false;
                default:
                    Meldungen.Add(string.Format(CultureInfo.InvariantCulture,
                        "config: '{0}' has unreadable value '{1}' - using default {2}", schluessel, wert, vorgabe));
                    return vorgabe;
            }
        }

        private void Schreibe(string pfad)
        {
            var dir = Path.GetDirectoryName(pfad);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var fremd = _fremd.Count == 0 ? "" :
                Environment.NewLine + "# Keys this build does not know. Kept so a downgrade cannot lose them." +
                Environment.NewLine + string.Join(Environment.NewLine, _fremd.ToArray()) + Environment.NewLine;
            // ⚠ DIE VORGABEN KOMMEN AUS EINER FRISCHEN INSTANZ, nicht als fester Text in der
            //   Vorlage. Sonst stuende neben jedem Schluessel eine zweite Wahrheit: aendert
            //   jemand ein Feld, luegt die Datei ab dem naechsten Start - und zwar dem NUTZER
            //   gegenueber, der keinen Quelltext liest. So kann sie es nicht.
            var v = new HostConfig();
            var text = string.Format(CultureInfo.InvariantCulture, Vorlage,
                Console, InteropLog, CheckInterop ? "true" : "false",
                ConsolePolicy, SurfacePolicy, SceneObjects, Debug ? "true" : "false", fremd,
                v.Console, v.InteropLog, v.CheckInterop ? "true" : "false",
                v.ConsolePolicy, v.SurfacePolicy, v.SceneObjects, v.Debug ? "true" : "false");
            // BOM-frei: die Datei wird auch von Skripten gelesen.
            File.WriteAllText(pfad, text.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
        }
    }
}
