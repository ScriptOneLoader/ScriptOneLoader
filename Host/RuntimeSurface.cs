using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using MoonSharp.Interpreter;

namespace ScriptOne.Host
{
    /// <summary>
    /// Die Flaeche als DATEN statt als einkompilierter Quelltext.
    ///
    /// WARUM ES DAS GIBT
    /// <see cref="Generated.GeneratedSurface"/> wird auf der Entwicklermaschine gegen EIN Spiel
    /// erzeugt und in die DLL uebersetzt. In jedem anderen Spiel ist ihr Ertrag damit zwingend
    /// null - gemessen 2026-08-20 in No Knock: "tables: 0 | members: 0", waehrend die
    /// Assembly-CSharp.dll des Spiels danebenliegt und nie angesehen wird.
    ///
    /// Diese Klasse liest stattdessen eine Datei, die beim NUTZER aus SEINEM Spiel entstanden
    /// ist, und bindet per Reflexion. Sie nennt selbst KEINEN Spieltyp und gehoert deshalb in
    /// den Kern - sie laeuft auch dort, wo die Schedule-I-Sonde nein sagt. Genau das ist der
    /// Punkt: die Sonde beantwortet "ist DIESES Spiel da", diese Klasse "ist IRGENDEIN Spiel da".
    ///
    /// GRENZREGEL, unveraendert gegenueber der erzeugten Flaeche: ueber diese Grenze geht NIE
    /// ein Spielobjekt nach Lua. Nur Zahlen, Zeichenketten und Wahrheitswerte; Enums als Zahl.
    /// Was die Datei nicht nennt, wird nicht gebunden - der Erzeuger entscheidet, nicht der Wirt.
    /// </summary>
    internal static class RuntimeSurface
    {
        /// <summary>Der Stempel, der in der gelesenen Datei stand (null, wenn gescannt wurde).</summary>
        internal static string GelesenerStempel { get; private set; }

        /// <summary>Die Flaechenmeldung gehoert EINMAL ins Protokoll, nicht je Skript.</summary>
        private static bool _gemeldet;

        /// <summary>
        /// Schluessel "tabelle.member" jeder Bindung, die es im Spiel nicht mehr gibt.
        /// ⚠ Die API-Referenz wird aus der LEBENDEN Lua-Tabelle geschrieben - und dort steht
        /// ein Stummel wie eine ganz normale Funktion. Ohne diese Menge wuerde die Doku eine
        /// Bindung als aufrufbar auffuehren, die beim Aufruf erklaert, dass es sie nicht mehr
        /// gibt. Der Stummel ist die richtige LAUFZEIT-Antwort und die falsche DOKU-Antwort.
        /// </summary>
        internal static readonly System.Collections.Generic.HashSet<string> Verschwunden =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// ⚠ FlattenHierarchy ist PFLICHT und nicht Geschmackssache: das statische 'Instance'
        /// wird bei den allermeisten Managern von einer Basisklasse GEERBT (Singleton&lt;T&gt;).
        /// Ohne dieses Flag findet die Reflexion nur selbst deklarierte statische Member -
        /// gemessen fehlten damit 107 von 465 Bindungen, und zwar lautlos: die Tabelle entsteht,
        /// nur leer.
        /// </summary>
        private const BindingFlags StatischOeffentlich =
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        private const BindingFlags InstanzOeffentlich =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        /// <summary>
        /// Typen, deren Instanz in der SZENE liegt statt hinter einem statischen 'Instance'.
        /// Gefuellt beim Binden aus dem Plan, gelesen von <see cref="Instanz"/>.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> _szene =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Lua-Namen der Tabellen, deren Objekt in der SZENE liegt. Fuer die Referenz: dort ist
        /// die Herkunft eine NUTZUNGSREGEL, keine Fussnote - so eine Tabelle antwortet mit
        /// <c>nil</c>, solange das Objekt in der laufenden Szene nicht existiert (Hauptmenue,
        /// Ladebildschirm, vor dem ersten Spielstand). Wer das nicht weiss, haelt sie fuer kaputt.
        /// </summary>
        internal static readonly System.Collections.Generic.HashSet<string> AusDerSzene =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <summary>Bereits gefundene Szenenobjekte - siehe <see cref="AusSzene"/>.</summary>
        private static readonly System.Collections.Generic.Dictionary<string, object> _gefunden =
            new System.Collections.Generic.Dictionary<string, object>(StringComparer.Ordinal);

