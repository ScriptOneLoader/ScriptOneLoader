using System.Text.RegularExpressions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Cpp2IL.Core;
using Cpp2IL.Core.OutputFormats;
using Il2CppInterop.Common;
using Il2CppInterop.Generator;
using Il2CppInterop.Generator.Runners;
using Microsoft.Extensions.Logging;

namespace ScriptOne.InteropGen
{
    /// <summary>
    /// Erzeugt die Il2Cpp-Proxy-Assemblies aus den Dateien des installierten Spiels.
    /// </summary>
    public static class Program
    {
        private const string StempelDatei = ".interop-stamp";

        /// <summary>Der Ordner mit den Unity-Basisbibliotheken fuer genau diese Spielfassung.</summary>
        /// <remarks>
        /// Erst exakt (2022.3.62f1), dann auf drei Stellen (2022.3.62). Weiter wird NICHT
        /// gelockert: die Basisbibliotheken sind versionsgenau, und ein Versatz erzeugt Proxies,
        /// die uebersetzen und zur Laufzeit nicht passen - der teuerste aller Fehlschlaege, weil
        /// er erst im Spiel auffaellt.
        /// </remarks>
        private static string WaehleUnityLibs(string wurzel, string spielFassung)
        {
            if (!Directory.Exists(wurzel)) return null;
            var genau = Path.Combine(wurzel, spielFassung);
            if (Directory.Exists(genau)) return genau;
            // ⚠ HIER STAND string.Join(".", spielFassung.Split('.').Take(3)) - und das ist KEINE
            //   Dreiteilung: "2021.3.45f2" zerfaellt in "2021" / "3" / "45f2", die ersten drei
            //   Teile ergeben also wieder "2021.3.45f2". Der Rueckfall verglich damit die
            //   Spielfassung mit sich selbst und hat NIE gegriffen; er sah nur so aus, weil im
            //   Autorenbetrieb der eingestempelte Ordner danach uebernahm. Aufgefallen erst,
            //   als der Installer die Bibliotheken selbst holte und sie als "2021.3.45"
            //   ablegte - der Erzeuger fand sie nicht (2026-08-20, Shadows of Doubt).
            var drei = Dreiteilig(spielFassung);
            foreach (var d in Directory.GetDirectories(wurzel))
                if (Dreiteilig(Path.GetFileName(d)).Equals(drei, StringComparison.OrdinalIgnoreCase))
                    return d;
            return null;
        }

        /// <summary>
        /// "2021.3.45f2" und "2021.3.45" ergeben beide "2021.3.45" - der Suffix der Unity-Fassung
        /// (f1/f2/b3/p4) gehoert nicht zur Paketfassung, unter der die Bibliotheken liegen.
        /// </summary>
        private static string Dreiteilig(string fassung)
        {
            if (string.IsNullOrEmpty(fassung)) return "";
            var m = Regex.Match(fassung, @"^(\d+)\.(\d+)\.(\d+)");
            return m.Success ? m.Groups[1].Value + "." + m.Groups[2].Value + "." + m.Groups[3].Value
                             : fassung;
        }

