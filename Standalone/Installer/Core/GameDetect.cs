using System;
using System.Collections.Generic;
using System.IO;

namespace ScriptOne.Setup
{
    internal enum Backend { Unbekannt, Il2Cpp, Mono, Beides }
    internal enum MelonZustand { NichtInstalliert, Aktiv, Abgeschaltet, AbgeschaltetOhneOrdner, Kaputt }

    /// <summary>Was in einem Spielordner vorgefunden wurde. Reine Messung, keine Entscheidung.</summary>
    internal sealed class Befund
    {
        internal string Spiel;
        internal Backend Backend;
        internal MelonZustand Melon;
        /// <summary>Die Bitbreite des Spiels - entscheidet, WELCHE winhttp.dll passt.</summary>
        internal Architektur Arch;
        internal string ArchText { get { return Arch == Architektur.x64 ? "64-bit"
                                       : Arch == Architektur.x86 ? "32-bit" : "unknown"; } }

        /// <summary>
        /// Wo MelonLoaders Plugins\, UserLibs\ und Mods\ WIRKLICH liegen.
        /// ⚠ Das ist NICHT zwangslaeufig der Spielordner. MelonLoader haengt diese Ordner an
        /// LoaderConfig.Loader.BaseDirectory, und das ist per Startargument umlenkbar
        /// (--melonloader.basedir). r2modman und der Thunderstore Mod Manager uebergeben es
        /// bedingungslos und halten MelonLoader in ihrem Profil; BepInEx.MelonLoader.Loader
        /// setzt es fest auf &lt;Spiel&gt;\MLLoader. Drei moegliche Orte also - wer den Spielordner
        /// verdrahtet, liegt in zwei Dritteln der Faelle falsch, und zwar LAUTLOS: es liegt dann
        /// einfach nichts in dem Ordner, aus dem der Loader liest.
        /// Vorgabe bleibt der Spielordner; ueberschrieben wird nur ausdruecklich.
        /// </summary>
        internal string MelonBasis;

        /// <summary>true, wenn MelonBasis GERATEN wurde und nicht vom Nutzer kam.</summary>
        internal bool MelonBasisGeraten;

        internal bool BepInEx;
        /// <summary>BepInEx mit seinem Lader - der Ordner allein sagt nichts.</summary>
        internal bool BepInExAktiv;

        /// <summary>
        /// BepInEx ist da und laeuft - aber ueber UNSEREN Einstiegspunkt, weil ScriptOne ihn
        /// uebernommen hat und BepInEx selbst nachstartet.
        /// </summary>
        /// <remarks>
        /// ⚠ OHNE DIESEN ZUSTAND GIBT ES ZWEI FALSCHE ANTWORTEN. BepInExAktiv misst, ob die
        /// doorstop_config.ini auf BepInEx zeigt - nach der Uebernahme zeigt sie auf uns, also
        /// ist es false, und daraus wurde (1) die Statuszeile "BepInEx folder, but its loader is
        /// not installed" ueber eine voll funktionierende Installation und (2) - schlimmer -
        /// WaehleModus = Standalone, womit ein ZWEITER Lauf BepInEx' winhttp.dll ueberschrieben
        /// und als UNSERE vermerkt haette; das folgende --remove haette sie geloescht und BepInEx
        /// ohne Lader zurueckgelassen. Gemessen 2026-08-20 in Landlord Simulator.
        /// </remarks>
        internal bool BepVonUnsGestartet;
        /// <summary>Die Hauptfassung von BepInEx: 5, 6 - oder 0, wenn nicht lesbar.</summary>
        internal int BepHaupt;
        internal string BepFassung;
        /// <summary>MelonLoaders Fassung, aus seiner eigenen DLL gelesen.</summary>
        internal Version MelonFassung;
        /// <summary>MelonLoader UND ein Doorstop: genau einer ueberlebt, lautlos.</summary>
        internal bool ZweiLader;
        internal bool FremdesDoorstop;
        internal bool StandaloneDa;
        internal bool PluginDa;

        /// <summary>Die gefundene Plugin-DATEI, oder null. Macht den Befund pruefbar statt bloss wahr/falsch.</summary>
        internal string PluginPfad;

        /// <summary>Welcher Lader es traegt - "MelonLoader" oder "BepInEx". Null, wenn nichts da ist.</summary>
        internal string PluginArt;
        internal string Exe;              // die gefundene Spiel-exe, fuer die Anzeige
        internal string DataOrdner;       // <Spiel>_Data, wenn gefunden

        internal string Tfm { get { return Backend == Backend.Mono ? "net472" : "net6"; } }

