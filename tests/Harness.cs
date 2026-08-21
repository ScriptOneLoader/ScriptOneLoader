using System;
using System.Globalization;
using System.IO;
using ScriptOne.Game;
using ScriptOne.Host;

namespace harness
{
    public static class Harness
    {
        private static readonly string ScriptDir =
            // Aus dem eigenen Ort abgeleitet. Ein fester Pfad waere maschinengebunden und stuende
            // mit Benutzernamen im Repo: bin\<cfg>\<tfm>\ -> vier Ebenen hoch, dann scripts\.
            System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.AppContext.BaseDirectory, "..", "..", "..", "..", "scripts"));

        public static int Main()
        {
            Console.WriteLine("== ScriptOne ohne Spiel: das ECHTE movespeed.lua durch den ECHTEN Wirt ==");
            Console.WriteLine("   Kultur dieses Laufs: " + CultureInfo.CurrentCulture.Name);
            Console.WriteLine();

            var fails = 0;
            fails += Fall1_Host();
            fails += Fall2_Gast();
            fails += Fall3_Endlosschleife();
            fails += Fall4_Syntaxfehler();
            fails += Fall5_Ereignisbruecke();
            fails += Fall6_ZeitUndGedaechtnis();
            fails += Fall7_Referenz();
            fails += Fall8_Konsolenpolitik();
            fails += Fall9_VerwaisterWirt();
            fails += Fall10_Taktwache();

            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "ALLE FAELLE BESTANDEN" : ("FEHLGESCHLAGEN: " + fails));
            return fails == 0 ? 0 : 1;
        }

        private sealed class KonsolenLog : IScriptLog
        {
            public void Info(string m)  { Console.WriteLine("       [log ] " + m); }
            public void Warn(string m)  { Console.WriteLine("       [warn] " + m); }
            public void Error(string m) { Console.WriteLine("       [err ] " + m); }
        }

        private static LuaEngine Neu(string konsolenStufe = ConsolePolicy.Sicher)
        {
            GameBridge.Submitted.Clear();
            GameBridge.MoveSpeed = 1f;
            GameBridge.Ready = false;
            return new LuaEngine(new KonsolenLog(), System.IO.Path.Combine(Path.GetTempPath(), "scriptone_state"),
                                 null, new HostSchalter { Konsole = konsolenStufe });
        }

        // Der Normalfall: Einzelspieler bzw. Host.
        private static int Fall1_Host()
        {
            Console.WriteLine("[Fall 1] Host - das Skript soll das Tempo auf 7 setzen");
            // Der Starterkit braucht 'bind' und damit die offene Stufe; das steht auch in
            // seinem Kopf. Die Vorgabe wird gleich darunter gegengeprueft.
            var e = Neu(ConsolePolicy.Unbegrenzt);
            GameBridge.IsHost = true;
            e.LoadFolder(ScriptDir);
            GameBridge.Ready = true;
            e.PollGameReady();

            Console.WriteLine("       gesendet: " + string.Join(" | ", GameBridge.Submitted.ToArray()));
            Console.WriteLine("       Multiplikator danach: " + GameBridge.MoveSpeed.ToString(CultureInfo.InvariantCulture));

            // Geprueft werden BEIDE ausgelieferten Skripte. Dass danach ein anderer
            // Multiplikator steht, ist kein Fehler: movespeed.lua und starterkit.lua setzen
            // beide das Tempo, das spaeter geladene gewinnt. Genau so verhaelt es sich im Spiel.
            if (!GameBridge.Submitted.Contains("setmovespeed 7"))
                return Bad("movespeed.lua hat 'setmovespeed 7' nicht gesendet");
            if (!GameBridge.Submitted.Contains("bind f5 save"))
                return Bad("starterkit.lua hat seine Tastenbelegungen nicht gesendet");

            // ⚠ UND DIE GEGENRICHTUNG, sonst belegt dieser Fall nur die offene Stufe:
            // unter der VORGABE 'safe' darf keine einzige Bindung ankommen. Ohne diese
            // Haelfte waere eine Politik, die versehentlich alles durchlaesst, unauffaellig.
            var eSicher = Neu();
            GameBridge.IsHost = true;
            eSicher.LoadFolder(ScriptDir);
            GameBridge.Ready = true;
            eSicher.PollGameReady();
            foreach (var z in GameBridge.Submitted)
                if (z.StartsWith("bind ", StringComparison.Ordinal))
                    return Bad("unter der Vorgabe 'safe' ist eine Bindung durchgekommen: " + z);
            if (!GameBridge.Submitted.Contains("setmovespeed 7"))
                return Bad("unter 'safe' fehlt 'setmovespeed 7' - die Liste sperrt zu viel");

            // Zweiter Poll darf NICHTS erneut senden.
            var vorher = GameBridge.Submitted.Count;
            e.PollGameReady();
            if (GameBridge.Submitted.Count != vorher) return Bad("hat beim zweiten Poll erneut gesendet");
            return Ok(vorher + " Befehle aus 2 Skripten, kein Doppelfeuer");
        }

        // Der stille Fall: als Mehrspieler-Gast schluckt das Spiel den Befehl wortlos.
        private static int Fall2_Gast()
        {
            Console.WriteLine("[Fall 2] Mehrspieler-Gast - das Skript soll das MERKEN und warnen");
            var e = Neu();
            GameBridge.IsHost = false;
            e.LoadFolder(ScriptDir);
            GameBridge.Ready = true;
            e.PollGameReady();

            Console.WriteLine("       gesendet: " + string.Join(" | ", GameBridge.Submitted.ToArray()));
            Console.WriteLine("       Multiplikator danach: " + GameBridge.MoveSpeed.ToString(CultureInfo.InvariantCulture));
            if (Math.Abs(GameBridge.MoveSpeed - 1f) > 0.0001f)
                return Bad("Attrappe haette nichts setzen duerfen");
            return Ok("Warnung statt falscher Erfolgsmeldung (siehe Log oben)");
        }

        // Endlosschleife in einem Skript darf das Spiel nicht einfrieren.
        private static int Fall3_Endlosschleife()
        {
            Console.WriteLine("[Fall 3] Endlosschleife in einem Skript");
            var dir = Path.Combine(Path.GetTempPath(), "scriptone_probe_endlos");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "endlos.lua"),
                "s1.on('game_ready', function() while true do end end)\n");

            var e = Neu();
            GameBridge.IsHost = true;
            e.LoadFolder(dir);
            GameBridge.Ready = true;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            e.PollGameReady();
            sw.Stop();
            Console.WriteLine("       Poll kehrte nach " + sw.ElapsedMilliseconds + " ms zurueck");
            Directory.Delete(dir, true);
            if (sw.ElapsedMilliseconds > 5000) return Bad("zu lange blockiert");
            return Ok("abgebrochen statt eingefroren");
        }

        // Kaputte Datei darf den Wirt nicht mitreissen.
        private static int Fall4_Syntaxfehler()
        {
            Console.WriteLine("[Fall 4] Datei mit Syntaxfehler neben einer guten Datei");
            var dir = Path.Combine(Path.GetTempPath(), "scriptone_probe_kaputt");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "a_kaputt.lua"), "das ist kein lua\n");
            File.WriteAllText(Path.Combine(dir, "b_gut.lua"),
                "s1.on('game_ready', function() s1.console('setmovespeed 3') end)\n");

            var e = Neu();
            GameBridge.IsHost = true;
            e.LoadFolder(dir);
            GameBridge.Ready = true;
            e.PollGameReady();
            Directory.Delete(dir, true);

            Console.WriteLine("       geladene Skripte: " + e.ScriptCount);
            Console.WriteLine("       Multiplikator danach: " + GameBridge.MoveSpeed.ToString(CultureInfo.InvariantCulture));
            if (Math.Abs(GameBridge.MoveSpeed - 3f) > 0.0001f)
                return Bad("das gute Skript lief nicht");
            return Ok("kaputte Datei uebersprungen, gute Datei lief");
        }

        // Die Gegenrichtung: loest ein Spielereignis wirklich einen Lua-Rueckruf aus?
        private static int Fall5_Ereignisbruecke()
        {
            Console.WriteLine("[Fall 5] Ereignisbruecke Spiel -> Lua");
            var dir = Path.Combine(Path.GetTempPath(), "scriptone_probe_events");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "ev.lua"),
                "s1.on('player_arrested', function() s1.console('setmovespeed 5') end)\n" +
                "s1.on('player_freed',    function() s1.console('setmovespeed 1') end)\n");

            var e = Neu();
            GameBridge.IsHost = true;
            e.LoadFolder(dir);
            GameBridge.Ready = true;
            e.PollGameReady();

            EventBridge.Simulate("player_arrested");
            var nachHaft = GameBridge.MoveSpeed;
            EventBridge.Simulate("player_freed");
            var nachFrei = GameBridge.MoveSpeed;
            Directory.Delete(dir, true);

            Console.WriteLine("       Attach() gerufen: " + EventBridge.AttachCount + "x");
            Console.WriteLine("       nach player_arrested: " + nachHaft.ToString(CultureInfo.InvariantCulture)
                              + " | nach player_freed: " + nachFrei.ToString(CultureInfo.InvariantCulture));
            if (Math.Abs(nachHaft - 5f) > 0.0001f) return Bad("player_arrested kam nicht an");
            if (Math.Abs(nachFrei - 1f) > 0.0001f) return Bad("player_freed kam nicht an");
            return Ok("beide Ereignisse erreichten ihren Lua-Rueckruf");
        }

        // Die beiden neuen Organe: Zeitgeber und Gedaechtnis ueber Neustarts hinweg.
        /// <summary>
        /// Die Referenz, die der Wirt selbst schreibt. Geprueft wird NICHT, dass Dateien
        /// entstehen - das taete auch ein leerer Lauf -, sondern dass ihr INHALT die
        /// tatsaechlich installierte Flaeche wiedergibt: eine Kernfunktion, eine erzeugte
        /// Tabelle und die Zahl aus s1.surface_size muessen darin vorkommen.
        /// </summary>
        /// <summary>
        /// Die Positivliste fuer s1.console. Geprueft wird BEIDES: dass Erlaubtes durchgeht
        /// und dass Verbotenes haengenbleibt. Ein Test nur auf "wird abgelehnt" waere auch
        /// mit einer Politik bestanden, die alles sperrt.
        /// </summary>
        private static int Fall8_Konsolenpolitik()
        {
            Console.WriteLine("[Fall 8] Positivliste fuer s1.console");
            var fehler = 0;

            // (Zeile, Stufe, soll durchgehen?)
            var faelle = new[]
            {
                new object[] { "setmovespeed 7",        ConsolePolicy.Sicher,     true  },
                new object[] { "SetMoveSpeed 7",        ConsolePolicy.Sicher,     true  },  // das Spiel schreibt klein
                new object[] { "  freecam  ",           ConsolePolicy.Sicher,     true  },
                new object[] { "changecash 999999",     ConsolePolicy.Sicher,     false },
                new object[] { "save",                  ConsolePolicy.Sicher,     false },
                new object[] { "setweather rain",       ConsolePolicy.Sicher,     false },
                new object[] { "setweather rain",       ConsolePolicy.Erweitert,  true  },
                new object[] { "changecash 999999",     ConsolePolicy.Erweitert,  false },
                new object[] { "changecash 999999",     ConsolePolicy.Unbegrenzt, true  },
                // ⚠ Der Kern: bind haengt einen BELIEBIGEN Befehl an eine Taste und laeuft
                // damit an der Liste vorbei. Es muss auf BEIDEN gesperrten Stufen fallen.
                new object[] { "bind t changecash 999", ConsolePolicy.Sicher,     false },
                new object[] { "bind t setmovespeed 7", ConsolePolicy.Erweitert,  false },
                new object[] { "bind t changecash 999", ConsolePolicy.Unbegrenzt, true  },
                new object[] { "",                      ConsolePolicy.Sicher,     false },
                new object[] { "   ",                   ConsolePolicy.Sicher,     false },
            };

            foreach (var f in faelle)
            {
                var zeile = (string)f[0]; var stufe = (string)f[1]; var soll = (bool)f[2];
                var grund = ConsolePolicy.Ablehnungsgrund(zeile, stufe);
                var ist = grund == null;
                if (ist == soll) continue;
                Console.WriteLine("       FALSCH: '" + zeile + "' unter '" + stufe + "' -> " +
                                  (ist ? "durchgelassen" : "abgelehnt (" + grund + ")") +
                                  ", erwartet " + (soll ? "durchgelassen" : "abgelehnt"));
                fehler++;
            }

            // Die Gegenprobe gegen die Befehlstabelle des Spiels muss FEHLENDE Namen melden.
            // Die Attrappe kennt absichtlich nur vier Befehle.
            var log = new SammelLog();
            ConsolePolicy.PruefeGegenSpiel(GameBridge.GetConsoleCommandWords(), ConsolePolicy.Sicher, log);
            if (!log.Enthaelt("no longer exist"))
            { Console.WriteLine("       Gegenprobe meldet fehlende Befehle NICHT"); fehler++; }
            if (!log.Enthaelt("setjumpforce"))
            { Console.WriteLine("       Gegenprobe nennt den fehlenden Namen nicht"); fehler++; }

            // Und sie darf NICHT meckern, wenn das Spiel alles kennt - sonst ist sie Rauschen.
            var alle = new SammelLog();
            var vorher = GameBridge.ConsoleCommandWords;
            try
            {
                GameBridge.ConsoleCommandWords = new[]
                {
                    "setmovespeed","setjumpforce","setgravitymultiplier","freecam","showfps","hidefps",
                    "enableocclusionculling","disableocclusionculling","enableterrain","disableterrain",
                    "enableinstancing","disableinstancing","setemotion","triggerdistantthunder",
                };
                ConsolePolicy.PruefeGegenSpiel(GameBridge.GetConsoleCommandWords(), ConsolePolicy.Sicher, alle);
                if (alle.Enthaelt("no longer exist"))
                { Console.WriteLine("       Gegenprobe meldet Fehlendes, obwohl alles da ist"); fehler++; }
            }
            finally { GameBridge.ConsoleCommandWords = vorher; }

            Console.WriteLine(fehler == 0 ? "       OK   " + faelle.Length + " Faelle, bind gesperrt, Gegenprobe beidseitig"
                                          : "       " + fehler + " Fehler");
            return fehler;
        }

        /// <summary>Sammelt Logzeilen, damit ein Test ihren INHALT pruefen kann.</summary>
        private sealed class SammelLog : IScriptLog
        {
            private readonly System.Text.StringBuilder _b = new System.Text.StringBuilder();
            public void Info(string m)  { _b.AppendLine(m); }
            public void Warn(string m)  { _b.AppendLine(m); }
            public void Error(string m) { _b.AppendLine(m); }
            internal bool Enthaelt(string teil) { return _b.ToString().IndexOf(teil, StringComparison.Ordinal) >= 0; }
        }

        private static int Fall7_Referenz()
        {
            Console.WriteLine("[Fall 7] der Wirt schreibt seine eigene Referenz");
            var ordner = Path.Combine(Path.GetTempPath(), "scriptone_doc_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var fehler = 0;
            try
            {
                GameBridge.Submitted.Clear(); GameBridge.MoveSpeed = 1f; GameBridge.Ready = false;
                var e = new LuaEngine(new KonsolenLog(), Path.Combine(Path.GetTempPath(), "scriptone_state"), ordner);
                e.LoadFolder(ScriptDir);

                foreach (var n in new[] { "ScriptOne-API.md", "s1.lua", "README.txt" })
                {
                    if (File.Exists(Path.Combine(ordner, n))) continue;
                    Console.WriteLine("       FEHLT: " + n); fehler++;
                }
                if (fehler > 0) return fehler;

                var md = File.ReadAllText(Path.Combine(ordner, "ScriptOne-API.md"));
                var lua = File.ReadAllText(Path.Combine(ordner, "s1.lua"));

                // Kernfunktion - muss in JEDEM Spiel dastehen.
                if (md.IndexOf("`s1.on`", StringComparison.Ordinal) < 0)
                { Console.WriteLine("       Kern-API s1.on fehlt in der Referenz"); fehler++; }
                if (lua.IndexOf("function s1.on()", StringComparison.Ordinal) < 0)
                { Console.WriteLine("       Kern-API s1.on fehlt in den Stubs"); fehler++; }

                // Die ERZEUGTE Flaeche muss ebenfalls drinstehen - sonst prueft der Test nur
                // den Kern, und der Teil, der die Spieltabellen ablaeuft, bliebe ungeprueft.
                foreach (var erwartet in new[] { "`s1.fake_manager`", "`s1.fake_manager.do_something()`",
                                                 "`s1.fake_manager.a_value` - value" })
                    if (md.IndexOf(erwartet, StringComparison.Ordinal) < 0)
                    { Console.WriteLine("       fehlt in der Referenz: " + erwartet); fehler++; }

                if (lua.IndexOf("function s1.fake_manager.do_something() end", StringComparison.Ordinal) < 0)
                { Console.WriteLine("       erzeugte Tabelle fehlt in den Stubs"); fehler++; }

                if (md.IndexOf("tables: 1", StringComparison.Ordinal) < 0)
                { Console.WriteLine("       Kopfzahl stimmt nicht mit der Flaeche ueberein"); fehler++; }

                Console.WriteLine("       geschrieben: " + new FileInfo(Path.Combine(ordner, "ScriptOne-API.md")).Length +
                                  " B API, " + new FileInfo(Path.Combine(ordner, "s1.lua")).Length + " B Stubs");
            }
            finally
            {
                try { if (Directory.Exists(ordner)) Directory.Delete(ordner, true); } catch { }
            }
            Console.WriteLine(fehler == 0 ? "       OK" : "       " + fehler + " Fehler");
            return fehler;
        }

        private static int Fall6_ZeitUndGedaechtnis()
        {
            Console.WriteLine("[Fall 6] Zeitgeber und Gedaechtnis");
            var dir = Path.Combine(Path.GetTempPath(), "scriptone_probe_timer");
            var state = Path.Combine(Path.GetTempPath(), "scriptone_probe_state");
            if (Directory.Exists(state)) Directory.Delete(state, true);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "t.lua"), @"local runs = s1.get('runs', 0)
s1.on('game_ready', function()
  runs = runs + 1
  s1.set('runs', runs)
  s1.save()
  s1.after(0.05, function() s1.console('setmovespeed 4') end)
  s1.log('run number ' .. runs)
end)");

            // Erster Start
            GameBridge.Submitted.Clear(); GameBridge.MoveSpeed = 1f; GameBridge.Ready = false;
            var e1 = new LuaEngine(new KonsolenLog(), state);
            e1.LoadFolder(dir);
            GameBridge.Ready = true;
            e1.PollGameReady();

            // Zeitgeber ist noch nicht faellig
            e1.Tick();
            var vorFaellig = GameBridge.MoveSpeed;

            // warten, bis er faellig ist, dann ticken
            System.Threading.Thread.Sleep(120);
            e1.Tick();
            var nachFaellig = GameBridge.MoveSpeed;
            e1.Tick();  // darf NICHT erneut feuern (after = einmalig)
            var nachZweitemTick = GameBridge.Submitted.Count;

            // Zweiter Start: das Gedaechtnis muss den Zaehler behalten haben
            GameBridge.Ready = false;
            var e2 = new LuaEngine(new KonsolenLog(), state);
            e2.LoadFolder(dir);
            GameBridge.Ready = true;
            e2.PollGameReady();

            Directory.Delete(dir, true);
            Directory.Delete(state, true);

            Console.WriteLine("       Tempo vor Faelligkeit: " + vorFaellig.ToString(CultureInfo.InvariantCulture)
                              + " | danach: " + nachFaellig.ToString(CultureInfo.InvariantCulture));
            if (Math.Abs(vorFaellig - 1f) > 0.0001f) return Bad("Zeitgeber feuerte zu frueh");
            if (Math.Abs(nachFaellig - 4f) > 0.0001f) return Bad("Zeitgeber feuerte gar nicht");
            if (GameBridge.Submitted.Count != nachZweitemTick) return Bad("s1.after feuerte mehrfach");
            return Ok("after() feuerte genau einmal, Zaehler ueberlebte den Neustart (siehe 'run number 2')");
        }

        /// <summary>
        /// Der Bericht ueber eine STANDALONE-Installation, die ein spaeter installierter Lader
        /// stillgelegt hat.
        ///
        /// ⚠ WARUM ALS TESTFALL UND NICHT IM SPIEL GEMESSEN: der Zustand entsteht erst, wenn
        /// jemand nach ScriptOne noch BepInEx installiert. Im Spiel laesst sich deshalb nur die
        /// NEGATIVE Haelfte zeigen ("es gibt keine Standalone-Reste, also schweigt er"), und
        /// ein Pruefer, der nur die schweigende Haelfte sieht, kann einen Melder, der NIE etwas
        /// sagt, nicht von einem richtigen unterscheiden. Beide Haelften stehen deshalb hier.
        /// </summary>
        private static int Fall9_VerwaisterWirt()
        {
            Console.WriteLine("[Fall 9] verwaiste Standalone-Installation erkennen");
            var wurzel = Path.Combine(Path.GetTempPath(), "scriptone_verwaist_" + Guid.NewGuid().ToString("N"));
            try
            {
                var spiel   = Path.Combine(wurzel, "Spiel");
                var s1      = Path.Combine(spiel, "ScriptOne");
                var zustand = Path.Combine(s1, "state");
                var core    = Path.Combine(Path.Combine(s1, "core-runtime"), "net472");
                Directory.CreateDirectory(zustand);
                Directory.CreateDirectory(core);

                // (a) Gar keine Standalone-Installation -> nichts zu melden.
                if (VerwaisterWirt.Bericht(zustand) != null)
                    return Bad("meldete etwas, obwohl kein Preloader dalag");
                Console.WriteLine("  ok   ohne Preloader: schweigt");

                File.WriteAllText(Path.Combine(core, "ScriptOne.Preloader.dll"), "x");

                // (b) Preloader da, doorstop_config.ini zeigt auf UNS -> lebendig, nichts melden.
                var ini = Path.Combine(spiel, "doorstop_config.ini");
                var t = Path.DirectorySeparatorChar;
                File.WriteAllLines(ini, new[]
                {
                    "[General]", "enabled=true",
                    "target_assembly=ScriptOne" + t + "core-runtime" + t + "net472" + t + "ScriptOne.Preloader.dll",
                });
                if (VerwaisterWirt.Bericht(zustand) != null)
                    return Bad("meldete verwaist, obwohl doorstop_config.ini noch auf uns zeigt");
                Console.WriteLine("  ok   Ziel zeigt auf uns: schweigt");

                // (c) BepInEx hat die Datei ueberschrieben -> genau dafuer ist der Bericht da.
                File.WriteAllLines(ini, new[]
                {
                    "[General]", "enabled=true",
                    "target_assembly=BepInEx" + t + "core" + t + "BepInEx.Preloader.dll",
                });
                var b = VerwaisterWirt.Bericht(zustand);
                if (b == null) return Bad("meldete NICHTS, obwohl das Ziel auf BepInEx zeigt");
                if (b.IndexOf("BepInEx", StringComparison.Ordinal) < 0)
                    return Bad("der Bericht nennt das neue Ziel nicht: " + b);
                Console.WriteLine("  ok   Ziel auf BepInEx umgebogen: meldet, und nennt das neue Ziel");

                // (d) Die ini ganz weg -> ebenfalls tot.
                File.Delete(ini);
                if (VerwaisterWirt.Bericht(zustand) == null)
                    return Bad("meldete nichts, obwohl es gar keine doorstop_config.ini mehr gibt");
                Console.WriteLine("  ok   ohne doorstop_config.ini: meldet");

                return Ok("beide Haelften belegt - schweigt, wenn nichts ist, und meldet, wenn etwas ist");
            }
            finally
            {
                try { Directory.Delete(wurzel, true); } catch { }
            }
        }

        /// <summary>
        /// Die Taktwache. ⚠ BEIDE Haelften, und die zweite ist die wichtigere: eine Wache, die
        /// IMMER meldet, faellt sonst nicht auf - sie saehe im kaputten Fall genauso richtig aus.
        /// </summary>
        private static int Fall10_Taktwache()
        {
            Console.WriteLine("[Fall 10] Taktwache meldet einen toten Frame-Takt");
            var zeilen = new System.Collections.Generic.List<string>();
            Action<string> warn = m => zeilen.Add(m);

            // (a) Kein einziges Bild -> es MUSS gemeldet werden.
            TaktWache.Zuruecksetzen();
            zeilen.Clear();
            TaktWache.Schluss(warn, TaktWache.HinweisPlugin("BepInEx"));
            if (zeilen.Count == 0) return Bad("schwieg, obwohl kein einziges Bild kam");
            var text = string.Join(" ", zeilen.ToArray());
            if (text.IndexOf("NO FRAME TICK", StringComparison.Ordinal) < 0)
                return Bad("die Meldung nennt den Sachverhalt nicht: " + text);
            if (text.IndexOf("BepInEx", StringComparison.Ordinal) < 0)
                return Bad("der wirtsspezifische Hinweis fehlt: " + text);
            Console.WriteLine("  ok   ohne Bilder: meldet, nennt den Wirt");

            // (b) Ein Bild genuegt -> sie muss SCHWEIGEN.
            TaktWache.Zuruecksetzen();
            zeilen.Clear();
            TaktWache.Bild();
            TaktWache.Schluss(warn, TaktWache.HinweisPlugin("BepInEx"));
            if (zeilen.Count != 0) return Bad("meldete, obwohl ein Bild gezaehlt wurde: " + string.Join(" ", zeilen.ToArray()));
            Console.WriteLine("  ok   mit einem Bild: schweigt");

            // (c) Genau EINMAL, auch wenn beide Meldewege zuschlagen.
            TaktWache.Zuruecksetzen();
            zeilen.Clear();
            TaktWache.Schluss(warn, "H");
            var nachErstem = zeilen.Count;
            TaktWache.Schluss(warn, "H");
            if (zeilen.Count != nachErstem) return Bad("meldete ein zweites Mal");
            Console.WriteLine("  ok   zweiter Meldeweg wiederholt die Meldung nicht");

            // (d) Der Zeitgeber-Weg, mit kurzer Frist gemessen statt geglaubt.
            TaktWache.Zuruecksetzen();
            zeilen.Clear();
            TaktWache.Starte(warn, "H", 1);
            System.Threading.Thread.Sleep(2500);
            if (zeilen.Count == 0) return Bad("der Zeitgeber-Weg meldete nichts");
            Console.WriteLine("  ok   Zeitgeber-Weg feuert ebenfalls");

            TaktWache.Zuruecksetzen();
            return Ok("meldet ohne Bilder, schweigt mit, und genau einmal");
        }

        private static int Ok(string s) { Console.WriteLine("  OK   " + s); Console.WriteLine(); return 0; }
        private static int Bad(string s) { Console.WriteLine("  FAIL " + s); Console.WriteLine(); return 1; }
    }
}