        internal static int Tabellen { get; private set; }
        internal static int Mitglieder { get; private set; }
        internal static string Herkunft { get; private set; }

        /// <summary>
        /// Liest die Flaechendatei und haengt jede Tabelle unter das Global "s1".
        /// Gibt die Zahl der gebundenen MITGLIEDER zurueck (nicht der Tabellen) - dieselbe
        /// Zaehlweise wie die erzeugte Flaeche, damit s1.surface_size vergleichbar bleibt.
        /// </summary>
        internal static int Install(Script script, Table s1, string datei, IScriptLog log, string stufe,
                                    string szeneStufe = SurfacePolicy.SzeneAuto)
        {
            Tabellen = 0; Mitglieder = 0; Herkunft = null; GelesenerStempel = null;
            Verschwunden.Clear(); _szene.Clear(); _gefunden.Clear(); AusDerSzene.Clear();

            List<SurfacePlan.Tabelle> plan = null;

            // 1. Die DATEI gewinnt, wenn es sie gibt. Der Nutzer soll die Flaeche beschneiden
            //    koennen, ohne dass ein Neustart seine Streichungen wieder ueberschreibt.
            if (!string.IsNullOrEmpty(datei) && File.Exists(datei))
            {
                try
                {
                    string herkunft, stempel;
                    plan = SurfacePlan.Lies(datei, out herkunft, out stempel);
                    Herkunft = herkunft;
                    GelesenerStempel = stempel;

                    // ⚠ EINE LEERE DATEI IST KEIN VERTRAG, SONDERN EIN GESCHEITERTER SCAN.
                    //   Die Datei gewinnt, damit Streichungen des Nutzers halten - aber auf
                    //   NULL streicht niemand: dafuer gibt es surface_policy = off, und das
                    //   steht genau so in der Konfiguration. Ohne diese Ausnahme zementiert
                    //   ein einziger ergebnisloser Scan den Zustand fuer immer: eine spaeter
                    //   verbesserte Regel laeuft nie wieder, weil die Datei sie ueberstimmt.
                    //   GEMESSEN in Cocaine Dealer - dort stand nach dem ersten Start eine
                    //   surface.txt mit "tables: 0", und jeder weitere Start band wieder
                    //   nichts, obwohl das Spiel 101 bindbare Typen in der Szene hat.
                    if (plan != null && plan.Count == 0)
                    {
                        if (log != null)
                            log.Info("surface file has no tables - treating it as an empty result, not as a" +
                                     " contract, and scanning again. Use surface_policy = off if you want no" +
                                     " generated surface at all.");
                        plan = null;
                        Herkunft = null;
                        GelesenerStempel = null;
                    }
                }
                catch (Exception ex)
                {
                    // Eine kaputte Flaechendatei darf den Wirt nicht mitreissen - sie wird
                    // gemeldet und der Scan uebernimmt.
                    if (log != null) log.Warn("surface file unreadable (" + ex.GetType().Name + ": " + ex.Message + ") - scanning instead");
                    plan = null;
                }
            }

            // 2. Sonst im laufenden Spiel ermitteln - und das Ergebnis hinschreiben, damit der
            //    Nutzer sieht, was gebunden wurde, und es beschneiden kann.
            if (plan == null)
            {
                int gesehen;
                var uhr = System.Diagnostics.Stopwatch.StartNew();
                try { plan = SurfaceScan.Ermittle(log, out gesehen, szeneStufe); }
                catch (Exception ex)
                {
                    if (log != null) log.Warn("surface scan failed (" + ex.GetType().Name + ": " + ex.Message + ") - continuing core-only");
                    return 0;
                }
                uhr.Stop();
                Herkunft = "scan of the loaded assemblies";
                if (log != null)
                    log.Info("surface scan: " + plan.Count + " tables from " + gesehen
                             + " public types in " + uhr.ElapsedMilliseconds + " ms");
                if (!string.IsNullOrEmpty(datei))
                {
                    try { SurfacePlan.Schreibe(datei, plan, "scan of the loaded assemblies",
                                               DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                                               SurfaceScan.Stempel); }
                    catch (Exception ex)
                    {
                        // Nicht schreiben zu koennen ist kein Grund, nicht zu binden.
                        if (log != null) log.Warn("could not write " + datei + " (" + ex.GetType().Name + ")");
                    }
                }
            }

            // ⚠ Ein Spiel-Update ist von einer beschnittenen Datei nur am STEMPEL zu
            //   unterscheiden - und die beiden Faelle verlangen entgegengesetzte Antworten.
            var geaendert = GelesenerStempel != null && SurfaceScan.Stempel != null
                            && GelesenerStempel != SurfaceScan.Stempel;

            var fehlend = new List<string>();
            var verschwunden = new List<string>();
            var wegenStufe = 0;

            foreach (var tab in plan)
            {
                var typ = FindeTyp(tab.Clr);
                if (typ == null) { fehlend.Add(tab.Clr); continue; }

                if (tab.Szene) { _szene.Add(tab.Clr); AusDerSzene.Add(tab.Lua); }

                var t = new Table(script);
                var n = 0;
                foreach (var m in tab.Mitglieder)
                {
                    if (!SurfacePolicy.Erlaubt(stufe, m)) { wegenStufe++; continue; }

                    var wert = Binde(typ, m);
                    if (wert != null) { t.Set(m.Lua, wert); n++; continue; }

                    // ⚠ HIER STAND 'continue', und das war die schlechteste der drei
                    //   Moeglichkeiten: das Skript bekommt dann 'nil' und stirbt mit
                    //   "attempt to call a nil value" - einer Meldung, die den Nutzer an
                    //   SEINEM Skript suchen laesst, waehrend sich das SPIEL geaendert hat.
                    //   Stattdessen wird ein Stummel gebunden, der genau das sagt.
                    var wohin = tab.Lua + "." + m.Lua;
                    var wasFehlt = tab.Clr + "." + m.Clr;
                    var kandidat = Aehnlich(typ, m);
                    Verschwunden.Add(wohin);
                    verschwunden.Add(wohin + "  ->  " + wasFehlt +
                                     (kandidat != null ? "   (looks like it became " + kandidat + ")" : ""));
                    t.Set(m.Lua, DynValue.NewCallback((c, a) =>
                    {
                        throw new ScriptRuntimeException(
                            "s1." + wohin + " is gone: this game no longer has " + wasFehlt +
                            (kandidat != null ? ". It looks like it became " + kandidat + "" : "") +
                            ". Your script is fine - the game changed. Edit ScriptOne\\surface.txt" +
                            " or delete it to have the surface found again.");
                    }));
                }
                if (n == 0) continue;               // leere Tabelle ist schlimmer als keine
                s1.Set(tab.Lua, DynValue.NewTable(t));
                Tabellen++; Mitglieder += n;
            }

            // ⚠ NUR EINMAL JE LAUF melden. InstallSurface laeuft je SKRIPT - bei zwei
            //   Skripten stand der ganze Block zweimal im Protokoll, und wer ihn liest,
            //   haelt eine wiederholte Meldung fuer zwei verschiedene Vorgaenge.
            var ersteMeldung = !_gemeldet;
            _gemeldet = true;
            if (log != null && ersteMeldung)
            {
                // Die Quelle GEHOERT in die Meldung: "from file" stand hier auch dann, wenn
                // gescannt wurde - und dann sucht man eine Datei, die es noch gar nicht gab.
                log.Info("surface bound: " + Tabellen + " tables, " + Mitglieder + " members"
                         + (Herkunft != null ? " (" + Herkunft + ")" : ""));
                log.Info(SurfacePolicy.Startmeldung(stufe, Mitglieder, wegenStufe));

                if (geaendert)
                    log.Warn("the game has changed since surface.txt was written. The Lua names in"
                             + " it stay valid on purpose - that is what keeps your scripts working."
                             + " Anything the game dropped is listed below.");

                if (verschwunden.Count > 0)
                {
                    var zeige = Math.Min(8, verschwunden.Count);
                    log.Warn(verschwunden.Count + " binding(s) no longer exist in this build."
                             + " Calling one raises an error that says so - it does not return nil:");
                    for (var i = 0; i < zeige; i++) log.Warn("    " + verschwunden[i]);
                    if (verschwunden.Count > zeige)
                        log.Warn("    ... and " + (verschwunden.Count - zeige) + " more");
                }
                // ⚠ Fehlende Typen NAMENTLICH melden, aber gedeckelt. Ein Spiel-Update, das
                //   einen Manager umbenennt, ist sonst nicht von "Datei passt gar nicht zu
                //   diesem Spiel" zu unterscheiden - und das sind sehr verschiedene Lagen.
                if (fehlend.Count > 0)
                {
                    var zeige = Math.Min(5, fehlend.Count);
                    var s = string.Join(", ", fehlend.GetRange(0, zeige).ToArray());
                    log.Warn("surface: " + fehlend.Count + " type(s) in the file do not exist in this build"
                             + (fehlend.Count > zeige ? " (first " + zeige + ": " + s + ", ...)" : " (" + s + ")")
                             + " - delete " + Path.GetFileName(datei) + " to have it found again");
                }
            }
            return Mitglieder;
        }