        internal string BackendText
        {
            get
            {
                switch (Backend)
                {
                    case Backend.Il2Cpp: return "Il2Cpp";
                    case Backend.Mono:   return "Mono";
                    case Backend.Beides: return "both?? (GameAssembly.dll AND a Managed folder)";
                    default:             return "unknown";
                }
            }
        }

        internal string MelonText
        {
            get
            {
                switch (Melon)
                {
                    case MelonZustand.Aktiv:                  return "active";
                    case MelonZustand.Abgeschaltet:           return "disabled";
                    case MelonZustand.AbgeschaltetOhneOrdner: return "disabled, but its folder is gone";
                    case MelonZustand.Kaputt:                 return "BROKEN (version.dll without a MelonLoader folder)";
                    default:                                  return "not installed";
                }
            }
        }
    }

    /// <summary>
    /// Erkennt Backend und Laderzustand eines Unity-Spielordners.
    /// </summary>
    /// <remarks>
    /// SPIELUNABHAENGIG: geprueft werden nur Unity-Merkmale, kein Spielname. Damit funktioniert
    /// der Installer auch an einem Spiel, das der Autor nie gesehen hat.
    ///
    /// ⚠ BEIDES pruefen, nicht eines annehmen: ein Steam-Zweigwechsel laesst Reste des anderen
    /// Backends liegen, und wer nur auf GameAssembly.dll sieht, haelt einen Mono-Ordner mit
    /// altem Il2Cpp-Rest fuer Il2Cpp.
    /// </remarks>
    /// <summary>Die Bitbreite eines Spiels. Eine falsch gewaehlte Proxy-DLL startet gar nicht.</summary>
    internal enum Architektur { Unbekannt, x86, x64 }

    internal static class GameDetect
    {
        internal static Befund Untersuche(string spiel) { return Untersuche(spiel, null); }

