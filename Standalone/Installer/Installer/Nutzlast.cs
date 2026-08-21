using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace ScriptOne.Setup
{
    /// <summary>
    /// Packt die im Installer EINGEBETTETE Nutzlast aus, damit die exe ein echter
    /// Einzeldownload ist.
    /// </summary>
    /// <remarks>
    /// WARUM EINGEBETTET: ein Installer, der ohne Beipack nichts kann, ist kein Installer.
    /// Wer eine einzelne .exe herunterlaedt, erwartet, dass sie reicht.
    ///
    /// ⚠ DER ZWISCHENSPEICHER HAENGT AM INHALT, NICHT AN DER VERSION. Die erste Fassung packte
    /// nach "ScriptOne-&lt;version&gt;-payload" aus und uebersprang das Auspacken, wenn der Ordner
    /// schon stand. Das ging schief, sobald ZWEI Baue dieselbe Versionsnummer tragen - und
    /// waehrend der Entwicklung tragen sie IMMER dieselbe:
    ///
    ///   gemessen am 2026-08-19 - der Nutzer startete den frisch gebauten Installer, dieser fand
    ///   den Auspackordner des VORHERIGEN Baus (gleiche Version 0.1.0, acht Minuten aelter),
    ///   packte nicht aus und installierte dessen alten Inhalt. Der neue Bau trug die
    ///   Plugin-DLLs, der alte nicht - der Installer brach also mit "not in this package" ab,
    ///   waehrend die DLL in seiner eigenen exe lag. Der Fehler sah aus wie ein Packfehler und
    ///   war einer im Zwischenspeicher.
    ///
    /// Fuer NUTZER gilt derselbe Fall bei jeder Nachbesserung, die unter der alten Nummer
    /// erscheint. Deshalb benennt der Ordner jetzt den INHALT (SHA-256 der eingebetteten ZIP,
    /// gekuerzt): gleicher Inhalt = derselbe Ordner und Wiederverwendung ist unschaedlich,
    /// anderer Inhalt = anderer Ordner. Nebenbei faellt das Loeschen weg, das einem parallel
    /// laufenden zweiten Installer die Dateien unter den Fuessen wegzoege.
    /// </remarks>
    internal static class Nutzlast
    {
        private const string Name = "ScriptOne.Setup.payload.zip";

        /// <summary>
        /// Prueft die eingebettete Nutzlast, OHNE eine einzige Datei zu schreiben.
        /// </summary>
        /// <remarks>
        /// ⚠ HIER WURDE FRUEHER AUSGEPACKT, UND ZWAR BEI JEDEM START. Das hatte zwei Folgen:
        ///
        /// 1. `--status` verspricht woertlich "only report what is there, change nothing" und
        ///    schrieb dabei rund zwanzig DLLs nach %TEMP%. Die Zusage war schlicht unwahr.
        /// 2. Eine Sandbox, die die exe nur STARTET - kein Klick, kein Spielordner - sah genau
        ///    das und sonst nichts: ein Programm, das sofort einen Haufen DLLs in einen
        ///    Temp-Ordner legt und .NET-Registrierungsschluessel liest. Das ist von einem
        ///    Dropper verhaltensmaessig nicht zu unterscheiden. Gemessen am 2026-08-21 auf
        ///    VirusTotal: 2 von rund 70 Motoren schlugen an, beide mit generischen
        ///    ML-Urteilen (Wacatac.C!ml, susgen), und der Verhaltensbericht fuehrte genau
        ///    diese Schreibvorgaenge auf.
        ///
        /// Die Vollstaendigkeitspruefung braucht das Auspacken aber gar nicht: die Namen der
        /// Eintraege stehen im ZIP-Verzeichnis, und das laesst sich IM SPEICHER lesen. Der
        /// fruehe Hinweis auf ein unvollstaendiges Paket bleibt damit erhalten - ohne einen
        /// einzigen Schreibvorgang.
        /// </remarks>
        internal static void Pruefen(Action<string> sag)
        {
            var a = Assembly.GetExecutingAssembly();
            if (Array.IndexOf(a.GetManifestResourceNames(), Name) < 0)
            {
                var neben = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Pfade.BeipackOrdner);
                if (Directory.Exists(neben)) { Installer.Quelle = neben; return; }
                sag("NOTE: this build carries no payload and none was found next to it.");
                return;
            }

            try
            {
                using (var q = a.GetManifestResourceStream(Name))
                using (var zip = new ZipArchive(q, ZipArchiveMode.Read))
                {
                    var namen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    // ⚠ Der Backslash aus seinem ZEICHENCODE, nicht als Escape: dieser Ausdruck
                    //   ist beim Durchreichen durch mehrere Schichten schon einmal zu einem
                    //   einzelnen '\' zerfallen und hat den Bau zerlegt. (char)92 kann das nicht.
                    //   Spec-konforme Archive nutzen ohnehin '/', .NET schreibt aber gern '\'.
                    foreach (var e in zip.Entries) namen.Add(e.FullName.Replace((char)92, '/'));
                    PruefeNamen(namen, sag);
                }
            }
            catch (Exception ex)
            {
                sag("Could not read the bundled files (" + ex.GetType().Name + ": " + ex.Message + ").");
            }
        }

        /// <summary>
        /// Packt aus - NUR wenn wirklich installiert wird. Vorher passiert nichts auf der Platte.
        /// </summary>
        internal static void Bereitstellen(Action<string> sag)
        {
            var a = Assembly.GetExecutingAssembly();
            if (Array.IndexOf(a.GetManifestResourceNames(), Name) < 0)
            {
                // Kein Drama: neben einer entpackten ZIP liegt die Nutzlast als Ordner.
                var neben = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Pfade.BeipackOrdner);
                if (Directory.Exists(neben)) { Installer.Quelle = neben; return; }
                sag("NOTE: this build carries no payload and none was found next to it.");
                return;
            }

            try
            {
                byte[] zip;
                using (var q = a.GetManifestResourceStream(Name))
                using (var m = new MemoryStream())
                { q.CopyTo(m); zip = m.ToArray(); }

                var ziel = Path.Combine(Path.GetTempPath(),
                    "ScriptOne-" + Anwendung.Version + "-payload-" + Kurzhash(zip));

                // Die Marke wird ZULETZT geschrieben. Ein abgebrochenes Auspacken hinterlaesst
                // damit einen Ordner ohne Marke und wird beim naechsten Start wiederholt -
                // sonst gilt eine halbe Nutzlast als fertig.
                var marke = Path.Combine(ziel, ".complete");
                if (!File.Exists(marke))
                {
                    if (Directory.Exists(ziel)) Directory.Delete(ziel, true);
                    Directory.CreateDirectory(ziel);
                    var tmp = Path.Combine(ziel, "_payload.zip");
                    File.WriteAllBytes(tmp, zip);
                    ZipFile.ExtractToDirectory(tmp, ziel);
                    File.Delete(tmp);
                    File.WriteAllText(marke, Anwendung.Version);
                }

                Installer.Quelle = ziel;
                Pruefe(ziel, sag);
            }
            catch (Exception ex)
            {
                sag("Could not unpack the bundled files (" + ex.GetType().Name + ": " + ex.Message + ").");
                sag("Installing will not work until that is fixed - check that %TEMP% is writable.");
            }
        }

        /// <summary>
        /// Meldet eine unvollstaendige Nutzlast SOFORT, nicht erst beim Klick auf Install.
        /// </summary>
        /// <remarks>
        /// Der Nutzer sah bisher erst nach der Installationsentscheidung, dass das Paket den
        /// Plugin-Bau nicht enthaelt. Was fehlt, gehoert an den Anfang - dann ist es ein
        /// bekannter Zustand und keine Ueberraschung mitten im Vorgang.
        /// </remarks>
        /// <summary>
        /// WAS im Beipack liegen muss - die EINZIGE Liste, gegen die geprueft wird.
        /// </summary>
        /// <remarks>
        /// ⚠ EINE LISTE, ZWEI LESER. Geprueft wird an zwei Stellen: beim Start gegen das
        /// ZIP-Verzeichnis im Speicher, beim Installieren gegen den ausgepackten Ordner. Zwei
        /// getrennte Listen wuerden auseinanderlaufen, und die stillere von beiden meldet dann
        /// ein vollstaendiges Paket, das keines ist - genau die Fehlerklasse, die hier schon
        /// zweimal zugeschlagen hat (winhttp.dll unter dem falschen Pfad, und spaeter die zwei
        /// BepInEx-6-Baue, die gar nicht geprueft wurden).
        /// </remarks>
        private static System.Collections.Generic.List<string> Sollpfade()
        {
            var l = new System.Collections.Generic.List<string>();
            foreach (var arch in new[] { "x64", "x86" })
                l.Add(Pfade.PaketOrdner + "/" + Pfade.DoorstopOrdner + "/" + arch + "/" + Pfade.DoorstopDll);
            foreach (var n in new[] { Pfade.PluginIl2Cpp, Pfade.PluginMono })
                l.Add(Pfade.PaketOrdner + "/" + n);
            foreach (var n in Pfade.BepAdapterAlle)
                l.Add(Pfade.PaketOrdner + "/" + Pfade.BepOrdnerImPaket + "/" + n);
            return l;
        }

        /// <summary>Gegen die Eintragsnamen einer ZIP - ohne Schreibvorgang.</summary>
        private static void PruefeNamen(System.Collections.Generic.HashSet<string> namen, Action<string> sag)
        {
            var fehlt = "";
            var wurzel = Pfade.Wurzel + "/";
            var wurzelDa = false;
            foreach (var n in namen) if (n.StartsWith(wurzel, StringComparison.OrdinalIgnoreCase)) { wurzelDa = true; break; }
            if (!wurzelDa) fehlt += " " + Pfade.Wurzel;
            foreach (var soll in Sollpfade())
                if (!namen.Contains(soll)) fehlt += " " + soll;
            if (fehlt.Length > 0)
                sag("WARNING: this package is incomplete - missing:" + fehlt);
        }

        /// <summary>Gegen den ausgepackten Ordner - dieselbe Liste, anderer Traeger.</summary>
        private static void Pruefe(string ziel, Action<string> sag)
        {
            var fehlt = "";
            if (!Directory.Exists(Path.Combine(ziel, Pfade.Wurzel))) fehlt += " " + Pfade.Wurzel;
            foreach (var soll in Sollpfade())
                if (!File.Exists(Path.Combine(ziel, soll.Replace('/', Path.DirectorySeparatorChar))))
                    fehlt += " " + soll;
            if (fehlt.Length > 0)
                sag("WARNING: this package is incomplete - missing:" + fehlt);
        }

        private static string Kurzhash(byte[] d)
        {
            using (var h = SHA256.Create())
            {
                var b = h.ComputeHash(d);
                var s = "";
                for (var i = 0; i < 6; i++) s += b[i].ToString("x2");
                return s;
            }
        }
    }
}
