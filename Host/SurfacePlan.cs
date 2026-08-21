using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ScriptOne.Host
{
    /// <summary>
    /// Der Bauplan der Flaeche - das, was BINDEN soll, getrennt von der Frage, WOHER es kommt.
    ///
    /// Es gibt zwei Quellen und genau eine Struktur:
    ///   * <see cref="SurfaceScan"/> ermittelt sie im laufenden Spiel per Reflexion.
    ///   * <see cref="RuntimeSurface"/> liest sie aus <c>ScriptOne\surface.txt</c>.
    /// Die Datei gewinnt, wenn es sie gibt - so kann der Nutzer die Flaeche beschneiden oder eine
    /// vom Spiel-Update kaputte Zeile herausnehmen, ohne dass wir etwas neu bauen muessen.
    ///
    /// Zeilenformat, bewusst KEIN JSON: alle Werte sind Bezeichner, in denen ein '|' nicht
    /// vorkommen kann. Damit gibt es keine Maskierungsfrage und keinen Parser, den man selbst
    /// schreibt und selbst falsch macht.
    /// </summary>
    internal static class SurfacePlan
    {
        internal sealed class Mitglied
        {
            internal string Lua, Clr, Art, Rueckgabe;
            internal string[] Args;
            /// <summary>Vom Typ SELBST deklariert (nicht von einer Basis geerbt).</summary>
            internal bool Eigen;
        }

        internal sealed class Tabelle
        {
            internal string Lua, Clr;

            /// <summary>
            /// WIE die Instanz erreicht wird. Zwei Wege, und sie schliessen einander aus:
            ///   false = ueber ein statisches 'Instance' am Typ selbst oder seiner Basiskette
            ///   true  = das Objekt liegt in der SZENE und wird per FindObjectOfType gesucht
            /// ⚠ Der zweite Weg ist nicht blosse Bequemlichkeit: es gibt Spiele, deren Manager
            /// GAR KEIN statisches Instance haben (gemessen an einem echten Titel: 403
            /// oeffentliche Typen, 0 Singletons, aber 101 Szenen-MonoBehaviours mit eigener
            /// Flaeche). Ohne diesen Weg bleibt dort alles leer.
            /// </summary>
            internal bool Szene;

            internal readonly List<Mitglied> Mitglieder = new List<Mitglied>();
        }

        internal static int Zaehle(List<Tabelle> plan)
        {
            var n = 0;
            foreach (var t in plan) n += t.Mitglieder.Count;
            return n;
        }

        // ------------------------------------------------------------------ schreiben

        /// <summary>
        /// Der Stempel der Spielfassung. Ohne ihn kann der Wirt ein Spiel-Update nicht von
        /// "der Nutzer hat die Datei beschnitten" unterscheiden - und genau diese beiden Faelle
        /// verlangen entgegengesetzte Antworten: beim Update soll er es SAGEN, beim Beschneiden
        /// soll er schweigen.
        /// </summary>
        internal static void Schreibe(string datei, List<Tabelle> plan, string herkunft, string wann,
                                      string stempel)
        {
            var b = new StringBuilder();
            b.Append("# ScriptOne surface - what this game offers, found by walking the types that\n");
            b.Append("# are actually loaded. Regenerated when this file is missing.\n");
            b.Append("#\n");
            b.Append("# THIS FILE IS THE CONTRACT. Once it exists it wins over the scan, so the Lua\n");
            b.Append("# names your scripts use stay put even when the game changes underneath.\n");
            b.Append("# You may DELETE lines to shrink the surface - the host binds what stands here.\n");
            b.Append("# You cannot add anything by editing: a line whose member no longer exists is\n");
            b.Append("# reported, and calling it raises an error that says so instead of a bare nil.\n");
            b.Append("# Delete the whole file to have the surface found again from scratch.\n");
            b.Append("#\n");
            b.Append("# T|<lua table>|<clr type>|static      instance comes from a static 'Instance'\n# T|<lua table>|<clr type>|scene       object is looked up in the scene\n");
            b.Append("# M|<lua name>|<clr member>|call|<return>|<arg>,<arg>\n");
            b.Append("# M|<lua name>|<clr member>|value|<return>|\n");
            b.Append("#\n");
            b.Append("# from: ").Append(herkunft ?? "?").Append('\n');
            b.Append("# game: ").Append(stempel ?? "?").Append('\n');
            b.Append("# on:   ").Append(wann ?? "?").Append('\n');
            b.Append("# tables: ").Append(plan.Count.ToString(CultureInfo.InvariantCulture))
             .Append("   members: ").Append(Zaehle(plan).ToString(CultureInfo.InvariantCulture)).Append('\n');
            b.Append('\n');

            foreach (var t in plan)
            {
                b.Append("T|").Append(t.Lua).Append('|').Append(t.Clr)
                 .Append('|').Append(t.Szene ? "scene" : "static").Append('\n');
                foreach (var m in t.Mitglieder)
                {
                    b.Append("M|").Append(m.Lua).Append('|').Append(m.Clr).Append('|')
                     .Append(m.Art).Append('|').Append(m.Rueckgabe).Append('|')
                     .Append(m.Args == null ? "" : string.Join(",", m.Args)).Append('\n');
                }
            }

            var ordner = Path.GetDirectoryName(datei);
            if (!string.IsNullOrEmpty(ordner) && !Directory.Exists(ordner)) Directory.CreateDirectory(ordner);
            File.WriteAllText(datei, b.ToString(), new UTF8Encoding(false));
        }

        // ------------------------------------------------------------------ lesen

        internal static List<Tabelle> Lies(string datei, out string herkunft, out string stempel)
        {
            herkunft = null; stempel = null;
            var liste = new List<Tabelle>();
            Tabelle aktuell = null;
            foreach (var roh in File.ReadAllLines(datei))
            {
                var z = roh.Trim();
                if (z.Length == 0) continue;
                if (z[0] == '#')
                {
                    const string marke = "# from:";
                    const string spiel = "# game:";
                    if (z.StartsWith(marke, System.StringComparison.Ordinal))
                        herkunft = z.Substring(marke.Length).Trim();
                    else if (z.StartsWith(spiel, System.StringComparison.Ordinal))
                        stempel = z.Substring(spiel.Length).Trim();
                    continue;
                }
                var teile = z.Split('|');
                if (teile.Length >= 3 && teile[0] == "T")
                {
                    // ⚠ Das vierte Feld kam spaeter dazu. Eine Datei OHNE es ist keine kaputte
                    //   Datei, sondern eine aeltere - sie bedeutet 'static', den einzigen Weg,
                    //   den es damals gab. Wer hier auf Feldzahl besteht, macht aus einem
                    //   Formatzuwachs einen Fehler beim Nutzer.
                    aktuell = new Tabelle
                    {
                        Lua = teile[1],
                        Clr = teile[2],
                        Szene = teile.Length >= 4 && teile[3] == "scene"
                    };
                    liste.Add(aktuell);
                }
                else if (teile.Length >= 5 && teile[0] == "M" && aktuell != null)
                {
                    var args = teile.Length > 5 && teile[5].Length > 0
                             ? teile[5].Split(',')
                             : new string[0];
                    aktuell.Mitglieder.Add(new Mitglied
                    {
                        Lua = teile[1], Clr = teile[2], Art = teile[3], Rueckgabe = teile[4], Args = args
                    });
                }
            }
            return liste;
        }
    }
}
