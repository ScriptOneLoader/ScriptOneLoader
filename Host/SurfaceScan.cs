using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace ScriptOne.Host
{
    /// <summary>
    /// Ermittelt die Flaeche IM LAUFENDEN SPIEL, per Reflexion ueber die geladenen Assemblies.
    ///
    /// WARUM HIER UND NICHT IM INSTALLER
    /// Der Installer muesste die Assembly von aussen lesen und braeuchte dafuer Mono.Cecil im
    /// Paket - und unter Il2Cpp muessten die Proxies dafuer schon erzeugt sein. Der Wirt laeuft
    /// dagegen IM Prozess: dort sind die Typen bereits geladen, auf beiden Backends als ganz
    /// normale .NET-Typen. Dieselbe Stelle, an der der Wirt heute schon seine API-Referenz
    /// schreibt.
    ///
    /// DIE REGELN sind dieselben wie im Entwicklerwerkzeug tools\Gen-Surface.ps1, nur hier gegen
    /// Reflexion statt gegen Cecil:
    ///   * Einstiegspunkt = oeffentlicher Typ, NICHT generisch, mit einem oeffentlichen
    ///     statischen 'Instance' ueber die EIGENE BASISKETTE erreichbar (dafuer FlattenHierarchy).
    ///   * Es gibt KEINEN Namensraumfilter. Sehr viele Unity-Spiele legen ihren Code ohne
    ///     Namensraum ab; gefiltert wird ueber die ASSEMBLY, und die ist bereits die Auswahl.
    ///   * Ueber die Grenze gehen nur Zahlen, Zeichenketten und Wahrheitswerte. Enums als Zahl.
    ///     Was das nicht erfuellt, wird gar nicht erst gebunden.
    /// </summary>
    internal static class SurfaceScan
    {
        /// <summary>Wie viele Member wegen einer nicht ladbaren Signatur uebersprungen wurden.</summary>
        internal static int Uebersprungen { get; private set; }

        /// <summary>Typen, die nur die Flaeche ihrer Basis tragen (Typfamilien).</summary>
        internal static int Familien { get; private set; }

        private const BindingFlags StatischOeffentlich =
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        /// <summary>
        /// Assemblies, die niemals Spielcode sind. ⚠ Das ist eine Sperrliste, keine Positivliste:
        /// eine Positivliste muesste den Namen der Spielassembly KENNEN, und genau das darf ein
        /// spielunabhaengiger Wirt nicht voraussetzen. Manche Spiele legen ihren Code nicht in
        /// 'Assembly-CSharp', sondern in mehrere eigene Assemblies.
        /// </summary>
        private static readonly string[] NieSpielcode =
        {
            "mscorlib", "netstandard", "System", "Microsoft", "Mono.", "MonoMod",
            "UnityEngine", "Unity.", "Il2CppInterop", "Il2Cppmscorlib", "Il2CppSystem",
            "0Harmony", "MoonSharp", "ScriptOne", "BepInEx", "MelonLoader", "Iced",
            "Newtonsoft", "FishNet", "Cpp2IL", "AssetRipper", "Doorstop", "Anonymously Hosted"
        };

        /// <summary>
        /// Member, die technisch passen wuerden, aber nichts bedeuten - Serialisierung,
        /// Netzwerk-Klempnerei, Unity-Eigenheiten. Dieselbe Liste wie im Entwicklerwerkzeug.
        /// </summary>
        private static readonly string[] Klempnerei =
        {
            "Awake", "OnDestroy", "OnEnable", "OnDisable", "Start", "Update", "LateUpdate",
            "FixedUpdate", "OnGUI", "OnValidate", "Reset", "GetHashCode", "ToString", "Equals",
            "GetInstanceID", "Serialize", "Deserialize",
            "InitializeSyncVars", "NetworkInitialize", "Dispose", "MemberwiseClone", "Finalize"
            // WARNUNG: 'Save', 'Load', 'Reset', 'Read', 'Write' standen hier und sind BEWUSST
            //   raus. Bei einem SaveManager ist 'Save' nicht Klempnerei, sondern der Zweck des
            //   Typs - die Sperrliste hat drei Bindungen gekostet, die die eingefrorene Flaeche
            //   sehr wohl hatte. Ein VERB taugt nicht als Ausschlusskriterium; Klempnerei
            //   erkennt man an der Unity-Rueckruf-Signatur oder am Interface, nicht am Namen.
        };

        /// <summary>Kernnamen, die eine erzeugte Tabelle NIE verdecken darf.</summary>
        private static readonly string[] Reserviert =
        {
            "log", "warn", "console", "move_speed", "backend", "on", "after", "every",
            "cancel", "get", "set", "save", "surface_size"
        };

        // ------------------------------------------------------------------

        /// <summary>
        /// Identitaet der gescannten Assemblies. Die ModuleVersionId aendert sich bei JEDEM Bau,
        /// also auch dann, wenn Dateigroesse und Datum gleich bleiben - ein Zeitstempel taete
        /// das nicht. Gekuerzt, weil er nur verglichen und nie aufgeloest wird.
        /// </summary>
        internal static string Stempel { get; private set; }

        /// <summary>Wie viele Tabellen aus SZENEN-Objekten stammen statt aus Singletons.</summary>
        internal static int AusSzene { get; private set; }

        internal static List<SurfacePlan.Tabelle> Ermittle(IScriptLog log, out int gesehen)
        {
            return Ermittle(log, out gesehen, SurfacePolicy.SzeneAuto);
        }

        /// <param name="szeneStufe">
        /// <see cref="SurfacePolicy.SzeneAuto"/> / <c>SzeneAn</c> / <c>SzeneAus</c>.
        /// ⚠ 'auto' bindet Szenenobjekte NUR, wenn die Singleton-Regel nichts gefunden hat - und
        /// dieser Schnitt ist gemessen, nicht geraten: in einem Spiel MIT Singletons braechte die
        /// Szenenregel 477 zusaetzliche Typen, die DebugPanelAnimation, EventTriggerTest,
        /// BasicSample oder LookAtCamera heissen - Demo- und Hilfskomponenten, keine Spiel-API.
        /// In einem Spiel OHNE Singletons ist sie dagegen die EINZIGE Quelle (dort 101 Typen,
        /// und die Manager sind darunter). Reichweite genau dann, wenn sie nichts kostet.
        /// </param>
        internal static List<SurfacePlan.Tabelle> Ermittle(IScriptLog log, out int gesehen, string szeneStufe)
        {
            gesehen = 0;
            Uebersprungen = 0;
            Familien = 0;
            AusSzene = 0;
            var szenenKandidaten = new List<Type>();
            var plan = new List<SurfacePlan.Tabelle>();
            var belegt = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in Reserviert) belegt.Add(r);

            var kennung = new StringBuilder();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (IstFremd(asm)) continue;
                try
                {
                    if (kennung.Length > 0) kennung.Append(' ');
                    kennung.Append(asm.GetName().Name).Append(':')
                           .Append(asm.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 8));
                }
                catch { /* eine Assembly ohne lesbares Modul zaehlt eben nicht mit */ }
                Type[] typen;
                // ⚠ GetTypes() wirft, wenn EIN Typ nicht aufloest - und nimmt dann alle mit.
                //   ReflectionTypeLoadException traegt die geladenen trotzdem mit sich; wer nur
                //   catch't und weitermacht, verliert eine ganze Assembly wegen eines Typs.
                try { typen = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { typen = Ohne(ex.Types); }
                catch { continue; }

                foreach (var t in typen)
                {
                    if (t == null || !t.IsPublic || t.IsGenericTypeDefinition || t.IsInterface) continue;
                    gesehen++;

                    // WARNUNG: Reflexion ueber eine Spielassembly WIRFT, und zwar nicht nur beim
                    //   Laden. Gemessen an Schedule I: 'Nicht abstrakte Nicht-.cctor-Methode in
                    //   einer Schnittstelle' aus MethodInfo.ReturnType - also erst beim Lesen der
                    //   SIGNATUR eines einzelnen Members. Ungefangen reisst das den ganzen Wirt
                    //   beim Spielstart mit. Deshalb ist hier jede Ebene einzeln abgesichert:
                    //   ein kaputter Member kostet den Member, nicht den Typ, und ein kaputter
                    //   Typ kostet den Typ, nicht das Spiel.
                    SurfacePlan.Tabelle tab;
                    try
                    {
                        if (!HatInstanz(t))
                        {
                            // Kein statisches 'Instance' - aber vielleicht ein Objekt in der
                            // Szene. Aufheben und spaeter entscheiden; ob es gebraucht wird,
                            // steht erst fest, wenn der ganze Durchgang durch ist.
                            if (szeneStufe != SurfacePolicy.SzeneAus && IstSzenenobjekt(t))
                                szenenKandidaten.Add(t);
                            continue;
                        }
                        tab = new SurfacePlan.Tabelle { Clr = t.FullName, Lua = LuaName(t.Name) };
                        Sammle(t, tab);
                    }
                    catch (Exception ex)
                    {
                        // NAMENTLICH melden. Ein blosser Zaehler beantwortet nicht die Frage,
                        // die man dann hat: WELCHER Manager fehlt, und warum.
                        Uebersprungen++;
                        if (log != null && Uebersprungen <= 10)
                            log.Warn("surface: skipped " + t.FullName + " (" + ex.GetType().Name + ": " + ex.Message + ")");
                        continue;
                    }
                    if (tab.Mitglieder.Count == 0) continue;

                    // ⚠ MINDESTENS EIN Member muss vom Typ SELBST deklariert sein.
                    //   Gemessen 2026-08-20 in No Knock: von 221 Tabellen waren 157
                    //   Easy-Save-Typadapter (ES3Types.ES3Type_*), die ALLE dieselben sechs
                    //   geerbten Eigenschaften trugen - 71 % der Flaeche war eine einzige
                    //   Basisklasse, 157-fach wiederholt. Solche Typfamilien sind keine
                    //   Manager: sie haben keine eigene Flaeche, nur die ihrer Basis.
                    //   Die Regel ist strukturell und braucht keine Namensliste - eine
                    //   Sperrliste fremder Pakete waere endlos und je Spiel anders.
                    var eigen = false;
                    foreach (var m in tab.Mitglieder) if (m.Eigen) { eigen = true; break; }
                    if (!eigen) { Familien++; continue; }

                    // Namenskollision: der Kern gewinnt IMMER, und zwei Tabellen mit gleichem
                    // Kurznamen (verschiedene Namensraeume) duerfen einander nicht verdecken.
                    var name = tab.Lua;
                    if (belegt.Contains(name)) name = LuaName(Kurz(t.Namespace)) + "_" + tab.Lua;
                    if (belegt.Contains(name)) continue;      // zweimal daneben: lieber gar nicht
                    belegt.Add(name);
                    tab.Lua = name;
                    plan.Add(tab);
                }
            }
            // ⚠ ERST JETZT entscheiden. 'auto' haengt am ERGEBNIS des ganzen Durchgangs, nicht
            //   am einzelnen Typ - deshalb kann das nicht in der Schleife stehen.
            var nimmSzene = szeneStufe == SurfacePolicy.SzeneAn
                         || (szeneStufe == SurfacePolicy.SzeneAuto && plan.Count == 0);

            if (nimmSzene && szenenKandidaten.Count > 0)
            {
                if (log != null)
                    log.Info(plan.Count == 0
                        ? "no type has a static 'Instance' - falling back to scene objects ("
                          + szenenKandidaten.Count + " candidates)"
                        : "scene objects switched on explicitly (" + szenenKandidaten.Count + " candidates)");

                foreach (var t2 in szenenKandidaten)
                {
                    SurfacePlan.Tabelle tab2;
                    try
                    {
                        tab2 = new SurfacePlan.Tabelle { Clr = t2.FullName, Lua = LuaName(t2.Name), Szene = true };
                        Sammle(t2, tab2);
                    }
                    catch { Uebersprungen++; continue; }
                    if (tab2.Mitglieder.Count == 0) continue;

                    var eigen2 = false;
                    foreach (var m in tab2.Mitglieder) if (m.Eigen) { eigen2 = true; break; }
                    if (!eigen2) { Familien++; continue; }

                    var name2 = tab2.Lua;
                    if (belegt.Contains(name2)) name2 = LuaName(Kurz(t2.Namespace)) + "_" + tab2.Lua;
                    if (belegt.Contains(name2)) continue;
                    belegt.Add(name2);
                    tab2.Lua = name2;
                    plan.Add(tab2);
                    AusSzene++;
                }
            }

            Stempel = kennung.ToString();
            return plan;
        }

        /// <summary>
        /// Ein Objekt, das in der SZENE liegt statt hinter einer statischen Instanz - also ein
        /// MonoBehaviour. Erreichbar wird es spaeter per FindObjectOfType.
        /// ⚠ Der Typ darf NICHT abstrakt sein: eine abstrakte Basis liegt nie selbst in der
        /// Szene, und FindObjectOfType gaebe darauf bestenfalls irgendeine Ableitung zurueck.
        /// </summary>
        private static bool IstSzenenobjekt(Type t)
        {
            if (t.IsAbstract) return false;
            try
            {
                var d = t.BaseType;
                var tiefe = 0;
                while (d != null && tiefe < 16)
                {
                    if (d.FullName == "UnityEngine.MonoBehaviour") return true;
                    d = d.BaseType;
                    tiefe++;
                }
            }
            catch { }
            return false;
        }

        private static Type[] Ohne(Type[] roh)
        {
            var liste = new List<Type>();
            foreach (var t in roh) if (t != null) liste.Add(t);
            return liste.ToArray();
        }

        private static bool IstFremd(Assembly asm)
        {
            string name;
            try { name = asm.GetName().Name; } catch { return true; }
            if (string.IsNullOrEmpty(name)) return true;
            foreach (var p in NieSpielcode)
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// ⚠ FlattenHierarchy ist Pflicht: bei den allermeisten Managern steht das statische
        /// 'Instance' auf einer generischen Basisklasse und wird nur GEERBT. Ohne das Flag sieht
        /// die Reflexion nur selbst deklarierte statische Member - der Typ faellt dann durch,
        /// obwohl er ueber seine Basiskette sehr wohl eine Instanz fuehrt.
        /// Kleinschreibung kommt vor ('instance'), C# ist case-sensitiv, also beide fragen.
        /// </summary>
        private static bool HatInstanz(Type t)
        {
            foreach (var n in new[] { "Instance", "instance" })
            {
                try
                {
                    var p = t.GetProperty(n, StatischOeffentlich);
                    if (p != null && p.CanRead) return true;
                    // ⚠ RS0030 punktuell: unter Il2Cpp null (Feld ist dort Property, die
                    //   Zeile darueber hat sie schon gefragt), unter Mono der einzige Weg an
                    //   ein 'public static Foo Instance;'. Ohne das faellt auf Mono jeder
                    //   Singleton durch, der seine Instanz als FELD fuehrt - der Normalfall.
#pragma warning disable RS0030
                    var f = t.GetField(n, StatischOeffentlich);
#pragma warning restore RS0030
                    if (f != null) return true;
                }
                catch { /* eine unlesbare Signatur ist ein Nein, kein Abbruch */ }
            }
            return false;
        }

        /// <summary>
        /// WARNUNG: Ein Member zaehlt nur, wenn ihn das SPIEL deklariert. Ohne diese Pruefung
        /// zieht FlattenHierarchy an JEDER Tabelle den ganzen geerbten Unity-Bestand mit -
        /// 'name', 'tag', 'enabled', 'hide_flags', 'is_active_and_enabled' und so fort. Gemessen
        /// an Schedule I: 3647 statt 626 Mitglieder, also rund 25 Wiederholungen je Tabelle, die
        /// alle dasselbe bedeuten und nichts ueber das Spiel sagen. Fuer 'Instance' bleibt
        /// FlattenHierarchy noetig - das deklariert die Singleton-Basis, und die gehoert dem
        /// Spiel.
        /// </summary>
        private static bool VomSpiel(Type deklarierend)
        {
            if (deklarierend == null) return false;
            try { return !IstFremd(deklarierend.Assembly); } catch { return false; }
        }

        private static void Sammle(Type t, SurfacePlan.Tabelle tab)
        {
            var namen = new HashSet<string>(StringComparer.Ordinal);

            MethodInfo[] methoden;
            try { methoden = t.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                                          BindingFlags.Static | BindingFlags.FlattenHierarchy); }
            catch { methoden = new MethodInfo[0]; Uebersprungen++; }
            foreach (var mi in methoden)
            {
                if (mi.IsSpecialName) continue;                  // get_/set_/op_ - kommen als Property
                if (mi.IsGenericMethodDefinition) continue;
                if (!VomSpiel(mi.DeclaringType)) continue;
                if (Ist(Klempnerei, mi.Name)) continue;
                string rueck; ParameterInfo[] ps;
                try { rueck = Grenztyp(mi.ReturnType); ps = mi.GetParameters(); }
                catch { Uebersprungen++; continue; }
                if (rueck == null) continue;
                if (ps.Length > 4) continue;                     // eine Lua-Zeile mit 5 Argumenten liest niemand
                var args = new List<string>();
                var gut = true;
                foreach (var p in ps)
                {
                    if (p.ParameterType.IsByRef) { gut = false; break; }   // out/ref quert nicht
                    var a = Grenztyp(p.ParameterType);
                    if (a == null || a == "void") { gut = false; break; }
                    args.Add(a);
                }
                if (!gut) continue;
                var lua = LuaName(mi.Name);
                if (!namen.Add(lua)) continue;                   // Ueberladung: die erste gewinnt
                tab.Mitglieder.Add(new SurfacePlan.Mitglied
                {
                    Lua = lua, Clr = mi.Name, Art = "call", Rueckgabe = rueck, Args = args.ToArray(),
                    Eigen = mi.DeclaringType == t
                });
            }

            PropertyInfo[] properties;
            try { properties = t.GetProperties(BindingFlags.Public | BindingFlags.Instance |
                                               BindingFlags.Static | BindingFlags.FlattenHierarchy); }
            catch { properties = new PropertyInfo[0]; Uebersprungen++; }
            foreach (var pi in properties)
            {
                if (!pi.CanRead) continue;
                if (!VomSpiel(pi.DeclaringType)) continue;
                if (Ist(Klempnerei, pi.Name)) continue;
                string art;
                try
                {
                    if (pi.GetIndexParameters().Length > 0) continue;   // Indexer hat keinen Namen in Lua
                    art = Grenztyp(pi.PropertyType);
                }
                catch { Uebersprungen++; continue; }
                if (art == null || art == "void") continue;
                var lua = LuaName(pi.Name);
                if (!namen.Add(lua)) continue;
                tab.Mitglieder.Add(new SurfacePlan.Mitglied
                {
                    Lua = lua, Clr = pi.Name, Art = "value", Rueckgabe = art, Args = new string[0],
                    Eigen = pi.DeclaringType == t
                });
            }

            FieldInfo[] felder;
            try { felder = t.GetFields(BindingFlags.Public | BindingFlags.Instance |
                                       BindingFlags.Static | BindingFlags.FlattenHierarchy); }
            catch { felder = new FieldInfo[0]; Uebersprungen++; }
            foreach (var fi in felder)
            {
                if (fi.IsSpecialName) continue;
                if (!VomSpiel(fi.DeclaringType)) continue;
                if (Ist(Klempnerei, fi.Name)) continue;
                string art;
                try { art = Grenztyp(fi.FieldType); }
                catch { Uebersprungen++; continue; }
                if (art == null || art == "void") continue;
                var lua = LuaName(fi.Name);
                if (!namen.Add(lua)) continue;
                tab.Mitglieder.Add(new SurfacePlan.Mitglied
                {
                    Lua = lua, Clr = fi.Name, Art = "value", Rueckgabe = art, Args = new string[0],
                    Eigen = fi.DeclaringType == t
                });
            }
        }

        private static bool Ist(string[] liste, string name)
        {
            foreach (var x in liste) if (string.Equals(x, name, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// Die GRENZREGEL in einer Methode: was hier null zurueckgibt, quert die Grenze nicht.
        /// Kein Spielobjekt, keine Sammlung, kein Vector3 - nur Zahl, Zeichenkette, Wahrheitswert
        /// und (als Zahl) Enum.
        /// </summary>
        private static string Grenztyp(Type t)
        {
            if (t == typeof(void))   return "void";
            if (t == typeof(bool))   return "bool";
            if (t == typeof(string)) return "string";
            if (t.IsEnum)            return "int";
            if (t == typeof(int) || t == typeof(short) || t == typeof(byte) ||
                t == typeof(uint) || t == typeof(ushort) || t == typeof(sbyte)) return "int";
            if (t == typeof(long) || t == typeof(ulong)) return "long";
            if (t == typeof(float))  return "float";
            if (t == typeof(double) || t == typeof(decimal)) return "double";
            return null;
        }

        private static string Kurz(string ns)
        {
            if (string.IsNullOrEmpty(ns)) return "game";
            var i = ns.LastIndexOf('.');
            return i < 0 ? ns : ns.Substring(i + 1);
        }

        /// <summary>PascalCase -&gt; snake_case, identisch zu tools\Gen-Bindings.ps1.</summary>
        internal static string LuaName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var b = new StringBuilder(name.Length + 8);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c) && i > 0)
                {
                    var vorher = name[i - 1];
                    var naechsterKlein = i + 1 < name.Length && char.IsLower(name[i + 1]);
                    if (char.IsLower(vorher) || char.IsDigit(vorher) ||
                        (char.IsUpper(vorher) && naechsterKlein)) b.Append('_');
                }
                b.Append(char.ToLowerInvariant(c));
            }
            return b.ToString();
        }
    }
}