        // ------------------------------------------------------------------ Binden

        private static DynValue Binde(Type typ, SurfacePlan.Mitglied m)
        {
            if (m.Art == "call")
            {
                var mi = FindeMethode(typ, m.Clr, m.Args.Length);
                if (mi == null) return null;
                var art = m.Rueckgabe;
                var argTypen = m.Args;
                return DynValue.NewCallback((c, a) =>
                {
                    object ziel = mi.IsStatic ? null : Instanz(typ);
                    if (!mi.IsStatic && ziel == null) return DynValue.Nil;
                    var werte = new object[argTypen.Length];
                    for (var i = 0; i < argTypen.Length; i++) werte[i] = Wandle(a, i, argTypen[i]);
                    object r;
                    try { r = mi.Invoke(ziel, werte); }
                    catch (TargetInvocationException ex)
                    {
                        // Die INNERE Ausnahme ist die des Spiels - die aeussere sagt nur
                        // "Invoke ist fehlgeschlagen" und waere im Protokoll wertlos.
                        throw new ScriptRuntimeException(
                            (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message);
                    }
                    return NachLua(r, art);
                });
            }

            // value: Property zuerst, DANN Feld.
            // ⚠ Die Reihenfolge ist nicht beliebig. Unter Il2Cpp macht Il2CppInterop aus jedem
            //   Feld eine Property; ein GetField() gibt dort so gut wie immer null. Wer zuerst
            //   das Feld fragt, bindet auf beiden Backends verschieden viel - und merkt es nur
            //   auf einem.
            var pi = typ.GetProperty(m.Clr, StatischOeffentlich) ?? typ.GetProperty(m.Clr, InstanzOeffentlich);
            if (pi != null && pi.CanRead)
            {
                var art = m.Rueckgabe;
                var get = pi.GetGetMethod();
                if (get == null) return null;
                return DynValue.NewCallback((c, a) =>
                {
                    object ziel = get.IsStatic ? null : Instanz(typ);
                    if (!get.IsStatic && ziel == null) return DynValue.Nil;
                    try { return NachLua(get.Invoke(ziel, null), art); }
                    catch (TargetInvocationException ex)
                    {
                        throw new ScriptRuntimeException(
                            (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message);
                    }
                });
            }

            // ⚠ RS0030 hier BEWUSST punktuell aufgehoben, nicht aus Bequemlichkeit.
            //   Die Sperre sagt richtig: GetField gibt gegen Il2Cpp-Proxies IMMER null, weil
            //   Il2CppInterop jedes Feld zur Property macht. Genau darauf ist dieser Code
            //   ausgelegt - er fragt ZUERST GetProperty und kommt nur hierher, wenn das nichts
            //   ergab. Unter Il2Cpp ist das Ergebnis dann null und der Member wird nicht
            //   gebunden; unter MONO ist es der einzige Weg an ein 'public float speed;', und
            //   solche Felder sind in Unity-Spielen die Regel. Die Sperre ersatzlos zu befolgen
            //   hiesse, auf Mono alle oeffentlichen FELDER zu verlieren.
#pragma warning disable RS0030
            var fi = typ.GetField(m.Clr, StatischOeffentlich) ?? typ.GetField(m.Clr, InstanzOeffentlich);
#pragma warning restore RS0030
            if (fi != null)
            {
                var art = m.Rueckgabe;
                return DynValue.NewCallback((c, a) =>
                {
                    object ziel = fi.IsStatic ? null : Instanz(typ);
                    if (!fi.IsStatic && ziel == null) return DynValue.Nil;
                    return NachLua(fi.GetValue(ziel), art);
                });
            }
            return null;
        }

        /// <summary>
        /// Der Zugang zur Instanz. ⚠ Hier zahlt sich FlattenHierarchy aus: 'Instance' steht bei
        /// den meisten Managern auf einer generischen Basis (Singleton&lt;T&gt;), nicht auf dem
        /// Typ selbst. Deshalb braucht die Laufzeit die Basiskette NICHT zu kennen - anders als
        /// der Erzeuger, der fuer den using-Alias den geschlossenen generischen Typ nennen muss.
        /// </summary>
        private static object Instanz(Type typ)
        {
            if (_szene.Contains(typ.FullName)) return AusSzene(typ);

            var p = typ.GetProperty("Instance", StatischOeffentlich);
            if (p != null && p.CanRead) { try { return p.GetValue(null, null); } catch { return null; } }
#pragma warning disable RS0030   // siehe Begruendung oben - Mono-Rueckfall nach GetProperty
            var f = typ.GetField("Instance", StatischOeffentlich);
            if (f != null) { try { return f.GetValue(null); } catch { return null; } }
            // Kleinschreibung kommt vor - vier Typen eines gemessenen Spiels fuehren 'instance'.
            var p2 = typ.GetProperty("instance", StatischOeffentlich);
            if (p2 != null && p2.CanRead) { try { return p2.GetValue(null, null); } catch { return null; } }
            var f2 = typ.GetField("instance", StatischOeffentlich);
            if (f2 != null) { try { return f2.GetValue(null); } catch { return null; } }
#pragma warning restore RS0030
            return null;
        }

        /// <summary>
        /// Holt ein Objekt aus der laufenden Szene per <c>UnityEngine.Object.FindObjectOfType</c>,
        /// und zwar per REFLEXION - damit dieser Kern weiterhin keinen Unity-Typ NENNT und ohne
        /// Spiel uebersetzbar bleibt. Genau das ist der Grund, warum RuntimeSurface im Kern
        /// liegen darf; ein <c>using UnityEngine;</c> waere hier das Ende davon.
        ///
        /// ⚠ ERST BEIM AUFRUF, NICHT BEIM BINDEN. Gebunden wird frueh, oft bevor ueberhaupt eine
        /// Spielszene geladen ist. Ein damals gesuchtes Objekt waere null, und die Bindung waere
        /// fuer den Rest des Laufs tot - ohne Fehlermeldung, denn "nicht gefunden" sieht genauso
        /// aus wie "gibt es hier nicht".
        /// ⚠ UND DAS ERGEBNIS WIRD GEMERKT. FindObjectOfType durchsucht die ganze Szene; je
        /// Aufruf waere das eine Dauerlast, die im Leerlauf nicht auffaellt und im Spielbetrieb
        /// kostet - dieselbe Klasse Fehler wie ein FindObjectsOfType je Frame.
        /// ⚠ Unity ueberlaedt '==' fuer zerstoerte Objekte ("fake null"): ein gemerkter Verweis
        /// auf ein zerstoertes Objekt ist im .NET-Sinn NICHT null und wuerde nach einem
        /// Szenenwechsel stumm ins Leere zeigen. Deshalb wird er vor der Wiederverwendung
        /// geprueft und sonst neu gesucht.
        /// </summary>
        private static object AusSzene(Type typ)
        {
            object da;
            if (_gefunden.TryGetValue(typ.FullName, out da) && Lebt(da)) return da;
            _gefunden.Remove(typ.FullName);

            try
            {
                var objTyp = FindeTyp("UnityEngine.Object");
                if (objTyp == null) return null;

                foreach (var mi in objTyp.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (mi.Name != "FindObjectOfType" || mi.IsGenericMethodDefinition) continue;
                    var ps = mi.GetParameters();
                    if (ps.Length != 1) continue;

                    // Mono nimmt System.Type; der Il2Cpp-Proxy will Il2CppSystem.Type, und den
                    // bekommt man nur ueber dessen eigene Fabrik - ein Cast gibt es nicht.
                    object arg;
                    if (ps[0].ParameterType.IsAssignableFrom(typeof(Type))) arg = typ;
                    else arg = FremderTyp(ps[0].ParameterType, typ.FullName);
                    if (arg == null) continue;

                    var gefunden = mi.Invoke(null, new[] { arg });
                    if (gefunden == null || !Lebt(gefunden)) continue;
                    _gefunden[typ.FullName] = gefunden;
                    return gefunden;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Ein <c>Il2CppSystem.Type</c> (oder was der Proxy sonst verlangt) aus einem Namen -
        /// ueber die statische GetType-Fabrik des ZIELTYPS selbst.
        /// </summary>
        private static object FremderTyp(Type zielTyp, string name)
        {
            try
            {
                var m = zielTyp.GetMethod("GetType", BindingFlags.Public | BindingFlags.Static,
                                          null, new[] { typeof(string) }, null);
                return m == null ? null : m.Invoke(null, new object[] { name });
            }
            catch { return null; }
        }

        /// <summary>
        /// Unitys "fake null". Der ueberladene Vergleichsoperator ist per Reflexion nicht zu
        /// bekommen, ohne ihn namentlich aufzurufen - die Eigenschaft sagt dasselbe aus:
        /// ein zerstoertes MonoBehaviour hat kein gameObject mehr.
        /// </summary>
        private static bool Lebt(object o)
        {
            if (o == null) return false;
            try
            {
                var p = o.GetType().GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance);
                if (p == null) return true;
                return p.GetValue(o, null) != null;
            }
            catch { return false; }
        }

        private static MethodInfo FindeMethode(Type typ, string name, int argzahl)
        {
            foreach (var flags in new[] { StatischOeffentlich, InstanzOeffentlich })
            {
                foreach (var mi in typ.GetMethods(flags))
                {
                    if (mi.Name != name) continue;              // ORDINAL - C# ist case-sensitiv
                    if (mi.GetParameters().Length != argzahl) continue;
                    if (mi.IsGenericMethodDefinition) continue; // nicht aufrufbar ohne Typargument
                    return mi;
                }
            }
            return null;
        }

        private static object Wandle(CallbackArguments a, int i, string art)
        {
            switch (art)
            {
                case "bool":   return a.Count > i && a[i].CastToBool();
                case "string": return a.Count > i && !a[i].IsNil() ? a[i].CastToString() : string.Empty;
                case "int":    return (int)Zahl(a, i);
                case "long":   return (long)Zahl(a, i);
                case "float":  return (float)Zahl(a, i);
                case "double": return Zahl(a, i);
                default:       return (int)Zahl(a, i);          // Enums queren als Zahl
            }
        }

        private static double Zahl(CallbackArguments a, int i)
        {
            if (a.Count <= i) return 0;
            var v = a[i];
            return v.Type == DataType.Number ? v.Number : 0;
        }

        private static DynValue NachLua(object r, string art)
        {
            if (art == "void" || r == null) return DynValue.Nil;
            if (r is bool)   return DynValue.NewBoolean((bool)r);
            if (r is string) return DynValue.NewString((string)r);
            if (r is Enum)   return DynValue.NewNumber(Convert.ToInt64(r, CultureInfo.InvariantCulture));
            try { return DynValue.NewNumber(Convert.ToDouble(r, CultureInfo.InvariantCulture)); }
            catch { return DynValue.Nil; }   // NIE ein Spielobjekt durchreichen
        }

        // ------------------------------------------------------------------ Lesen

        /// <summary>
        /// Sucht auf demselben Typ einen Member mit derselben GESTALT (Art, Rueckgabe,
        /// Argumenttypen) und anderem Namen. Das ist ein Hinweis auf eine Umbenennung, kein
        /// Beweis - deshalb wird er GEMELDET und nicht angewandt. Ein automatisches Umhaengen
        /// waere hier gefaehrlich: zwei Methoden gleicher Gestalt sind haeufig, und die falsche
        /// zu binden ist schlimmer als gar keine.
        /// Eindeutig oder gar nicht: gibt es mehrere Kandidaten, ist die Frage nicht entscheidbar.
        /// </summary>
        private static string Aehnlich(Type typ, SurfacePlan.Mitglied m)
        {
            string treffer = null;
            if (m.Art == "call")
            {
                foreach (var mi in typ.GetMethods(StatischOeffentlich | BindingFlags.Instance))
                {
                    if (mi.IsSpecialName || mi.Name == m.Clr) continue;
                    ParameterInfo[] ps;
                    try { if (Kurzname(mi.ReturnType) != m.Rueckgabe) continue; ps = mi.GetParameters(); }
                    catch { continue; }
                    if (ps.Length != (m.Args == null ? 0 : m.Args.Length)) continue;
                    var passt = true;
                    for (var i = 0; i < ps.Length; i++)
                        if (Kurzname(ps[i].ParameterType) != m.Args[i]) { passt = false; break; }
                    if (!passt) continue;
                    if (treffer != null) return null;      // mehrdeutig - nicht raten
                    treffer = mi.Name + "()";
                }
                return treffer;
            }
            foreach (var pi in typ.GetProperties(StatischOeffentlich | BindingFlags.Instance))
            {
                if (pi.Name == m.Clr || !pi.CanRead) continue;
                try { if (Kurzname(pi.PropertyType) != m.Rueckgabe) continue; } catch { continue; }
                if (treffer != null) return null;
                treffer = pi.Name;
            }
            return treffer;
        }

        /// <summary>Dieselbe Abbildung wie im Scan - sonst vergleicht man Aepfel mit Birnen.</summary>
        private static string Kurzname(Type t)
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

        /// <summary>
        /// Typ ueber ALLE geladenen Assemblies suchen. Ein fester Assemblyname waere hier falsch:
        /// unter Mono heisst sie Assembly-CSharp, unter Il2Cpp traegt der Proxysatz denselben
        /// Namen, aber die Typen ein 'Il2Cpp'-Praefix am Namensraum - und manche Spiele legen
        /// ihren Code in mehrere Assemblies. Die Datei nennt den Namen so, wie er in DIESEM
        /// Spiel gilt; der Erzeuger hat ihn dort gelesen.
        /// </summary>
        private static Type FindeTyp(string vollerName)
        {
            var direkt = Type.GetType(vollerName, false);
            if (direkt != null) return direkt;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(vollerName, false); }
                catch { /* eine Assembly, die ihre Typen nicht aufloest, ist kein Grund aufzugeben */ }
                if (t != null) return t;
            }
            return null;
        }
    }
}