        public static int Main(string[] args)
        {
            var spiel = Arg(args, "--game");
            var ziel  = Arg(args, "--out");
            var nurPruefen = args.Contains("--check");
            var nurStempeln = args.Contains("--stamp");
            var erzwingen = args.Contains("--force");

            if (spiel == null || (ziel == null && !nurPruefen))
            {
                Console.WriteLine("InteropGen - generates Il2Cpp proxy assemblies for ScriptOne");
                Console.WriteLine();
                Console.WriteLine("  --game <dir>   game folder (contains GameAssembly.dll)");
                Console.WriteLine("  --out  <dir>   output folder, normally <game>\\ScriptOne\\interopgenerator");
                Console.WriteLine("  --check        only report whether the existing set is current");
                Console.WriteLine("  --stamp        only record the current game build for an existing set");
                Console.WriteLine("  --force        regenerate even if the stamp says it is current");
                return 2;
            }

            try
            {
                var gameAssembly = Path.Combine(spiel, "GameAssembly.dll");
                if (!File.Exists(gameAssembly))
                {
                    Console.WriteLine("  no GameAssembly.dll in " + spiel + " - this is not an Il2Cpp build.");
                    Console.WriteLine("  Nothing to generate: on Mono the game's own assemblies are the reference.");
                    return 0;
                }

                var hash = Sha256(gameAssembly);
                var groesse = new FileInfo(gameAssembly).Length;
                Console.WriteLine("  game assembly : " + groesse.ToString("N0") + " B, sha256 " + hash.Substring(0, 16) + "...");

                if (nurPruefen)  return Pruefe(ziel ?? Path.Combine(spiel, "ScriptOne", "interopgenerator"), hash) ? 0 : 1;
                if (nurStempeln) { Stempel(ziel, hash, groesse); Console.WriteLine("  stamp written."); return 0; }

                if (!erzwingen && Pruefe(ziel, hash))
                {
                    Console.WriteLine("  already current - nothing to do (use --force to regenerate anyway).");
                    return 0;
                }

                return Erzeuge(spiel, ziel, gameAssembly, hash, groesse);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  FAILED: " + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        private static int Erzeuge(string spiel, string ziel, string gameAssembly, string hash, long groesse)
        {
            var metadata = Path.Combine(spiel, "Schedule I_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
            if (!File.Exists(metadata))
            {
                // Nicht raten: den *_Data-Ordner suchen statt einen Spielnamen zu verdrahten.
                var daten = Directory.GetDirectories(spiel, "*_Data").FirstOrDefault();
                if (daten != null)
                    metadata = Path.Combine(daten, "il2cpp_data", "Metadata", "global-metadata.dat");
            }
            if (!File.Exists(metadata))
            {
                Console.WriteLine("  global-metadata.dat not found under <game>\\*_Data\\il2cpp_data\\Metadata\\");
                return 1;
            }

            var unityPaket = Metadaten("UnityModulesVersion");

            // ⚠ DER EINGESTEMPELTE PFAD IST NUR NOCH DER RUECKFALL. Er zeigt in den NuGet-Cache
            //   der BAUMASCHINE, und beim Nutzer existiert der NIE - fuer ein Werkzeug, das nur
            //   der Autor startet, war das richtig; sobald es ausgeliefert wird, macht genau das
            //   es unbrauchbar. Zuerst wird deshalb NEBEN der eigenen exe gesucht:
            //       <exe-Ordner>/unitylibs/<Unity-Fassung des Spiels>
            //   Damit bedient dieselbe exe mehrere Unity-Fassungen, ohne neu gebaut zu werden -
            //   der Ordnername ist die Zuordnung.
            var eigener = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
            var libWurzel = Path.Combine(eigener, "unitylibs");

            Console.WriteLine("  metadata      : " + metadata);

            // ---- Unity-Fassung des SPIELS bestimmen und gegen das Paket pruefen ----
            Cpp2IlApi.Init();
            var unityPlayer = Path.Combine(spiel, "UnityPlayer.dll");
            var datenOrdner = Directory.GetDirectories(spiel, "*_Data").FirstOrDefault();
            var unityVersion = Cpp2IlApi.DetermineUnityVersion(
                File.Exists(unityPlayer) ? unityPlayer : null, datenOrdner);
            Console.WriteLine("  unity version : " + unityVersion);

            var spielFassung = unityVersion.ToString();

            // ⚠ Ein Versatz erzeugt Proxies, die uebersetzen und zur Laufzeit NICHT passen -
            //   also wird die passende Fassung GEWAEHLT statt gehofft. Reihenfolge: exakter
            //   Ordner neben der exe, dann der auf drei Stellen passende, dann der
            //   eingestempelte Rueckfall (nur fuer den Autorenbetrieb).
            var unityLibs = WaehleUnityLibs(libWurzel, spielFassung);
            if (unityLibs == null)
            {
                var gestempelt = Metadaten("UnityBaseLibs");
                if (gestempelt != null && Directory.Exists(gestempelt)
                    && (unityPaket == null || spielFassung.StartsWith(unityPaket, StringComparison.Ordinal)))
                    unityLibs = gestempelt;
            }
            if (unityLibs == null)
            {
                Console.WriteLine();
                Console.WriteLine("  ABORT: no Unity base libraries for this game.");
                Console.WriteLine("  The game runs Unity " + spielFassung + ".");
                Console.WriteLine("  Expected them next to this program in:");
                Console.WriteLine("    " + Path.Combine(libWurzel, spielFassung));
                Console.WriteLine("  or in a folder for the same three-part version. They come from");
                Console.WriteLine("  the UnityEngine.Modules package, which is version-exact.");
                return 1;
            }
            Console.WriteLine("  unity libs    : " + unityLibs);

            var uhr = Stopwatch.StartNew();
            Console.WriteLine();
            Console.WriteLine("  [1/3] Cpp2IL: reading il2cpp metadata ...");
            Cpp2IlApi.InitializeLibCpp2Il(gameAssembly, metadata, unityVersion);

            Console.WriteLine("  [2/3] Cpp2IL: building stub assemblies ...");
            var quellen = new AsmResolverDllOutputFormatDefault().BuildAssemblies(Cpp2IlApi.CurrentAppContext);
            Console.WriteLine("        " + quellen.Count + " assemblies");

            Console.WriteLine("  [3/3] Il2CppInterop: generating proxies ...");
            if (Directory.Exists(ziel))
            {
                // Wie MelonLoader: den alten Satz WEGRAEUMEN, nicht danebenlegen. Ein
                // gemischter Ordner ist die schlimmste Variante - er laeuft halb.
                foreach (var f in Directory.GetFiles(ziel, "*.dll")) File.Delete(f);
            }
            else Directory.CreateDirectory(ziel);

            using (var gen = Il2CppInteropGenerator.Create(new GeneratorOptions
            {
                Source = quellen,
                OutputDir = ziel,
                UnityBaseLibsDir = unityLibs,
                GameAssemblyPath = gameAssembly,
                Parallel = true,

                // ⚠ OHNE DIESE ZEILE HEISSEN DIE ERZEUGNISSE FALSCH.
                // Die Vorgabe ist OptIn: dann bekommen NUR die Namen aus
                // NamespacesAndAssembliesToPrefix (System, mscorlib, Microsoft, Mono, I18N)
                // das Il2Cpp-Praefix - alles andere bleibt roh. Heraus kommt dann
                // 'ScheduleOne.Core.dll' statt 'Il2CppScheduleOne.Core.dll', und jeder Mod,
                // der gegen den ueblichen Satz geschrieben ist, bricht mit CS0246 (gemessen:
                // 354 Fehler). MelonLoader und BepInEx erzeugen mit OptOut - Praefix fuer
                // ALLES ausser der Ausnahmeliste (Assembly-CSharp, Unity).
                Il2CppPrefixMode = GeneratorOptions.PrefixMode.OptOut,
            }).AddLogger(new KonsolenLog()).AddInteropAssemblyGenerator())
            {
                gen.Run();
            }

            var anzahl = Directory.GetFiles(ziel, "*.dll").Length;
            Console.WriteLine();
            if (anzahl == 0)
            {
                Console.WriteLine("  FAILED: no assemblies were written to " + ziel);
                return 1;
            }

            Stempel(ziel, hash, groesse);
            Console.WriteLine("  done: " + anzahl + " assemblies in " + uhr.Elapsed.TotalSeconds.ToString("F1") + " s");
            Console.WriteLine("  " + ziel);
            return 0;
        }

        private static bool Pruefe(string ziel, string hash)
        {
            var pfad = Path.Combine(ziel ?? ".", StempelDatei);
            if (!File.Exists(pfad)) { Console.WriteLine("  no stamp -> state unknown"); return false; }
            var alt = File.ReadAllLines(pfad)
                          .Select(z => z.Split('='))
                          .Where(t => t.Length == 2 && t[0].Trim() == "game_assembly_sha256")
                          .Select(t => t[1].Trim()).FirstOrDefault();
            var gleich = string.Equals(alt, hash, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine(gleich ? "  stamp matches the installed game" : "  STAMP DOES NOT MATCH - the game changed");
            return gleich;
        }

        private static void Stempel(string ziel, string hash, long groesse)
        {
            var werkzeug = "InteropGen " + (Assembly.GetExecutingAssembly().GetName().Version ?? new Version()) +
                           " (Cpp2IL 2022.1.0-development.1452, Il2CppInterop.Generator 1.5.1-ci.845)";
            var text = new StringBuilder()
                .AppendLine("# ScriptOne - which game build these proxy assemblies were generated from.")
                .AppendLine("# Do not edit. Delete to force the next check to report 'unknown'.")
                .AppendLine("game_assembly_sha256 = " + hash)
                .AppendLine("game_assembly_size = " + groesse)
                .AppendLine("generated = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .AppendLine("tool = " + werkzeug)
                .ToString();
            File.WriteAllText(Path.Combine(ziel, StempelDatei), text, new UTF8Encoding(false));
        }

        private static string Sha256(string pfad)
        {
            using (var s = File.OpenRead(pfad))
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        private static string Arg(string[] a, string name)
        {
            var i = Array.IndexOf(a, name);
            return (i >= 0 && i + 1 < a.Length) ? a[i + 1] : null;
        }

        /// <summary>Der eingestempelte Pfad aus der csproj - nicht geraten.</summary>
        private static string Metadaten(string schluessel)
        {
            return Assembly.GetExecutingAssembly()
                           .GetCustomAttributes<AssemblyMetadataAttribute>()
                           .Where(x => x.Key == schluessel)
                           .Select(x => x.Value)
                           .FirstOrDefault();
        }

        private sealed class KonsolenLog : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel l) => l >= LogLevel.Information;
            public void Log<TState>(LogLevel l, EventId id, TState state, Exception ex,
                                    Func<TState, Exception, string> f)
            {
                if (!IsEnabled(l)) return;
                Console.WriteLine("        " + (f != null ? f(state, ex) : Convert.ToString(state)));
            }
        }
    }
}