        /// <param name="melonBasis">
        /// Wo MelonLoaders Plugins\/UserLibs\ liegen, falls NICHT im Spielordner. Leer heisst
        /// Spielordner - siehe <see cref="Befund.MelonBasis"/>, warum das nur die Vorgabe ist.
        /// </param>
        internal static Befund Untersuche(string spiel, string melonBasis)
        {
            var b = new Befund { Spiel = spiel };
            if (string.IsNullOrEmpty(spiel) || !Directory.Exists(spiel)) return b;

            var il2cpp = File.Exists(Path.Combine(spiel, "GameAssembly.dll"));

            var mono = false;
            foreach (var d in SicherOrdner(spiel))
            {
                if (!d.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) continue;
                b.DataOrdner = d;
                if (File.Exists(Path.Combine(Path.Combine(spiel, d), Path.Combine("Managed", "Assembly-CSharp.dll"))))
                    mono = true;
            }

            b.Backend = il2cpp && mono ? Backend.Beides
                      : il2cpp        ? Backend.Il2Cpp
                      : mono          ? Backend.Mono
                                      : Backend.Unbekannt;

            foreach (var f in SicherDateien(spiel, "*.exe")) { b.Exe = f; break; }

            // ⚠ DIE ARCHITEKTUR STAND BISHER NIRGENDS - und das Paket lieferte nur x64. Ein
            //   32-Bit-Unity-Spiel haette eine 64-Bit-winhttp.dll bekommen; Windows laedt die
            //   gar nicht, das Spiel startet dann bestenfalls ohne uns und schlimmstenfalls
            //   nicht. Gemessen wird an UnityPlayer.dll, ersatzweise an der Spiel-exe.
            b.Arch = Bitbreite(Path.Combine(spiel, "UnityPlayer.dll"));
            if (b.Arch == Architektur.Unbekannt && b.Exe != null) b.Arch = Bitbreite(b.Exe);

            // --- MelonLoader. version.dll ALLEIN genuegt nicht: ohne seinen Ordner ist es eine
            //     Ruine, die den Start kapert und dann nichts findet - schlimmer als gar nicht da.
            var mlOrdner = Directory.Exists(Path.Combine(spiel, Pfade.MelonOrdner));
            var mlAn     = File.Exists(Path.Combine(spiel, Pfade.MelonDll));
            var mlAus    = File.Exists(Path.Combine(Pfade.AbstellOrdner(spiel), Pfade.MelonAus))
                        || File.Exists(Path.Combine(spiel, Pfade.MelonAus));

            // Die Basis ist per Vorgabe der Spielordner. Wer es anders weiss, sagt es.
            b.MelonBasis = string.IsNullOrEmpty(melonBasis) ? spiel : melonBasis;
            b.MelonBasisGeraten = string.IsNullOrEmpty(melonBasis);

            if (mlAn && mlOrdner)      b.Melon = MelonZustand.Aktiv;
            else if (mlAn)             b.Melon = MelonZustand.Kaputt;
            else if (mlAus)            b.Melon = mlOrdner ? MelonZustand.Abgeschaltet : MelonZustand.AbgeschaltetOhneOrdner;
            else                       b.Melon = MelonZustand.NichtInstalliert;

            // ⚠ ORDNER IST NICHT LADER. Gemessen 2026-08-19 in einem Testordner des Autors:
            //   BepInEx\ vorhanden, aber weder winhttp.dll noch doorstop_config.ini - also eine
            //   halbe Installation, die NICHTS tut. Wer nur den Ordner prueft, meldet "BepInEx
            //   present" und verunsichert damit ohne Grund, oder verweigert sogar den Dienst.
            // ⚠ UND WIR LEGEN DIESEN ORDNER SELBST AN. Der Kommentar oben argumentiert genau
            //   dagegen, die Zeile darunter tat es trotzdem: geprueft wurde nur, ob BepInEx\
            //   existiert. Seit der Vorsorge-Ablage erzeugt der Installer aber
            //   BepInEx\plugins\ScriptOne\ auf JEDEM Spiel - auch auf einem, das nie ein BepInEx
            //   gesehen hat. Folge, gemessen 2026-08-21 an einem nachgebauten Mono-Spiel: nach der
            //   eigenen Standalone-Installation meldete die Statuszeile "BepInEx folder, but its
            //   loader is not installed", in Warnfarbe - ueber einen Ordner, den derselbe Lauf
            //   eine Sekunde vorher angelegt hatte. Dieselbe Klasse wie die Warnung vor dem
            //   eigenen Sicherheitsnetz.
            //   Entschieden wird deshalb am KERN: dort liegen BepInEx.dll bzw. BepInEx.Core.dll,
            //   und den legt niemand ausser BepInEx selbst an.
            b.BepInEx = Directory.Exists(Path.Combine(Path.Combine(spiel, Pfade.BepInExOrdner),
                                                      Pfade.BepInExCore));

            // ⚠ DIE FASSUNG ENTSCHEIDET, OB UNSER ADAPTER UEBERHAUPT GELADEN WIRD. BepInEx 5 und 6
            //   filtern gegen VERSCHIEDENE Assembly-Namen (BepInEx gegen BepInEx.Core) - ein
            //   5er-Plugin wird unter 6 nicht abgelehnt, sondern GAR NICHT ANGEFASST. Der Nutzer
            //   saehe dann eine erfolgreiche Installation und im Spiel nichts. Deshalb hier
            //   messen, nicht hoffen: der Dateiname im core-Ordner ist der Diskriminator.
            var bepKern = Path.Combine(Path.Combine(spiel, Pfade.BepInExOrdner), Pfade.BepInExCore);
            var bep5 = Path.Combine(bepKern, "BepInEx.dll");
            var bep6 = Path.Combine(bepKern, "BepInEx.Core.dll");
            if (File.Exists(bep6)) { b.BepHaupt = 6; b.BepFassung = Dateifassung(bep6); }
            else if (File.Exists(bep5)) { b.BepHaupt = 5; b.BepFassung = Dateifassung(bep5); }

            // MelonLoader ebenso - unser Plugin benutzt MelonLoader.Utils.MelonEnvironment, und
            // das gibt es erst ab 0.6. Aeltere Fassungen wuerden das Plugin beim Laden werfen.
            foreach (var tfm in new[] { "net6", "net35" })
            {
                var ml = Path.Combine(Path.Combine(Path.Combine(spiel, Pfade.MelonOrdner), tfm), "MelonLoader.dll");
                if (!File.Exists(ml)) continue;
                Version v;
                if (Version.TryParse(Dateifassung(ml) ?? "", out v)) { b.MelonFassung = v; break; }
            }

            var doorDa = File.Exists(Path.Combine(spiel, Pfade.DoorstopDll));
            b.StandaloneDa = doorDa && PreloaderIrgendwo(spiel);

            // ⚠ BepInEx benutzt DIESELBE winhttp.dll. Ein fremdes Doorstop zu ueberschreiben
            //   toetet dessen Installation still.
            b.FremdesDoorstop = doorDa && b.BepInEx && !b.StandaloneDa;

            // BepInEx zaehlt nur als LAUFEND, wenn sein Lader wirklich da ist.
            // ⚠ NICHT AM ORDNER, SONDERN AM ZIEL. Gemessen 2026-08-19 im Spiel des Autors:
            //   er installierte BepInEx NACH ScriptOne, und dessen Setup ueberschrieb unsere
            //   winhttp.dll UND unsere doorstop_config.ini - darin stand danach
            //   'target_assembly=BepInEx\core\BepInEx.Preloader.dll'. Beide Lader
            //   beanspruchen dieselbe Datei; wer zuletzt installiert, gewinnt, und der andere
            //   ist lautlos tot. Die Konfiguration sagt also, WER wirklich laeuft.
            b.BepInExAktiv = b.BepInEx && doorDa && ZeigtAufBepInEx(spiel)
                             && Directory.Exists(Path.Combine(Path.Combine(spiel, Pfade.BepInExOrdner), Pfade.BepInExCore));
            if (b.BepInExAktiv) b.StandaloneDa = false;

            // Beleg fuer die Verkettung: die GESICHERTE Konfiguration des Fremdladers liegt da
            // und zeigt auf BepInEx. Ohne sie waere jede standalone-Installation neben einem
            // BepInEx-Ordner faelschlich eine "Verkettung".
            b.BepVonUnsGestartet = b.BepInEx && doorDa && !b.BepInExAktiv && b.StandaloneDa
                                   && ZeigtSicherungAufBepInEx(spiel)
                                   && Directory.Exists(Path.Combine(Path.Combine(spiel, Pfade.BepInExOrdner), Pfade.BepInExCore));

            // ⚠ ZWEI LADER, EIN EINSTIEGSPUNKT. MelonLoader und UnityDoorstop ersetzen denselben
            //   IAT-Eintrag (kernel32!GetProcAddress) in UnityPlayer.dll, und KEINER verkettet den
            //   anderen - es ueberlebt genau einer, lautlos. Wer das nicht gesagt bekommt, sucht
            //   den Fehler in seinen Mods.
            b.ZweiLader = b.Melon == MelonZustand.Aktiv && doorDa;

            // ⚠ BEIDE PLUGIN-WEGE, UND MELONLOADER UNTER SEINER BASIS. Hier stand eine Zeile,
            //   die ausschliesslich <spiel>\Plugins\ absuchte. Sie war damit in ZWEI Faellen
            //   blind: bei jeder BepInEx-Installation (anderer Ordner) und bei jeder
            //   MelonLoader-Installation ueber einen Mod-Manager (andere Basis). Beide meldeten
            //   "not installed", waehrend die DLL danebenlag und im Spiel lief.
            b.PluginPfad = SuchePlugin(Pfade.MelonPluginOrdner(b.MelonBasis), Pfade.MelonPluginAlle);
            if (b.PluginPfad != null) b.PluginArt = "MelonLoader";
            else
            {
                b.PluginPfad = SuchePlugin(Pfade.BepPluginOrdner(spiel), Pfade.BepAdapterAlle);
                if (b.PluginPfad != null) b.PluginArt = "BepInEx";
            }
            b.PluginDa = b.PluginPfad != null;
            return b;
        }

