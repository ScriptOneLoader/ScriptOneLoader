using System;
using System.IO;

namespace ScriptOne.Setup
{
    /// <summary>
    /// Die Ordner- und Dateinamen einer Installation - EINE Quelle fuer alle Werkzeuge.
    /// </summary>
    /// <remarks>
    /// ⚠ WARUM ZENTRAL: nach der Umbenennung vom 2026-08-19 blieb in der Zeile, die
    /// doorstop_config.ini SCHREIBT, der alte Pfad stehen. Bau und Tests blieben gruen, weil
    /// es Zeichenketten sind, und eine Standalone-Installation haette auf einen Ordner
    /// gezeigt, den es nicht gibt. Jeder Name steht deshalb GENAU EINMAL - hier.
    /// </remarks>
    /// <remarks>
    /// ⚠ Die Klasse hiess bis zum ersten Bau 'Layout'. Das kollidiert in einer Form-Ableitung
    /// mit dem geerbten Ereignis <c>Control.Layout</c> - der Compiler meldet dort CS0079
    /// ("kann nur links von += oder -= verwendet werden") und zwar 14 Mal, an Stellen die mit
    /// dem eigentlichen Fehler nichts zu tun haben. Kurze, allgemeine Klassennamen kollidieren
    /// in WinForms haeufig; 'Pfade' beschreibt ausserdem besser, was drinsteht.
    /// </remarks>
    internal static class Pfade
    {
        /// <summary>Der Pfadtrenner als KONSTANTE.</summary>
        /// <remarks>
        /// ⚠ Damit in den Meldungstexten kein einziger Backslash im Literal steht. Beim
        /// Erzeugen dieser Dateien sind daraus mehrfach ausgefuehrte Escapes geworden -
        /// einmal ein Tabulator im Pfad, zweimal CS1009. Eine Konstante kann keine Schicht
        /// mehr falsch interpretieren.
        /// </remarks>
        internal const string T = "\\";

        internal const string Wurzel      = "ScriptOne";
        internal const string CoreRuntime = "core-runtime";
        internal const string Interop     = "interopgenerator";
        /// <summary>Der Erzeuger SELBST plus seine Unity-Basisbibliotheken.</summary>
        /// <remarks>
        /// ⚠ Er bleibt nach der Installation liegen, und das ist Absicht: nach einem
        /// Spiel-Update muessen die Proxies neu erzeugt werden, und dann soll das OHNE
        /// Netz und ohne das Setup gehen. Die passenden Unity-Bibliotheken liegen dann
        /// schon daneben - genau das meint "sich selbst organisieren".
        /// </remarks>
        internal const string Generator  = "generator";
        internal const string Logs        = "logs";
        internal const string State       = "state";
        internal const string Doku        = "documentation";
        internal const string Lizenzen    = "licenses";
        internal const string Abgestellt  = "disabled-loaders";
        internal const string Diagnose    = "diagnostics";

        internal const string Cfg          = "ScriptOne-Starter.cfg";
        internal const string LogDatei     = "ScriptOne.log";
        internal const string PreloaderDll = "ScriptOne.Preloader.dll";

        internal const string DoorstopDll     = "winhttp.dll";

        /// <summary>
        /// Der Proxy-Name, unter dem UNSER Doorstop scharf liegt - der jeweils FREIE.
        /// </summary>
        /// <remarks>
        /// ⚠ DIESELBE DATEI KANN BEIDE NAMEN. Doorstop 4.5.0 exportiert die winhttp- UND die
        /// version-Funktionen (per Bytesuche belegt); der Name ist also eine Wahl, keine
        /// Eigenschaft des Baus. Genutzt wird das, um dem Fremdlader NICHT in die Quere zu
        /// kommen: MelonLoader belegt version.dll, BepInEx belegt winhttp.dll.
        ///
        /// ⚠ UND WARUM UEBERHAUPT SCHARF NEBEN EINEM FREMDLADER: gemessen 2026-08-20 in
        /// Schedule I (Il2Cpp). Beide Namen werden von UnityPlayer.dll importiert, also werden
        /// beide Proxies geladen - aber der Fremdlader gewinnt den Einstieg, und unser Doorstop
        /// bleibt vollstaendig still (kein ScriptOne.log in jenem Lauf). Wird der Fremdlader
        /// spaeter GELOESCHT, ist unserer der einzige und uebernimmt beim naechsten Spielstart
        /// von selbst - ohne Installer, ohne Klick. Genau das war die Anforderung des Autors,
        /// und der abgestellte Satz erfuellte sie nicht.
        /// </remarks>
        internal static string ProxyName(bool melonBelegtVersion, bool bepBelegtWinhttp)
        {
            // ⚠ GEGEN BEPINEX GEHT ES NICHT - und der freie DATEINAME hilft dabei nicht.
            //   BepInEx IST Doorstop. Es gibt genau EINE doorstop_config.ini, und BEIDE
            //   Proxy-DLLs lesen sie: unsere version.dll wuerde dasselbe target_assembly
            //   benutzen wie BepInEx' winhttp.dll. Entweder zeigt es auf BepInEx (dann laedt
            //   unsere DLL dessen Preloader ein zweites Mal) oder auf uns (dann startet
            //   BepInEx nicht mehr). Beim ersten Versuch am 2026-08-20 ist genau das passiert:
            //   der Installer schrieb sein target_assembly in BepInEx' Konfiguration und hat
            //   die fremde Installation damit stillgelegt - gemessen und sofort zurueckgenommen.
            //   Bei MelonLoader ist es anders: das benutzt gar kein Doorstop und hat keine
            //   solche Datei, deshalb traegt der freie Name dort (in Schedule I belegt).
            if (bepBelegtWinhttp) return null;                                 // Doorstop gehoert BepInEx
            if (melonBelegtVersion) return DoorstopDll;                        // winhttp.dll ist frei
            return DoorstopDll;                                                // kein Fremdlader
        }
        internal const string DoorstopCfg     = "doorstop_config.ini";
        internal const string DoorstopVersion = ".doorstop_version";

