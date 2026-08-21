using System;
using System.IO;
using ScriptOne.Setup;

namespace ScriptOne.Host
{
    /// <summary>
    /// Erkennt eine STANDALONE-Installation, die es zwar noch als Dateien gibt, die aber nie
    /// wieder geladen wird - weil ein Lader (BepInEx, MelonLoader) den Einstiegspunkt
    /// uebernommen hat.
    ///
    /// WIE DIESER ZUSTAND ENTSTEHT
    /// Der Standalone-Weg haengt an UnityDoorstops <c>winhttp.dll</c> plus
    /// <c>doorstop_config.ini</c>. BepInEx benutzt DIESELBEN zwei Dateien - wer BepInEx nach
    /// ScriptOne installiert, ueberschreibt beide, und zwar ohne Nachfrage. Danach liegt der
    /// ganze Standalone-Wirt vollstaendig da und wird nie wieder angefasst. Genau dafuer legt
    /// der Installer die Plugin-Kopie vorsorglich mit: ScriptOne laeuft weiter, aber eben als
    /// Plugin des neuen Laders. Der Standalone-Teil ist ab da tote Fracht.
    ///
    /// ⚠ WARUM DER TEST AN DEN DATEIEN HAENGT UND NICHT AM ZEITPUNKT
    /// Naheliegend waere: "wenn das Plugin den Anspruch bekommt (siehe <see cref="HostGuard"/>),
    /// dann lief der Standalone-Wirt nicht, also ist er verwaist." Das ist FALSCH, und zwar
    /// nachweislich - der Mono-Standalone-Zweig haengt seinen Wirt erst an ein Unity-Objekt,
    /// wenn die Laufzeit steht, also unter Umstaenden NACH dem Plugin. Der Anspruch waere dann
    /// noch frei und ein lebendiger Wirt wuerde als verwaist gemeldet.
    /// Die Dateien luegen nicht: zeigt <c>doorstop_config.ini</c> nicht mehr auf unseren
    /// Preloader, kann der nie wieder starten - unabhaengig davon, wer wann laeuft.
    ///
    /// ⚠ UND WARUM ES NUR UNTER debug=true GEMELDET WIRD
    /// Es ist kein Fehler. Nichts ist kaputt, nichts fehlt, die Skripte laufen. Eine Warnung
    /// dafuer waere eine Warnung ohne Handlung - der Nutzer koennte nur nicken. Wer an
    /// ScriptOne selbst arbeitet, will es dagegen wissen.
    /// </summary>
    internal static class VerwaisterWirt
    {
        /// <summary>
        /// Der uebliche Weg: eine Zeile fuer BEIDE Ausgaenge.
        /// ⚠ Ohne die zweite Zeile ist "debug=true, aber nichts zu melden" von "debug wurde gar
        /// nicht gelesen" nicht zu unterscheiden - beides sieht im Protokoll gleich aus. Ein
        /// Schalter ohne beobachtbaren Ein-Zustand ist nicht pruefbar, und genau das fiel beim
        /// ersten Lauf in Cocaine Dealer auf.
        /// </summary>
        internal static void Melde(string zustandOrdner, IScriptLog log)
        {
            if (log == null) return;
            var b = Bericht(zustandOrdner);
            log.Info(b ?? "debug: on - no orphaned standalone installation found next to this game.");
        }

        /// <summary>
        /// Gibt den Berichtstext zurueck - oder <c>null</c>, wenn es nichts zu berichten gibt.
        /// </summary>
        /// <param name="zustandOrdner">
        /// <c>&lt;spiel&gt;\ScriptOne\state</c>. Daraus ergeben sich beide Ebenen, die geprueft
        /// werden - der ScriptOne-Ordner und der Spielordner darueber.
        /// </param>
        internal static string Bericht(string zustandOrdner)
        {
            try
            {
                var wurzel = Ebene(zustandOrdner);          // <spiel>\ScriptOne
                if (wurzel == null) return null;
                var spiel = Ebene(wurzel);                  // <spiel>
                if (spiel == null) return null;

                var core = Path.Combine(wurzel, Pfade.CoreRuntime);
                if (!Directory.Exists(core)) return null;   // gar keine Standalone-Installation

                // Liegt ueberhaupt ein Preloader darin? Der Ordner allein koennte ein Rest sein.
                var gefunden = false;
                foreach (var tfm in Directory.GetDirectories(core))
                    if (File.Exists(Path.Combine(tfm, Pfade.PreloaderDll))) { gefunden = true; break; }
                if (!gefunden) return null;

                var ini = Path.Combine(spiel, Pfade.DoorstopCfg);
                if (!File.Exists(ini))
                    return Text(core, "there is no " + Pfade.DoorstopCfg + " next to the game any more");

                // Zeigt das Ziel noch auf uns? Der Wert ist relativ zum Spielordner und traegt
                // in jedem Fall unseren Wurzelordner als erstes Element.
                var marker = Pfade.Wurzel + Pfade.T + Pfade.CoreRuntime;
                foreach (var roh in File.ReadAllLines(ini))
                {
                    var z = roh.Trim();
                    if (z.Length == 0 || z[0] == '#' || z[0] == ';' || z[0] == '[') continue;
                    var i = z.IndexOf('=');
                    if (i <= 0) continue;
                    if (z.Substring(0, i).Trim().ToLowerInvariant() != "target_assembly") continue;
                    var ziel = z.Substring(i + 1).Trim()
                                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                    if (ziel.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return null;
                    return Text(core, Pfade.DoorstopCfg + " now points at \"" + ziel + "\"");
                }

                return Text(core, Pfade.DoorstopCfg + " has no target_assembly line any more");
            }
            catch
            {
                // Ein Diagnosehinweis, der den Start gefaehrdet, ist seinen Preis nicht wert.
                return null;
            }
        }

        private static string Text(string core, string grund)
        {
            return "debug: a standalone ScriptOne installation is still present at \"" + core +
                   "\", but it can no longer start - " + grund + ". A loader has taken over the" +
                   " entry point, and ScriptOne is running as its plugin instead (which is what" +
                   " the stand-by copy is for). Nothing is broken; the files are just dead" +
                   " weight. Run the installer once and choose remove to get rid of them.";
        }

        /// <summary>Eine Ebene nach oben - ohne dass ein abschliessender Trenner das verhindert.</summary>
        private static string Ebene(string pfad)
        {
            if (string.IsNullOrEmpty(pfad)) return null;
            var g = pfad.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var oben = Path.GetDirectoryName(g);
            return string.IsNullOrEmpty(oben) ? null : oben;
        }
    }
}