        /// <summary>Die erste vorhandene Datei aus <paramref name="namen"/> in <paramref name="ordner"/>.</summary>
        private static string SuchePlugin(string ordner, string[] namen)
        {
            try
            {
                if (string.IsNullOrEmpty(ordner) || !Directory.Exists(ordner)) return null;
                foreach (var n in namen)
                {
                    var p = Path.Combine(ordner, n);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        private static bool PreloaderIrgendwo(string spiel)
        {
            var core = Pfade.Core(spiel);
            if (!Directory.Exists(core)) return false;
            foreach (var d in SicherOrdner(core))
                if (File.Exists(Path.Combine(Path.Combine(core, d), Pfade.PreloaderDll))) return true;
            return File.Exists(Path.Combine(core, Pfade.PreloaderDll));   // flache Altfassung
        }

        /// <summary>Sieht so aus wie ein Unity-Spielordner? Fuer die Pfadeingabe im Fenster.</summary>
        /// <summary>Liest die Maschinenkennung aus dem PE-Kopf.</summary>
        /// <remarks>
        /// Aufbau: bei 0x3C steht der Versatz des PE-Kopfes, dort die Signatur PE, danach zwei Bytes
        /// Machine - 0x014C ist x86, 0x8664 ist x64. Kein Werkzeug noetig, 20 Zeilen, und die
        /// Antwort ist eindeutig. Bei jedem Zweifel Unbekannt zurueckgeben statt zu raten.
        /// </remarks>
        /// <summary>Zeigt die Doorstop-Konfiguration auf BepInEx' Preloader?</summary>
        /// <remarks>
        /// Der eine Satz, der die Frage "wer laeuft hier" beantwortet. Ohne ihn haelt man einen
        /// Ordner fuer einen Lader - und ScriptOne wuerde seine eigene, laengst ueberschriebene
        /// Installation fuer aktiv halten.
        /// </remarks>
        /// <summary>Die Dateifassung einer DLL - oder <c>null</c>.</summary>
        private static string Dateifassung(string datei)
        {
            try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(datei).FileVersion; }
            catch { return null; }
        }

        private static bool ZeigtAufBepInEx(string spiel)
        {
            return ZielIstBepInEx(Path.Combine(spiel, Pfade.DoorstopCfg));
        }

        /// <summary>
        /// Zeigt die GESICHERTE Konfiguration des Fremdladers auf BepInEx? Nur dann hat ScriptOne
        /// hier wirklich einen BepInEx-Einstieg uebernommen und startet ihn nach.
        /// </summary>
        private static bool ZeigtSicherungAufBepInEx(string spiel)
        {
            return ZielIstBepInEx(Path.Combine(Pfade.AbstellOrdner(spiel), Pfade.DoorstopCfgOriginal));
        }

        private static bool ZielIstBepInEx(string ini)
        {
            try
            {
                if (!File.Exists(ini)) return false;
                foreach (var z in File.ReadAllLines(ini))
                {
                    var t = z.Trim();
                    if (t.StartsWith("#") || t.IndexOf("target_assembly", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    return t.IndexOf(Pfade.BepInExOrdner, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { }
            return false;
        }

        internal static Architektur Bitbreite(string datei)
        {
            try
            {
                if (!File.Exists(datei)) return Architektur.Unbekannt;
                using (var f = File.OpenRead(datei))
                using (var r = new BinaryReader(f))
                {
                    if (r.ReadUInt16() != 0x5A4D) return Architektur.Unbekannt;   // "MZ"
                    f.Position = 0x3C;
                    var pe = r.ReadInt32();
                    if (pe <= 0 || pe + 6 > f.Length) return Architektur.Unbekannt;
                    f.Position = pe;
                    if (r.ReadUInt32() != 0x00004550) return Architektur.Unbekannt; // Signatur
                    var maschine = r.ReadUInt16();
                    if (maschine == 0x014C) return Architektur.x86;
                    if (maschine == 0x8664) return Architektur.x64;
                    return Architektur.Unbekannt;
                }
            }
            catch { return Architektur.Unbekannt; }
        }

        internal static bool SiehtWieUnityAus(string pfad)
        {
            if (string.IsNullOrEmpty(pfad) || !Directory.Exists(pfad)) return false;
            if (File.Exists(Path.Combine(pfad, "UnityPlayer.dll"))) return true;
            if (File.Exists(Path.Combine(pfad, "GameAssembly.dll"))) return true;
            foreach (var d in SicherOrdner(pfad))
                if (d.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Die hoechste installierte .NET-6-Laufzeit. <c>null</c>, wenn keine da ist.
        /// </summary>
        /// <remarks>
        /// Nur der Il2Cpp-Zweig braucht sie: dort startet Doorstop einen CoreCLR. Auf Mono
        /// laeuft der Preloader in der Mono-Domaene des Spiels und braucht gar keine.
        /// </remarks>
        internal static string FindeCoreClr()
        {
            var basis = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Path.Combine("dotnet", Path.Combine("shared", "Microsoft.NETCore.App")));
            if (!Directory.Exists(basis)) return null;

            string beste = null; Version besteV = null;
            foreach (var d in SicherOrdner(basis))
            {
                if (!d.StartsWith("6.", StringComparison.Ordinal)) continue;
                Version v; if (!Version.TryParse(d, out v)) continue;
                if (besteV != null && v <= besteV) continue;
                var voll = Path.Combine(basis, d);
                if (!File.Exists(Path.Combine(voll, "coreclr.dll"))) continue;
                besteV = v; beste = voll;
            }
            return beste;
        }

        private static IEnumerable<string> SicherOrdner(string p)
        {
            string[] a;
            try { a = Directory.GetDirectories(p); } catch { yield break; }
            foreach (var d in a) yield return Path.GetFileName(d);
        }

        private static IEnumerable<string> SicherDateien(string p, string muster)
        {
            string[] a;
            try { a = Directory.GetFiles(p, muster); } catch { yield break; }
            foreach (var f in a) yield return Path.GetFileName(f);
        }
    }
}