        /// <summary>
        /// Die ORIGINAL-doorstop_config.ini des Fremdladers, bevor ScriptOne den Einstieg
        /// uebernommen hat. ⚠ Ohne diese Sicherung waere das Uebernehmen unumkehrbar - und ein
        /// Eingriff in eine fremde Installation, den man nicht zurueckgeben kann, darf man
        /// nicht machen.
        /// </summary>
        internal const string DoorstopCfgOriginal = "doorstop_config.ini.original";

        /// <summary>
        /// Welche Proxy-DATEI der Installer selbst gelegt hat - eine Zeile, im ScriptOne-Ordner.
        /// </summary>
        /// <remarks>
        /// ⚠ AM INHALT IST DAS NICHT ENTSCHEIDBAR, und der Versuch hat Schaden angerichtet:
        /// BepInEx' Proxy IST dieselbe Doorstop-Datei wie unsere - gleiche Groesse, gleiche
        /// Kennung, byte-identisch. Eine Inhaltspruefung haelt sie zwangslaeufig fuer unsere und
        /// loescht sie beim Entfernen (gemessen 2026-08-20: nach '--remove' war BepInEx ohne
        /// Proxy und startete nicht mehr). Wer zwei Kopien DERSELBEN Datei unterscheiden muss,
        /// kann das nur ueber eine AUFZEICHNUNG - nicht ueber die Datei.
        /// </remarks>
        internal const string Bestand = ".installed-proxy";

        internal const string MelonDll     = "version.dll";
        internal const string MelonAus     = "version.dll.melonloader-off";
        internal const string MelonOrdner  = "MelonLoader";
        internal const string BepInExOrdner = "BepInEx";
        internal const string BepInExPlugins = "plugins";
        internal const string BepInExCore    = "core";
        /// <summary>Unser Unterordner unter BepInEx\plugins - ein Plugin darf tief liegen.</summary>
        internal const string BepInExUnterordner = "ScriptOne";
        internal const string BepAdapterDll  = "ScriptOne.BepInEx5.MONO.dll";
        internal const string Bep6MonoDll    = "ScriptOne.BepInEx6.MONO.dll";
        internal const string Bep6Il2CppDll  = "ScriptOne.BepInEx6.IL2CPP.dll";

        /// <summary>
        /// Welcher Adapter zu dieser BepInEx-Fassung und diesem Backend gehoert - oder null,
        /// wenn es keinen gibt.
        /// ⚠ Die drei Baue sind NICHT austauschbar: sie referenzieren verschiedene Assemblies
        /// (BepInEx / BepInEx.Unity.Mono / BepInEx.Unity.IL2CPP), und der Chainloader laedt nur,
        /// was SEINE Assembly referenziert. Der falsche Bau wird nicht abgelehnt, sondern GAR
        /// NICHT ANGEFASST - der Nutzer haette eine erfolgreiche Installation und im Spiel
        /// nichts. Deshalb entscheidet das eine Stelle und nicht jeder Aufrufer neu.
        /// </summary>
        internal static string BepAdapterFuer(int haupt, bool il2cpp)
        {
            if (haupt == 5) return il2cpp ? null : BepAdapterDll;   // BepInEx 5 gibt es nur fuer Mono
            if (haupt == 6) return il2cpp ? Bep6Il2CppDll : Bep6MonoDll;
            return null;
        }
        internal const string BepOrdnerImPaket = "bepinex";

        internal const string SkriptOrdner = "LuaScripts";
        /// <summary>Das EINE mitgelieferte Skript - Kernfunktionen, laeuft in jedem Spiel.</summary>
        internal const string HelloLua = "hello.lua";

        internal const string PluginOrdner = "Plugins";

        /// <summary>Die Dateien, die im Paket liegen und beim Installieren gebraucht werden.</summary>
        internal const string PaketOrdner = "setup-files";

