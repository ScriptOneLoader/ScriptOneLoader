using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MonoMod.Core;
using IlDetour = Il2CppInterop.Runtime.Injection.IDetour;
using IlDetourProvider = Il2CppInterop.Runtime.Injection.IDetourProvider;

namespace ScriptOne.Preloader
{
    /// <summary>
    /// Der native Detour-Unterbau fuer Il2CppInterop.
    /// </summary>
    /// <remarks>
    /// ER IST NICHT OPTIONAL - das war ein teurer Irrtum. Die Annahme war: Il2CppInterop
    /// braucht Detours nur fuer KLASSENINJEKTION, und ScriptOne injiziert nichts. Falsch:
    /// jedes DelegateSupport.ConvertDelegate geht ueber ClassInjector.RegisterTypeInIl2Cpp
    /// und damit ueber InjectorHelpers.Setup(), das seinerseits Hooks anlegt. Ohne Provider
    /// gab es eine NullReferenceException in Detour.Apply - gemessen an ZWEI Stellen
    /// (Ereignisabos und Frame-Takt), beide Male beim Umwandeln eines Delegaten.
    ///
    /// BepInEx reicht hier Dobby oder Funchook durch - beides NATIVE Bibliotheken zum
    /// Mitliefern, und genau die Sorte Datei, die Virenscanner anspringen laesst. Hier
    /// stattdessen MonoMod.Core: rein verwaltet, per NuGet beziehbar, nichts Natives im
    /// Auslieferungsordner.
    ///
    /// Die Abbildung der beiden Schnittstellen ist NICHT namensgleich - wer sie verwechselt,
    /// baut einen Detour, der auf sich selbst zeigt:
    ///     Il2CppInterop.Target            = die ORIGINALfunktion   -> MonoMod.Source
    ///     Il2CppInterop.Detour            = unser ERSATZ           -> MonoMod.Target
    ///     Il2CppInterop.OriginalTrampoline= Sprung ins Original    -> MonoMod.OrigEntrypoint
    ///
    /// DIE REIHENFOLGE IST DER ZWEITE STOLPERSTEIN, und die beiden Vertraege sind
    /// GEGENLAEUFIG. Il2CppInterop.Injection.Detour.Apply macht (dekompiliert, 1.5.1):
    ///     var d = provider.Create(original, target);
    ///     trampoline = d.GenerateTrampoline&lt;T&gt;();   // ZUERST
    ///     d.Apply();                                    // DANACH
    /// MonoMod legt den OrigEntrypoint aber ERST BEIM ANWENDEN an - gemessen an einem
    /// eigenen Detour: HasOrigEntrypoint vor Apply() False, danach True. Ein Provider,
    /// der stur durchreicht, wirft deshalb im GenerateTrampoline, obwohl beide Seiten
    /// fuer sich genommen korrekt sind. Darum wird hier bei Bedarf VORGEZOGEN angewendet
    /// und das spaetere Apply() zum No-op - dann stimmt es in beiden Reihenfolgen.
    /// (Die Alternative ApplyByDefault=true scheidet aus: das nachfolgende Apply() wirft
    /// dann "Cannot apply a detour which is already applied" - ebenfalls gemessen.)
    /// </remarks>
    internal sealed class MonoModDetourProvider : IlDetourProvider
    {
        public IlDetour Create<TDelegate>(nint original, TDelegate target) where TDelegate : Delegate
            => new MonoModDetour(original, target);
    }

    internal sealed class MonoModDetour : IlDetour
    {
        // Das Ersatz-Delegat muss am Leben bleiben, solange der Detour steht - sonst raeumt
        // der GC es weg und das Spiel springt in freigegebenen Speicher.
        private static readonly List<Delegate> AmLeben = new List<Delegate>();

        private readonly ICoreNativeDetour _kern;
        private readonly object _schloss = new object();
        private bool _angewendet;

        internal MonoModDetour(nint original, Delegate ersatz)
        {
            lock (AmLeben) AmLeben.Add(ersatz);
            var ersatzPtr = Marshal.GetFunctionPointerForDelegate(ersatz);
            _kern = DetourFactory.Current.CreateNativeDetour(
                new CreateNativeDetourRequest(original, ersatzPtr) { ApplyByDefault = false });
        }

        public nint Target => _kern.Source;              // s. Kommentar oben: nicht verwechseln
        public nint Detour => _kern.Target;

        public nint OriginalTrampoline
        {
            get
            {
                StelleSicherAngewendet();
                return _kern.HasOrigEntrypoint ? _kern.OrigEntrypoint : IntPtr.Zero;
            }
        }

        public void Apply() => StelleSicherAngewendet();

        public void Dispose() => _kern.Dispose();

        public T GenerateTrampoline<T>() where T : Delegate
        {
            // Zieht das Anwenden vor, falls der Aufrufer das Trampolin zuerst haben will.
            StelleSicherAngewendet();

            if (!_kern.HasOrigEntrypoint)
                throw new NotSupportedException(
                    "MonoMod konnte fuer diesen Detour kein Trampolin erzeugen - das Original " +
                    "ist von hier aus nicht mehr aufrufbar. Factory: " +
                    DetourFactory.Current.GetType().FullName);

            return Marshal.GetDelegateForFunctionPointer<T>(_kern.OrigEntrypoint);
        }

        /// <summary>
        /// Wendet den Detour genau einmal an - egal, wie oft und aus welcher Richtung
        /// gefragt wird. Ein zweites _kern.Apply() waere eine InvalidOperationException.
        /// </summary>
        private void StelleSicherAngewendet()
        {
            if (_angewendet) return;
            lock (_schloss)
            {
                if (_angewendet) return;
                _kern.Apply();
                _angewendet = true;
            }
        }
    }
}
