using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Common;   // AddLogger - Erweiterungsmethode auf BaseHost
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Startup;
using MonoMod.RuntimeDetour;
using ScriptOne.Host;

namespace ScriptOne.Preloader
{
    /// <summary>
    /// Faehrt Il2CppInterop hoch und bestimmt den Zeitpunkt, an dem der Lua-Wirt
    /// starten darf.
    /// </summary>
    /// <remarks>
    /// DAS ZEITPUNKTPROBLEM UND SEINE LOESUNG
    /// Wenn Doorstop uns ruft, laeuft CoreCLR, aber Unity nicht. Ein Aufruf ins Spiel
    /// waere zu diesem Zeitpunkt ein Absturz. Man koennte von einem Nebenfaden aus
    /// pollen, bis die Domaene existiert - aber dann liefe der Wirt auf dem FALSCHEN
    /// FADEN, und Unity-Aufrufe gehoeren auf den Hauptfaden.
    ///
    /// Der Weg, den BepInEx geht und der beides auf einmal loest: einen Detour auf
    /// den Export 'il2cpp_runtime_invoke' von GameAssembly.dll legen und auf den
    /// Methodennamen 'Internal_ActiveSceneChanged' warten. Wenn der durchlaeuft,
    /// steht die Laufzeit nachweislich UND wir sind auf dem Hauptfaden. Danach
    /// haengt sich der Detour sofort wieder aus - er kostet also nur waehrend des
    /// Ladens etwas.
    ///
    /// ⚠ KORREKTUR (2026-08-18): hier stand, das sei "ein ANDERER Mechanismus als
    /// MelonLoaders GetProcAddress-Hook" und "die beiden kaemen sich nicht einmal in die
    /// Quere". Der erste Halbsatz stimmt und der zweite ist trotzdem falsch, weil er die
    /// falsche Ebene betrachtet.
    ///
    /// Richtig ist: DIESER Detour hier - ein managed Hook auf den Export
    /// il2cpp_runtime_invoke - kollidiert tatsaechlich mit nichts. Aber was ScriptOne
    /// ueberhaupt erst startet, ist UnityDoorstop, und Doorstop benutzt GENAU MelonLoaders
    /// Mechanismus: eine Proxy-DLL plus Ersetzen des Importeintrags kernel32!GetProcAddress
    /// in der Importtabelle von UnityPlayer.dll. Verschiedene Dateinamen (version.dll gegen
    /// winhttp.dll) helfen dabei nicht - beide greifen auf denselben IAT-Slot zu, und KEINER
    /// von beiden verkettet. Nebeneinander ueberlebt also nur einer.
    /// Statisch belegt (Disassembly von winhttp.dll 4.5.0 + MelonLoader-Quelltext
    /// ModuleSymbolRedirect.cs/PltHook.cs); ein LAUF mit beiden steht aus.
    /// </remarks>
    internal static class Il2CppBootstrap
    {
        private delegate IntPtr RuntimeInvokeFn(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc);
        // MonoMod 25 reicht das ORIGINAL als ersten Parameter durch - damit entfaellt jede
        // eigene Trampolin-Verwaltung, und der Aufruf des Originals kann nicht schiefgehen.
        private delegate IntPtr RuntimeInvokeHook(RuntimeInvokeFn orig, IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc);

