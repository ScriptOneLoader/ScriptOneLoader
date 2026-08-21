using System;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace ScriptOne.Preloader
{
    /// <summary>
    /// Der Frame-Takt des Wirts: ein eigener, in die Il2Cpp-Domaene injizierter
    /// MonoBehaviour. Unity ruft sein <c>Update</c> je Bild auf dem Hauptfaden.
    /// </summary>
    /// <remarks>
    /// WARUM NICHT DER EINFACHERE WEG. Naheliegend waeren zwei BCL-Ereignisse, beide
    /// ohne Injektion - und beide sind in diesem Build WEGGESTRIPPT:
    ///   Application.onBeforeRender          -> BeforeRenderHelper.RegisterCallback strippt
    ///   RenderPipelineManager.beginFrameRendering (und beginContext/endFrame) strippt
    /// Il2Cpp wirft ungenutzte Methoden aus dem Build; registriert im Spiel niemand einen
    /// solchen Rueckruf, ist die REGISTRIERUNG weg, obwohl der Typ existiert. Zur Laufzeit
    /// kommt das als 'NotSupportedException: Method unstripping failed'.
    ///
    /// ⚠ Und der Stacktrace zeigt dabei auf die FALSCHE Methode: 'add_onBeforeRender'
    /// selbst ist vorhanden - gestrippt ist erst das, was sie aufruft. Wer nur seine
    /// eigene Aufrufstelle gegenprueft, bekommt Entwarnung. Geprueft gehoert die ganze
    /// Kette (Werkzeug: IL-Rumpf nach der Zeichenkette 'unstripping failed' absuchen,
    /// s. lib/dekompilat-lesen.md).
    ///
    /// Ein injizierter MonoBehaviour umgeht das Problem ganz: sein Update haengt an der
    /// nativen Schleife und braucht keine gestrippte BCL-Methode. Das ist auch der Weg,
    /// den MelonLoader und BepInEx gehen.
    /// </remarks>
    public sealed class TickBehaviour : MonoBehaviour
    {
        /// <summary>Wird von Il2CppInterop gerufen, wenn Unity die Komponente anlegt.</summary>
        public TickBehaviour(IntPtr zeiger) : base(zeiger) { }

        /// <summary>
        /// Pflichtpaar fuer injizierte Typen - ein blankes <c>: base()</c> genuegt nicht.
        /// </summary>
        public TickBehaviour()
            : base(ClassInjector.DerivedConstructorPointer<TickBehaviour>())
        {
            ClassInjector.DerivedConstructorBody(this);
        }

        // Wird per Namen von Unity gefunden - deshalb public und exakt so geschrieben.
        public void Update() => StandaloneHost.OnFrame();
    }
}
