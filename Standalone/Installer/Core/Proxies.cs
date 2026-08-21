using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;

namespace ScriptOne.Setup
{
    /// <summary>
    /// Erzeugt die Il2Cpp-Proxy-Assemblies aus den Spieldateien des NUTZERS - beim Installieren,
    /// nicht erst beim ersten Spielstart.
    ///
    /// WARUM ES DAS GIBT
    /// Der Standalone-Weg braucht auf einem Il2Cpp-Spiel verwaltete Sichten auf die kompilierte
    /// Spiel-API. Ausliefern kann man sie nicht: sie entstehen aus der global-metadata.dat und
    /// der GameAssembly.dll GENAU DIESES Spielstandes. Bisher druckte der Installer dazu nur
    /// einen Hinweis - die Installation lief durch, das Spiel startete, und der Wirt starb sofort
    /// mit "no Il2Cpp interop assemblies found". Gemessen 2026-08-20 in zwei Spielen; die
    /// Projektdoku versprach an derselben Stelle woertlich "the installer generates them during
    /// setup".
    ///
    /// ⚠ EIN TEIL LAESST SICH NICHT AUS DEM SPIEL ABLEITEN: die Unity-BASISbibliotheken
    /// (UnityEngine.CoreModule und Geschwister) muessen VERSIONSGENAU zur Unity-Fassung des
    /// Spiels passen, und sie stecken in einem Il2Cpp-Spiel nirgends in verwalteter Form. Sie
    /// kommen deshalb aus dem NuGet-Feed nuget.bepinex.dev, der 1546 Fassungen fuehrt (gemessen
    /// 2026-08-20). Mitliefern ist ausgeschlossen - eine Fassung wiegt 3,2 MB gepackt.
    /// Das ist die EINZIGE Stelle, an der ScriptOne etwas aus dem Netz holt, sie betrifft nur
    /// Il2Cpp-Spiele, und sie muss nur EINMAL je Spiel und Spielstand laufen.
    /// </summary>
    internal static class Proxies
    {
        /// <summary>Der Feed, aus dem die versionsgenauen Unity-Basisbibliotheken kommen.</summary>
        private const string Feed = "https://nuget.bepinex.dev/v3/package/unityengine.modules/";