        /// <summary>Der Beipackordner NEBEN dem Setup - in der ZIP und neben einer exe ohne Nutzlast.</summary>
        /// <remarks>
        /// ⚠ HIESS "ScriptOne-Setup-Files" UND WURDE DESHALB VERWECHSELT: nach dem Entpacken
        /// standen zwei Ordner mit "ScriptOne" im Namen nebeneinander, und der Autor fragte
        /// zu Recht, wieso er ScriptOne zweimal habe (2026-08-19). Der Name sagt jetzt, was
        /// er ist: Zubehoer des Setups, klein geschrieben, mit Unterstrich einsortiert - und
        /// er verschwindet nach einem erfolgreichen Lauf ohnehin.
        /// </remarks>
        internal const string BeipackOrdner = "_setup-files";

        /// <summary>MelonLoaders Ordner fuer Bibliotheken - wird VOR Plugins geladen.</summary>
        internal const string UserLibs = "UserLibs";

        /// <summary>Der Interpreter als DATEI, je Zielrahmen. Siehe Installer.LegeInterpreter.</summary>
        /// <summary>Der Lader je Architektur - x64 passt nicht in ein 32-Bit-Spiel.</summary>
        internal const string DoorstopOrdner  = "doorstop";

        internal const string MoonSharpOrdner = "moonsharp";
        internal const string MoonSharpDll    = "MoonSharp.Interpreter.dll";

        /// <summary>Die Lizenztexte, die eine Installation tragen MUSS - namentlich.</summary>
        /// <remarks>
        /// ⚠ Vorher war genau EINER davon zugesichert (UnityDoorstop), und ausgerechnet der
        /// wichtigste FEHLTE: MoonSharp ist im Plugin-Weg das einzige installierte Fremdwerk,
        /// und sein BSD-3-Text war nur per Dekompiler auffindbar. Acht Texte lagen dabei fuer
        /// Komponenten bei, die in diesem Weg gar nicht installiert werden. Eine Sollliste
        /// statt einer Stichprobe - sonst darf still alles ausser einem fehlen.
        /// (Audit 2026-08-19, Strang C.)
        /// </remarks>
        internal static readonly string[] LizenzSoll =
        {
            "ScriptOne.LICENSE.txt",
            "MoonSharp.LICENSE.txt",
            "UnityDoorstop.LICENSE.txt",
            "HarmonyX.LICENSE.txt",
            "Iced.LICENSE.txt",
            "Il2CppInterop.LICENSE.txt",
            "Microsoft.Extensions.Logging.Abstractions.LICENSE.txt",
            "Mono.Cecil.LICENSE.txt",
            "MonoMod.LICENSE.txt",
            "THIRDPARTY-NOTICE.md",
        };

        internal const string PluginIl2Cpp = "ScriptOne.IL2CPP.dll";
        internal const string PluginMono   = "ScriptOne.MONO.dll";

        /// <summary>
        /// Wohin das BepInEx-Plugin gehoert - und wo es folglich zu SUCHEN ist.
        /// ⚠ Diese Zusammensetzung stand nur im Installer, und die Erkennung baute sie gar
        /// nicht nach: sie sah einzig in MelonLoaders Plugins\ nach. Jede BepInEx-Installation
        /// meldete deshalb "not installed", beliebig oft wiederholbar (gemeldet 2026-08-20 mit
        /// Bildschirmfoto, Landlord Simulator). Installieren und Nachsehen muessen aus
        /// DERSELBEN Quelle kommen, sonst suchen sie an verschiedenen Orten.
        /// </summary>
        internal static string BepPluginOrdner(string spiel)
        {
            return Path.Combine(Path.Combine(Path.Combine(spiel, BepInExOrdner), BepInExPlugins),
                                BepInExUnterordner);
        }

        /// <summary>Alle drei Adapter-Dateinamen - zum SUCHEN, wenn Fassung und Backend offen sind.</summary>
        internal static readonly string[] BepAdapterAlle = { BepAdapterDll, Bep6MonoDll, Bep6Il2CppDll };

        /// <summary>
        /// MelonLoaders Plugin-Ordner unter der gewaehlten Basis.
        /// ⚠ Die Basis ist NICHT zwingend der Spielordner - ein Mod-Manager legt Plugins in sein
        /// Profil. Wer hier den Spielordner einsetzt, meldet dieselbe Installation als fehlend.
        /// </summary>
        internal static string MelonPluginOrdner(string basis) { return Path.Combine(basis, PluginOrdner); }

        internal static readonly string[] MelonPluginAlle = { PluginIl2Cpp, PluginMono };

        internal static string W(string spiel)            { return Path.Combine(spiel, Wurzel); }
        internal static string Core(string spiel)         { return Path.Combine(W(spiel), CoreRuntime); }
        internal static string CoreTfm(string s, string t){ return Path.Combine(Core(s), t); }
        internal static string LizenzOrdner(string spiel) { return Path.Combine(W(spiel), Lizenzen); }
        internal static string AbstellOrdner(string spiel){ return Path.Combine(W(spiel), Abgestellt); }
        internal static string CfgPfad(string spiel)      { return Path.Combine(W(spiel), Cfg); }

        /// <summary>Der Wert, der in doorstop_config.ini steht. Relativ zum Spielordner.</summary>
        internal static string TargetAssembly(string tfm)
        {
            return Wurzel + "\\" + CoreRuntime + "\\" + tfm + "\\" + PreloaderDll;
        }
    }
}
