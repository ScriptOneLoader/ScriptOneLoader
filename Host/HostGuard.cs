using System;

namespace ScriptOne.Host
{
    /// <summary>
    /// Sorgt dafuer, dass in EINEM Spielprozess hoechstens EIN ScriptOne-Wirt hochkommt.
    ///
    /// WARUM ES DAS GIBT
    /// Bis hierher wurde dasselbe Ziel durch VERSCHIEBEN erreicht: der Standalone-Zweig legte
    /// jede gefundene Plugin-Kopie als <c>.plugin-off</c> beiseite ("two entry points would be
    /// two hosts"). Das ist wirksam, aber es loest das falsche Problem - es verhindert, dass die
    /// Datei DA ist, statt zu verhindern, dass zwei Wirte LAUFEN.
    ///
    /// Und es kostet genau die Eigenschaft, die man braucht: liegt die Plugin-Kopie vorsorglich
    /// bereit, ueberlebt ScriptOne einen Lader, der ERST SPAETER installiert wird. Ohne sie ist
    /// dieser Fall lautlos toedlich - BepInEx ueberschreibt die eigene winhttp.dll samt
    /// doorstop_config.ini, der Standalone-Wirt liegt danach vollstaendig da und wird nie wieder
    /// geladen. Melden kann das niemand: es laeuft ja nichts.
    ///
    /// ⚠ EIN STATISCHES FELD GENUEGT NICHT. Preloader und Plugin sind verschiedene Assemblies;
    /// jede haette ihre eigene Statik und beide saehen sich fuer die erste. Der Anspruch muss
    /// deshalb an einem Ort liegen, den beide teilen - <see cref="AppDomain"/>-Daten sind das
    /// auf Mono wie auf CoreCLR.
    /// </summary>
    internal static class HostGuard
    {
        private const string Schluessel = "ScriptOne.Host.Active";

        /// <summary>
        /// Meldet den Anspruch an. Gibt <c>true</c> zurueck, wenn dieser Aufrufer der erste ist -
        /// nur dann darf er starten. Beim zweiten Aufruf <c>false</c>, samt Name des Ersten.
        /// </summary>
        internal static bool Beanspruche(string wer, out string ersterWar)
        {
            ersterWar = null;
            try
            {
                var da = AppDomain.CurrentDomain.GetData(Schluessel) as string;
                if (!string.IsNullOrEmpty(da)) { ersterWar = da; return false; }
                AppDomain.CurrentDomain.SetData(Schluessel, wer ?? "?");
                return true;
            }
            catch
            {
                // Kann die Laufzeit keine AppDomain-Daten fuehren, ist das kein Grund, den Start
                // zu verweigern: ein Wirt, der wegen einer Sperre gar nicht laeuft, ist schlechter
                // als zwei, die sich in die Quere kommen. Die Sperre ist eine Absicherung, keine
                // Vorbedingung.
                return true;
            }
        }

        /// <summary>Fuer die Testbank: den Anspruch wieder freigeben.</summary>
        internal static void Freigeben()
        {
            try { AppDomain.CurrentDomain.SetData(Schluessel, null); } catch { }
        }
    }
}