        /// <summary>
        /// Die Unity-Fassung des Spiels, dreistellig (z. B. "2021.3.45").
        /// ⚠ Quelle ist die PRODUKTVERSION von UnityPlayer.dll, nicht globalgamemanagers: von
        /// vier gemessenen Spielen fuehren nur zwei ein globalgamemanagers, die anderen packen
        /// alles in data.unity3d. UnityPlayer.dll lieferte in allen vier die richtige Fassung.
        /// </summary>
        internal static string UnityFassung(string spiel)
        {
            try
            {
                var up = Path.Combine(spiel, "UnityPlayer.dll");
                if (!File.Exists(up)) return null;
                var roh = FileVersionInfo.GetVersionInfo(up).ProductVersion;
                if (string.IsNullOrEmpty(roh)) return null;
                // "2021.3.45f2 (88f88f591b2e)" -> "2021.3.45"
                var m = Regex.Match(roh, @"(\d+)\.(\d+)\.(\d+)");
                return m.Success ? m.Groups[1].Value + "." + m.Groups[2].Value + "." + m.Groups[3].Value : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Holt die Unity-Basisbibliotheken der Fassung und legt sie unter
        /// <c>&lt;werkzeug&gt;\unitylibs\&lt;fassung&gt;\</c> ab - genau dort, wo der Erzeuger sie sucht.
        /// Gibt <c>true</c> zurueck, wenn sie danach da sind (auch wenn sie es schon vorher waren).
        /// </summary>
        internal static bool HoleUnityLibs(string werkzeug, string fassung, Ergebnis e)
        {
            var ziel = Path.Combine(Path.Combine(werkzeug, "unitylibs"), fassung);
            if (Directory.Exists(ziel) && Directory.GetFiles(ziel, "*.dll").Length > 0)
            {
                e.Sag("unity base libraries " + fassung + ": already here, not downloaded again.");
                return true;
            }

            var tmp = Path.Combine(Path.GetTempPath(),
                                   "scriptone-unitylibs-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".nupkg");
            try
            {
                // ⚠ .NET Framework 4.7.2 waehlt von sich aus KEIN TLS 1.2. Ohne diese Zeile
                //   scheitert der Download an einem modernen Feed mit einer Meldung ueber eine
                //   "geschlossene Verbindung" - was wie ein Netzproblem des Nutzers aussieht
                //   und keines ist.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                var url = Feed + fassung + "/unityengine.modules." + fassung + ".nupkg";
                e.Sag("downloading unity base libraries for Unity " + fassung + " (about 3 MB) ...");
                using (var wc = new WebClient()) wc.DownloadFile(url, tmp);

                Directory.CreateDirectory(ziel);
                var n = 0;
                using (var zip = ZipFile.OpenRead(tmp))
                    foreach (var eintrag in zip.Entries)
                    {
                        if (!eintrag.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                        // Flach ablegen - der Erzeuger erwartet die DLLs direkt im Ordner.
                        eintrag.ExtractToFile(Path.Combine(ziel, eintrag.Name), true);
                        n++;
                    }

                if (n == 0)
                {
                    e.Sag("the downloaded package contained no assemblies - this is not usable.");
                    return false;
                }
                e.Sag("unity base libraries: " + n + " assemblies for Unity " + fassung + ".");
                return true;
            }
            catch (Exception ex)
            {
                // ⚠ KEIN Abbruch hier. Ob das den Lauf beendet, entscheidet der Aufrufer - beim
                //   PLUGIN-Weg sind die Proxies bloss Vorsorge und ihr Fehlen darf die
                //   Installation nicht kippen.
                e.Sag("could not fetch the unity base libraries: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        /// <summary>
        /// Faehrt den Erzeuger. <paramref name="werkzeug"/> ist der Ordner mit InteropGen.exe.
        /// </summary>
        internal static bool Erzeuge(string werkzeug, string spiel, string ausgabe, Ergebnis e)
        {
            var exe = Path.Combine(werkzeug, "InteropGen.exe");
            if (!File.Exists(exe)) { e.Sag("the proxy generator is not in this package (" + exe + ")."); return false; }
            try
            {
                var p = new ProcessStartInfo(exe)
                {
                    Arguments = "--game \"" + spiel.TrimEnd('\\') + "\" --out \"" + ausgabe.TrimEnd('\\') + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                e.Sag("generating the Il2Cpp proxy assemblies from YOUR game files - this takes a moment ...");
                using (var lauf = Process.Start(p))
                {
                    var aus = lauf.StandardOutput.ReadToEnd();
                    var err = lauf.StandardError.ReadToEnd();
                    lauf.WaitForExit();
                    if (lauf.ExitCode != 0)
                    {
                        // Die letzte sprechende Zeile des Werkzeugs weitergeben - eine blosse
                        // Exitcode-Meldung schickt den Nutzer ins Leere.
                        var letzte = LetzteZeile(aus) ?? LetzteZeile(err);
                        e.Sag("the generator failed (exit " + lauf.ExitCode + ")"
                              + (letzte == null ? "." : ": " + letzte));
                        return false;
                    }
                }
                // ⚠ EXITCODE 0 IST HIER ZU SCHWACH. Gemessen werden die Pflichtdateien, denn ein
                //   leerer Ordner mit Exitcode 0 sieht wie ein Erfolg aus und faellt erst beim
                //   Spielstart auf.
                var a = Path.Combine(ausgabe, "Assembly-CSharp.dll");
                var m = Path.Combine(ausgabe, "Il2Cppmscorlib.dll");
                if (!File.Exists(a) || !File.Exists(m))
                {
                    e.Sag("the generator reported success but did not write Assembly-CSharp.dll and Il2Cppmscorlib.dll.");
                    return false;
                }
                e.Sag("proxy assemblies generated: " + Directory.GetFiles(ausgabe, "*.dll").Length + " files.");
                return true;
            }
            catch (Exception ex)
            {
                e.Sag("could not run the generator: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static string LetzteZeile(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var zeilen = s.Replace("\r", "").Split('\n');
            for (var i = zeilen.Length - 1; i >= 0; i--)
                if (zeilen[i].Trim().Length > 0) return zeilen[i].Trim();
            return null;
        }
    }
}
