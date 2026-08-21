using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ScriptOne.Host
{
    /// <summary>
    /// Laedt MoonSharp aus der eingebetteten Ressource statt aus einer Datei neben der Mod-DLL.
    /// </summary>
    /// <remarks>
    /// Zwei Fallen stecken hier drin, beide teuer gelernt und in der Werkstatt-Doku vermerkt:
    ///
    /// 1. Der JIT loest die Typen einer Methode auf, BEVOR er sie ausfuehrt. Nennt die Methode,
    ///    die den Resolver einhaengt, irgendwo einen MoonSharp-Typ, sucht die Laufzeit die
    ///    Assembly schon beim Betreten - also bevor der Resolver haengt. Deshalb darf hier
    ///    nichts aus MoonSharp vorkommen, und der eigentliche Start liegt hinter einem
    ///    [MethodImpl(NoInlining)]-Sprung in einer anderen Klasse (siehe LuaBootstrap).
    ///
    /// 2. Zwischen Assembly.Load und dem return darf NICHT geloggt werden. Wirft der Logger -
    ///    und MelonLogger kann das waehrend der Initialisierung -, verwirft die Laufzeit die
    ///    bereits geladene Assembly lautlos und der naechste Aufruf scheitert erneut.
    /// </remarks>
    internal static class EmbeddedAssemblies
    {
        private const string AssemblySimpleName = "MoonSharp.Interpreter";
        private const string ResourceName = "ScriptOne.Embedded.MoonSharp.Interpreter.dll";

        private static bool _installed;
        private static Assembly _cached;

        /// <summary>Letzter Fehler beim Aufloesen - wird NACH dem Laden geloggt, nie waehrenddessen.</summary>
        internal static string LastError { get; private set; }

        /// <summary>Wie oft der Haken ueberhaupt gefragt wurde, und wonach.</summary>
        /// <remarks>
        /// ⚠ WARUM MITZAEHLEN: als das Laden am 2026-08-19 in einem fremden Spiel scheiterte,
        /// war die entscheidende Frage nicht "welcher Fehler", sondern "wurde der Haken
        /// ueberhaupt gerufen". Beide Faelle sehen im Log gleich aus - eine TypeLoadException,
        /// die den Wirt beendet -, verlangen aber gegensaetzliche Abhilfen: nicht gerufen
        /// heisst, die Laufzeit fragt uns nie (danebenlegen statt einbetten); gerufen und
        /// gescheitert heisst, die Nutzlast passt nicht. Ohne Zaehler bleibt das Raten.
        /// </remarks>
        internal static int Anfragen { get; private set; }
        internal static string Gesehen { get; private set; }

        /// <summary>Ob das Vorabladen beim Start geklappt hat - die tragende Aussage im Log.</summary>
        internal static bool Vorgeladen { get; private set; }

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;

            // Der Haken bleibt - unter CoreCLR (Il2Cpp-Zweig) traegt er, dort ist er gemessen.
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;

            // ⚠ UND ER GENUEGT NICHT. Gemessen 2026-08-19 unter Unity 6 / MonoBleedingEdge mit
            //   MelonLoader 0.7.3: der Wirt starb an einer TypeLoadException auf
            //   MoonSharp.Interpreter, und unser eigener Zaehler stand dabei auf
            //   "0 request(s)" - die Laufzeit hat den Haken NIE gefragt. Die Nutzlast war also
            //   nie das Problem (Identitaet bitgleich zur Referenz, Ressource vorhanden); der
            //   Weg war es. AssemblyResolve ist eine RUECKFALLEBENE fuer die FEHLGESCHLAGENE
            //   Aufloesung, und diese Mono-Laufzeit fragt sie beim Laden eines FELDTYPS nicht.
            //
            //   Deshalb vorher laden statt auf die Frage zu warten: eine per Assembly.Load
            //   geladene Assembly steht in der Assembly-Liste der Domaene, und die durchsucht
            //   die Laufzeit, BEVOR sie irgendwo sucht oder fragt. Kostet einen Ladevorgang
            //   beim Start und macht den Haken zur zweiten Sicherung statt zur einzigen.
            _cached = LadeAusRessource();
            Vorgeladen = _cached != null;
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            Anfragen++;
            if (Gesehen == null) Gesehen = args.Name;
            else if (Anfragen <= 8) Gesehen += " | " + args.Name;

            string simple;
            try { simple = new AssemblyName(args.Name).Name; }
            catch { return null; }

            if (!string.Equals(simple, AssemblySimpleName, StringComparison.OrdinalIgnoreCase))
                return null;

            // Zweite und spaetere Anfragen: dieselbe Instanz zurueckgeben. Zwei Kopien derselben
            // Assembly waeren zwei getrennte Typidentitaeten - alles danach wirft InvalidCast.
            if (_cached != null) return _cached;

            _cached = LadeAusRessource();
            return _cached;   // KEIN Logging zwischen Load und return.
        }

        /// <summary>Liest die eingebettete Assembly und laedt sie. <c>null</c> bei Fehler, dann steht er in LastError.</summary>
        private static Assembly LadeAusRessource()
        {
            try
            {
                var self = typeof(EmbeddedAssemblies).Assembly;
                using (var stream = self.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        LastError = "Eingebettete Ressource '" + ResourceName + "' fehlt in " + self.FullName;
                        return null;
                    }

                    var buffer = new byte[stream.Length];
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var n = stream.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read != buffer.Length)
                    {
                        LastError = "Eingebettete Ressource unvollstaendig gelesen: " + read + " von " + buffer.Length + " Bytes";
                        return null;
                    }

                    return Assembly.Load(buffer);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Woher der Interpreter WIRKLICH kommt - eine Zeile fuers Protokoll.
        /// </summary>
        /// <remarks>
        /// ⚠ WARUM DAS NOETIG IST: unser Resolver ist eine RUECKFALLEBENE. AssemblyResolve
        /// feuert nur, wenn die normale Aufloesung scheitert. Liegt also schon ein
        /// MoonSharp.Interpreter im Prozess, benutzen wir DESSEN Kopie - und merken es nicht.
        ///
        /// Genau das passiert neben ifBars' S1Lua: das liefert MoonSharp als eigene Datei in
        /// UserLibs\ aus, und MelonLoader laedt UserLibs VOR Plugins (Core.cs:173 gegen 174).
        /// Wenn ScriptOne startet, ist der Name bereits vergeben.
        ///
        /// Das ist die GUTE Nachricht - eine Kopie, eine Typidentitaet, kein InvalidCast, und
        /// wir koennen ihre Fassung nicht verdraengen. Aber eine stille Ersetzung des eigenen
        /// Interpreters gehoert protokolliert: laeuft etwas schief, ist das die erste Frage.
        /// </remarks>
        internal static string Herkunft()
        {
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                                   .FirstOrDefault(a => string.Equals(a.GetName().Name, AssemblySimpleName,
                                                                      StringComparison.OrdinalIgnoreCase));
                if (asm == null) return "MoonSharp: not loaded yet";

                var version = asm.GetName().Version;
                if (_cached != null && ReferenceEquals(asm, _cached))
                    return "MoonSharp " + version + " (embedded in ScriptOne)";

                string ort;
                try { ort = string.IsNullOrEmpty(asm.Location) ? "in-memory, not ours" : asm.Location; }
                catch { ort = "location unavailable"; }
                return "MoonSharp " + version + " loaded from file: " + ort;
            }
            catch (Exception ex) { return "MoonSharp: origin unknown (" + ex.GetType().Name + ")"; }
        }

        /// <summary>Text der mitgelieferten MoonSharp-Lizenz, oder null.</summary>
        internal static string ReadLicense()
        {
            try
            {
                using (var s = typeof(EmbeddedAssemblies).Assembly.GetManifestResourceStream("ScriptOne.Embedded.MoonSharp.LICENSE.txt"))
                {
                    if (s == null) return null;
                    using (var r = new StreamReader(s)) return r.ReadToEnd();
                }
            }
            catch { return null; }
        }
    }
}
