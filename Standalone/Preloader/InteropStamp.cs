using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ScriptOne.Preloader
{
    /// <summary>
    /// Der Stempel neben den Proxy-Assemblies: gegen welchen Spielstand wurden sie erzeugt?
    /// </summary>
    /// <remarks>
    /// DAS PROBLEM, DAS ER LOEST - und warum es ohne ihn unsichtbar ist:
    ///
    /// Nach einem Spiel-Update passen die Proxy-Assemblies nicht mehr. Man sieht das
    /// NIRGENDS von selbst:
    ///   - Der Bau bleibt gruen. Er uebersetzt gegen genau denselben Ordner, der auch
    ///     laeuft; ein veralteter Satz ist mit sich selbst widerspruchsfrei.
    ///   - Es fliegt keine Ausnahme. Methoden loesen im Proxy ueber einen einbetonierten
    ///     Metadaten-TOKEN auf. Ist der tot, kommt eine Attrappen-MethodInfo ohne
    ///     Funktionszeiger zurueck - kein Fehler, nur Wirkungslosigkeit.
    ///   - Klassen und Felder loesen dagegen ueber NAMEN auf und ueberleben oefter. Das
    ///     macht es schlimmer: der Mod laeuft halb weiter und wirkt „fast richtig".
    ///
    /// Deshalb hier ein eigener Vergleich VOR dem Start, nach demselben Prinzip, das
    /// MelonLoaders Generator benutzt: ein Hash ueber die ganze GameAssembly.dll.
    ///
    /// ⚠ ABSICHTLICH NUR MELDEN, NIE REPARIEREN. Erzeugen dauert Minuten und schreibt
    /// 70 MB - das gehoert nicht in einen Spielstart, den der Nutzer gerade angeklickt hat.
    /// Der Wirt sagt Bescheid, das Werkzeug macht es (tools\Update-Interop.ps1).
    ///
    /// Der Hash ist SHA256 statt SHA512: gleiche Aussage, halbe Rechenzeit auf 66 MB, und
    /// wir vergleichen ohnehin nur gegen unseren eigenen Stempel.
    /// </remarks>
    internal static class InteropStamp
    {
        internal const string Dateiname = ".interop-stamp";

        internal sealed class Stand
        {
            internal string GameAssemblyHash;
            internal long   GameAssemblyGroesse;
            internal string Erzeugt;
            internal string Werkzeug;
        }

        /// <summary>Hash der GameAssembly.dll. Null, wenn es sie nicht gibt (Mono-Zweig).</summary>
        internal static string HashGameAssembly(string spielOrdner)
        {
            var pfad = Path.Combine(spielOrdner, "GameAssembly.dll");
            if (!File.Exists(pfad)) return null;
            using (var s = File.OpenRead(pfad))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        internal static Stand Lies(string interopOrdner)
        {
            var pfad = Path.Combine(interopOrdner, Dateiname);
            if (!File.Exists(pfad)) return null;
            var st = new Stand();
            foreach (var roh in File.ReadAllLines(pfad))
            {
                var i = roh.IndexOf('=');
                if (i <= 0) continue;
                var k = roh.Substring(0, i).Trim();
                var v = roh.Substring(i + 1).Trim();
                if (k == "game_assembly_sha256") st.GameAssemblyHash = v;
                else if (k == "game_assembly_size") { long n; if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) st.GameAssemblyGroesse = n; }
                else if (k == "generated") st.Erzeugt = v;
                else if (k == "tool") st.Werkzeug = v;
            }
            return st;
        }

        internal static void Schreibe(string interopOrdner, string hash, long groesse, string werkzeug)
        {
            var text = new StringBuilder()
                .AppendLine("# ScriptOne - which game build these proxy assemblies were generated from.")
                .AppendLine("# Do not edit. Delete to force the next check to report 'unknown'.")
                .AppendLine("game_assembly_sha256 = " + hash)
                .AppendLine("game_assembly_size = " + groesse.ToString(CultureInfo.InvariantCulture))
                .AppendLine("generated = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .AppendLine("tool = " + werkzeug)
                .ToString();
            File.WriteAllText(Path.Combine(interopOrdner, Dateiname), text, new UTF8Encoding(false));
        }

        /// <summary>
        /// Prueft und MELDET. Gibt true zurueck, wenn der Satz nachweislich passt.
        /// </summary>
        internal static bool Pruefe(string spielOrdner, string interopOrdner, FileLog log)
        {
            try
            {
                var jetzt = HashGameAssembly(spielOrdner);
                if (jetzt == null)
                {
                    log.Info("interop check: no GameAssembly.dll - not an Il2Cpp build, skipping");
                    return true;
                }

                var stand = Lies(interopOrdner);
                if (stand == null || string.IsNullOrEmpty(stand.GameAssemblyHash))
                {
                    // Kein Stempel heisst NICHT "in Ordnung" - es heisst "unbekannt", und das
                    // gehoert gesagt. Ein stiller Durchlauf waere hier dieselbe Luege wie ein
                    // gruener Exitcode nach einer uebersprungenen Pruefung.
                    log.Warn("interop check: no " + Dateiname + " next to the proxy assemblies -");
                    log.Warn("  cannot tell whether they match this game build. Run tools\\Update-Interop.ps1 -Stamp");
                    log.Warn("  once to record the current state, or -Force to regenerate them.");
                    return false;
                }

                if (string.Equals(stand.GameAssemblyHash, jetzt, StringComparison.OrdinalIgnoreCase))
                {
                    log.Info("interop check: proxy assemblies match this game build (generated " + (stand.Erzeugt ?? "?") + ")");
                    return true;
                }

                log.Error("interop check: THE GAME HAS CHANGED SINCE THE PROXY ASSEMBLIES WERE GENERATED.");
                log.Error("  proxies built for: " + stand.GameAssemblyHash.Substring(0, 16) + "...  (" + (stand.Erzeugt ?? "?") + ")");
                log.Error("  game is now      : " + jetzt.Substring(0, 16) + "...");
                log.Error("  Expect methods to silently do nothing. This does NOT throw and the build stays green.");
                log.Error("  Fix: run tools\\Update-Interop.ps1 -Force");
                return false;
            }
            catch (Exception ex)
            {
                log.Warn("interop check failed to run: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }
    }
}