        private static NativeHook _hook;
        private static RuntimeInvokeHook _ersatz;   // Halten! Sonst raeumt der GC das Delegat weg.
        private static FileLog _log;
        private static string _spielOrdner;
        private static bool _gestartet;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Start(string spielOrdner, string interopOrdner, FileLog log, HostConfig cfg)
        {
            _log = log;
            _spielOrdner = spielOrdner;

            // P/Invoke von Il2CppInterop auf "GameAssembly" auf die echte Datei lenken.
            NativeLibrary.SetDllImportResolver(typeof(IL2CPP).Assembly, (name, asm, pfad) =>
                name == "GameAssembly"
                    ? NativeLibrary.Load(Path.Combine(spielOrdner, "GameAssembly.dll"), asm, pfad)
                    : IntPtr.Zero);

            var unity = LiesUnityVersion(spielOrdner);
            log.Info("unity version: " + unity);

            // ⚠ KORREKTUR: hier stand 'DetourProvider = null' mit der Begruendung, ScriptOne
            // injiziere keine Klassen und brauche darum keinen Provider. Das ist falsch -
            // JEDES DelegateSupport.ConvertDelegate laeuft ueber ClassInjector und braucht ihn.
            // Gemessen an zwei Stellen (Ereignisabos, Frame-Takt), beide mit
            // NullReferenceException in Il2CppInterop.Injection.Detour.Apply.
            Il2CppInteropRuntime.Create(new RuntimeConfiguration
                                {
                                    UnityVersion = unity,
                                    DetourProvider = new MonoModDetourProvider(),
                                })
                                .AddLogger(new InteropLog(log, cfg.InteropLog))
                                .Start();
            // ⚠ KORREKTUR: hier stand, AddLogger sei "bewusst weggelassen", weil es
            // Microsoft.Extensions.Logging hereinzoege. Die liegt ohnehin neben uns (sie kommt
            // als Abhaengigkeit von Il2CppInterop.Common), es kostet also keine Datei - und
            // ohne den Logger verschwinden genau die Meldungen, die einen veralteten Proxysatz
            // nach einem Spiel-Update benennen. Begruendung ausfuehrlich in InteropLog.cs.
            log.Info("Il2CppInterop started");

            HaengeStartHakenEin();
        }

        /// <summary>
        /// Unity-Fassung aus der Dateiversion von UnityPlayer.dll - keine Domaene noetig.
        /// </summary>
        private static Version LiesUnityVersion(string spielOrdner)
        {
            try
            {
                var fi = FileVersionInfo.GetVersionInfo(Path.Combine(spielOrdner, "UnityPlayer.dll"));
                // Unity schreibt dort z. B. "2022.3.62.63052" - die ersten drei zaehlen.
                var teile = (fi.FileVersion ?? "").Split('.');
                if (teile.Length >= 3 &&
                    int.TryParse(teile[0], out var a) && int.TryParse(teile[1], out var b) && int.TryParse(teile[2], out var c))
                    return new Version(a, b, c);
            }
            catch { }
            // Fallback: die Fassung, gegen die die Proxies erzeugt wurden.
            return new Version(2022, 3, 62);
        }

        private static void HaengeStartHakenEin()
        {
            try
            {
                var handle = NativeLibrary.Load(Path.Combine(_spielOrdner, "GameAssembly.dll"));
                var ziel = NativeLibrary.GetExport(handle, "il2cpp_runtime_invoke");
                _log.Info("il2cpp_runtime_invoke at 0x" + ziel.ToInt64().ToString("X"));

                _ersatz = OnRuntimeInvoke;
                _hook = new NativeHook(ziel, _ersatz);

                _log.Info("waiting for Internal_ActiveSceneChanged ...");
            }
            catch (Exception ex)
            {
                _log.Exception("HaengeStartHakenEin", ex);
            }
        }

        private static IntPtr OnRuntimeInvoke(RuntimeInvokeFn orig, IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc)
        {
            if (!_gestartet)
            {
                try
                {
                    var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(method));
                    if (name == "Internal_ActiveSceneChanged")
                    {
                        // ZUERST merken, dann handeln: wirft der Start, soll der Detour
                        // trotzdem genau einmal gefeuert haben und sich aushaengen.
                        _gestartet = true;
                        StarteWirt();
                        try { _hook.Dispose(); _log.Info("start hook removed"); }
                        catch (Exception ex) { _log.Exception("detour dispose", ex); }
                    }
                }
                catch (Exception ex)
                {
                    _gestartet = true;
                    _log.Exception("OnRuntimeInvoke", ex);
                }
            }
            return orig(method, obj, parameters, exc);
        }

        /// <summary>Getrennte Methode: so wird sie erst JIT-kompiliert, wenn die Proxies wirklich da sind.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StarteWirt()
        {
            _log.Info("il2cpp runtime is up - starting Lua host on the main thread");
            var skriptOrdner = Path.Combine(_spielOrdner, "LuaScripts");
            var zustandOrdner = Path.Combine(_spielOrdner, "ScriptOne", "state");
            StandaloneHost.Start(_log, skriptOrdner, zustandOrdner);
        }
    }
}
