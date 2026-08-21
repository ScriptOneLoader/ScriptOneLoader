using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace ScriptOne.Setup
{
    /// <summary>
    /// Der Installer mit Fenster - fuer Nutzer, die nichts entpacken und nichts tippen wollen.
    /// </summary>
    /// <remarks>
    /// ENTWURFSREGELN (die ersten vier sind aelter als die Optik und gelten weiter):
    ///  1. Der Zustand steht IMMER da, bevor ein Knopf etwas tut. Ein Installer, der erst nach
    ///     dem Klick sagt, was er vorgefunden hat, laesst den Nutzer raten.
    ///  2. Kein Knopf ist aktiv, wenn er nicht gehen kann - und daneben steht, warum.
    ///  3. Was passiert ist, steht im Protokollfeld, nicht in einem Meldungsfenster, das man
    ///     wegklickt und danach nicht mehr nachlesen kann.
    ///  4. Kein Designer, kein .resx: eine Datei, die man lesen kann.
    ///  5. Die Optik traegt die Aussage, sie ersetzt sie nicht: der Zustand steht als Text da
    ///     UND als Farbpunkt. Wer den Punkt nicht sieht (Farbfehlsichtigkeit, Screenshot in
    ///     Graustufen), verliert nichts.
    ///
    /// ⚠ KEINE externen Bilddateien. Alles gezeichnet oder aus Systemschriften - der Installer
    ///   bleibt eine einzelne exe, und genau das ist sein Zweck.
    ///
    /// Alle sichtbaren Texte ENGLISCH.
    /// </remarks>
    internal sealed class MainForm : Form
    {
        // Farben an EINER Stelle. Wer eine davon aendert, aendert sie ueberall.
        private static readonly Color Tinte      = Color.FromArgb(0x1B, 0x24, 0x30);
        private static readonly Color TinteMatt  = Color.FromArgb(0x5A, 0x66, 0x72);
        private static readonly Color Flaeche    = Color.FromArgb(0xF5, 0xF6, 0xF8);
        private static readonly Color Karte      = Color.White;
        private static readonly Color Linie      = Color.FromArgb(0xDD, 0xE1, 0xE6);
        private static readonly Color Akzent     = Color.FromArgb(0x1F, 0x6F, 0xEB);
        private static readonly Color Gut        = Color.FromArgb(0x1A, 0x7F, 0x37);
        private static readonly Color Warnung    = Color.FromArgb(0x9A, 0x67, 0x00);
        private static readonly Color Schlecht   = Color.FromArgb(0xB4, 0x23, 0x18);

        private readonly TextBox  _pfad      = new TextBox();
        private readonly Button   _blaettern = new Button();
        private readonly Button   _suchen    = new Button();
        private readonly ComboBox _gefunden  = new ComboBox();
        private readonly Zustandskarte _karte = new Zustandskarte();
        private readonly Label    _hinweis   = new Label();
        private readonly Button   _install   = new Button();
        private readonly Button   _alternativ = new Button();
        private readonly Button   _entfernen = new Button();
        private readonly TextBox  _log       = new TextBox();

        private bool _fuelleGerade;

        internal MainForm()
        {
            Text = "ScriptOne " + Anwendung.Version;
            ClientSize = new Size(780, 640);
            MinimumSize = new Size(720, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Flaeche;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            // ⚠ DAS FENSTERSYMBOL AUS DER EIGENEN EXE - dieselbe Datei, die der Explorer
            //   zeigt. Vorher wurde es zur Laufzeit gezeichnet: zwei Wege zum selben Zeichen,
            //   und das Fenster haette sich vom Dateisymbol wegentwickeln koennen. Der
            //   gezeichnete Weg bleibt als Rueckfall, falls die exe kein Symbol traegt.
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            if (Icon == null) Icon = Symbol.Bauen();

            Controls.Add(Kopfband());

            var y = 96;

            // Die Aussage, die eine Hervorhebung verdient - einmal, sichtbar, ausserhalb des
            // Protokolls.
            var koennen = new Label
            {
                Text = "Works under MelonLoader   ·   under BepInEx   ·   or with its own loader",
                AutoSize = false, ForeColor = Akzent
            };
            koennen.Font = new Font("Segoe UI Semibold", 9f);
            koennen.SetBounds(20, y, 740, 20);
            koennen.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(koennen);
            y += 28;

            Controls.Add(Ueberschrift("Game folder", 20, y)); y += 22;

            _pfad.SetBounds(20, y, 500, 26);
            _pfad.BorderStyle = BorderStyle.FixedSingle;
            _pfad.TextChanged += (s, e) => { if (!_fuelleGerade) Aktualisiere(); };
            Controls.Add(_pfad);

            Flach(_blaettern, "Browse...", 528, y - 1, 96, 28, false);
            _blaettern.Click += (s, e) => Blaettern();
            Controls.Add(_blaettern);

            Flach(_suchen, "Find games", 632, y - 1, 128, 28, false);
            _suchen.Click += (s, e) => Suchen();
            Controls.Add(_suchen);
            y += 36;

            _gefunden.SetBounds(20, y, 740, 26);
            _gefunden.DropDownStyle = ComboBoxStyle.DropDownList;
            _gefunden.FlatStyle = FlatStyle.Flat;
            _gefunden.Visible = false;
            _gefunden.SelectedIndexChanged += (s, e) =>
            {
                var f = _gefunden.SelectedItem as Fund;
                if (f == null) return;
                _fuelleGerade = true; _pfad.Text = f.Ordner; _fuelleGerade = false;
                Aktualisiere();
            };
            Controls.Add(_gefunden);
            y += 36;

            _karte.SetBounds(20, y, 740, 108);
            _karte.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_karte);
            y += 116;

            _hinweis.SetBounds(20, y, 740, 40);
            _hinweis.ForeColor = Warnung;
            _hinweis.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_hinweis);
            y += 46;

            // ⚠ EIN Knopf fuer den Normalfall. Standen beide Wege gleichrangig nebeneinander,
            //   musste der Nutzer wissen, welcher fuer SEINEN Ordner richtig ist - der falsche
            //   schaltet ihm alle anderen Mods ab. Der Hauptknopf beschriftet sich nach dem Befund.
            Flach(_install, "Install", 20, y, 240, 38, true);
            _install.Click += (s, e) => Lauf(Modus.Auto);
            Controls.Add(_install);

            Flach(_alternativ, "Install standalone instead", 270, y, 220, 38, false);
            _alternativ.Click += (s, e) => Lauf(Modus.Standalone);
            Controls.Add(_alternativ);

            Flach(_entfernen, "Remove", 500, y, 120, 38, false);
            _entfernen.Click += (s, e) => Lauf(Modus.Entfernen);
            Controls.Add(_entfernen);
            y += 48;

            Controls.Add(Ueberschrift("What happened", 20, y)); y += 22;

            _log.SetBounds(20, y, 740, ClientSize.Height - y - 20);
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Font = new Font("Consolas", 9f);
            _log.BackColor = Karte;
            _log.ForeColor = Tinte;
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(_log);

            // ⚠ DAS PROTOKOLL BEGINNT LEER. Hier standen zwei Saetze als Vorgabetext - eine
            //   Bedienanleitung und die Aussage, dass ScriptOne mit und ohne Lader laeuft. Der
            //   Autor hat das zu Recht als Muell im Log bezeichnet: ein Protokoll zeigt, WAS
            //   PASSIERT IST. Steht dort schon etwas, bevor etwas passiert ist, muss man beim
            //   Lesen erst trennen. Die Aussage selbst ist eine Hervorhebung wert - sie steht
            //   jetzt als Zeile im Kopfbereich, nicht im Protokoll.
            // ⚠ NUR PRUEFEN, NICHT AUSPACKEN. Beim Start hat niemand etwas installiert;
            //   Dateien nach %TEMP% zu schreiben waere hier eine Nebenwirkung ohne Anlass.
            Nutzlast.Pruefen(Sag);
            Aktualisiere();
        }

        // ------------------------------------------------------------------ Optik

        /// <summary>Das dunkle Band oben: Name, Version, ein Satz - und ein gezeichnetes Zeichen.</summary>
        private Control Kopfband()
        {
            var band = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Tinte };
            band.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                // Ein Verlauf von links nach rechts - dezent, damit es nicht nach Werbung aussieht.
                using (var b = new LinearGradientBrush(band.ClientRectangle,
                           Tinte, Color.FromArgb(0x2A, 0x3A, 0x4E), LinearGradientMode.Horizontal))
                    g.FillRectangle(b, band.ClientRectangle);

                Symbol.Zeichne(g, new Rectangle(22, 20, 44, 44));

                using (var f1 = new Font("Segoe UI Semibold", 15f))
                using (var f2 = new Font("Segoe UI", 9f))
                {
                    g.DrawString("ScriptOne", f1, Brushes.White, 80, 18);
                    var breite = g.MeasureString("ScriptOne", f1).Width;
                    g.DrawString("v" + Anwendung.Version, f2,
                                 new SolidBrush(Color.FromArgb(0x9F, 0xB0, 0xC4)), 82 + breite, 27);
                    g.DrawString("Lua scripting for Unity games by Virtunerd", f2,
                                 new SolidBrush(Color.FromArgb(0xB8, 0xC6, 0xD6)), 82, 46);
                }
            };
            return band;
        }

        private Label Ueberschrift(string t, int x, int y)
        {
            var l = new Label { Text = t.ToUpperInvariant(), AutoSize = true, ForeColor = TinteMatt };
            l.Font = new Font("Segoe UI Semibold", 8f);
            l.SetBounds(x, y, 200, 16);
            return l;
        }

        /// <summary>Flache Knoepfe - Windows-Standardknoepfe sehen neben dem Kopfband fremd aus.</summary>
        private static void Flach(Button b, string text, int x, int y, int w, int h, bool betont)
        {
            b.SetBounds(x, y, w, h);
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            b.FlatAppearance.BorderSize = 1;
            if (betont)
            {
                b.BackColor = Akzent;
                b.ForeColor = Color.White;
                b.Font = new Font("Segoe UI Semibold", 9.5f);
                b.FlatAppearance.BorderColor = Akzent;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x18, 0x5E, 0xCC);
            }
            else
            {
                b.BackColor = Karte;
                b.ForeColor = Tinte;
                b.FlatAppearance.BorderColor = Linie;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xEC, 0xEF, 0xF3);
            }
            b.EnabledChanged += (s, e) =>
            {
                if (!betont) return;
                b.BackColor = b.Enabled ? Akzent : Color.FromArgb(0xB9, 0xC6, 0xD8);
                b.FlatAppearance.BorderColor = b.BackColor;
            };
        }

        /// <summary>
        /// Die Zustandskarte: vier Zeilen, je ein Farbpunkt und Klartext.
        /// </summary>
        /// <remarks>
        /// Der Punkt ist Beiwerk. Die Aussage steht im Text - sonst haengt die Information an
        /// der Farbwahrnehmung des Lesers.
        /// </remarks>
        private sealed class Zustandskarte : Panel
        {
            internal sealed class Zeile
            {
                internal string Was;
                internal string Wert;
                internal Color Farbe;
            }

            private Zeile[] _zeilen = new Zeile[0];

            internal Zustandskarte()
            {
                BackColor = Karte;
                DoubleBuffered = true;
            }

            internal void Setze(Zeile[] z) { _zeilen = z ?? new Zeile[0]; Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var p = new Pen(Linie))
                    g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

                if (_zeilen.Length == 0)
                {
                    using (var f = new Font("Segoe UI", 9f))
                        g.DrawString("No folder picked yet.", f, new SolidBrush(TinteMatt), 16, 16);
                    return;
                }

                var y = 14;
                using (var fWas = new Font("Segoe UI", 9f))
                using (var fWert = new Font("Segoe UI Semibold", 9f))
                {
                    foreach (var z in _zeilen)
                    {
                        using (var b = new SolidBrush(z.Farbe))
                            g.FillEllipse(b, 16, y + 5, 9, 9);
                        g.DrawString(z.Was, fWas, new SolidBrush(TinteMatt), 36, y);
                        g.DrawString(z.Wert, fWert, new SolidBrush(Tinte), 150, y);
                        y += 23;
                    }
                }
            }
        }

        /// <summary>Ein gezeichnetes Zeichen statt einer Bilddatei - geschweifte Klammern.</summary>
        private static class Symbol
        {
            internal static void Zeichne(Graphics g, Rectangle r)
            {
                using (var p = new Pen(Color.FromArgb(0x6E, 0xA8, 0xFF), 3f))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    var x = r.Left; var y = r.Top; var w = r.Width; var h = r.Height;
                    // { auf der linken, } auf der rechten Seite - das Zeichen fuer "Skript".
                    g.DrawArc(p, x + 4, y + 2, 14, 18, 100, 160);
                    g.DrawArc(p, x + 4, y + h - 20, 14, 18, 100, 160);
                    g.DrawArc(p, x + w - 18, y + 2, 14, 18, -80, 160);
                    g.DrawArc(p, x + w - 18, y + h - 20, 14, 18, -80, 160);
                }
                using (var b = new SolidBrush(Color.FromArgb(0xB8, 0xC6, 0xD6)))
                using (var f = new Font("Consolas", 11f, FontStyle.Bold))
                    g.DrawString("Lua", f, b, r.Left + 8, r.Top + r.Height / 2 - 10);
            }

            internal static Icon Bauen()
            {
                using (var bmp = new Bitmap(32, 32))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Tinte);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using (var p = new Pen(Color.FromArgb(0x6E, 0xA8, 0xFF), 2f))
                        {
                            g.DrawArc(p, 5, 4, 10, 12, 100, 160);
                            g.DrawArc(p, 5, 16, 10, 12, 100, 160);
                            g.DrawArc(p, 17, 4, 10, 12, -80, 160);
                            g.DrawArc(p, 17, 16, 10, 12, -80, 160);
                        }
                    }
                    return Icon.FromHandle(bmp.GetHicon());
                }
            }
        }

        // ------------------------------------------------------------------ Bedienung

        private void Blaettern()
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "Pick the game folder - the one that contains the game's .exe";
                if (Directory.Exists(_pfad.Text)) d.SelectedPath = _pfad.Text;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _fuelleGerade = true; _pfad.Text = d.SelectedPath; _fuelleGerade = false;
                Aktualisiere();
            }
        }

        private void Suchen()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                var f = SteamScan.Finde();
                _gefunden.Items.Clear();
                foreach (var x in f) _gefunden.Items.Add(x);
                _gefunden.Visible = f.Count > 0;
                // ⚠ "nichts gefunden" ist KEIN Fehler - Spiele ausserhalb von Steam gibt es.
                Sag(f.Count > 0
                    ? "Found " + f.Count + " Unity game(s) in your Steam libraries. Pick one, or use Browse."
                    : "No Unity games found in the Steam libraries. That is not an error - use Browse for "
                      + "anything installed outside Steam.");
            }
            finally { Cursor = Cursors.Default; }
        }

        private void Aktualisiere()
        {
            var p = _pfad.Text.Trim().Trim('"');
            if (string.IsNullOrEmpty(p) || !Directory.Exists(p))
            {
                _karte.Setze(null);
                _hinweis.Text = "";
                _install.Enabled = _alternativ.Enabled = _entfernen.Enabled = false;
                return;
            }

            var b = GameDetect.Untersuche(p);
            var unity = b.Backend != Backend.Unbekannt;

            var loader = LaderText(b);
            var loaderFarbe = b.Melon == MelonZustand.Aktiv ? Gut
                            : b.Melon == MelonZustand.Kaputt ? Schlecht
                            // ⚠ Der VERKETTETE Zustand ist gesund, nicht orange: nach der
                            //   Uebernahme ist BepInExAktiv definitionsgemaess false (die
                            //   Konfiguration zeigt auf uns), und die Warnfarbe war fuer
                            //   "Ordner ohne Lader" gedacht. Daneben stand dann
                            //   "BepInEx x.y (started by ScriptOne)" in Orange.
                            : b.BepVonUnsGestartet ? Gut
                            : b.BepInEx && !b.BepInExAktiv ? Warnung : TinteMatt;

            _karte.Setze(new[]
            {
                new Zustandskarte.Zeile { Was = "Unity backend", Wert = b.BackendText + "  (" + b.ArchText + ")",
                                          Farbe = unity ? Gut : Schlecht },
                new Zustandskarte.Zeile { Was = "Mod loader",    Wert = loader, Farbe = loaderFarbe },
                new Zustandskarte.Zeile { Was = "ScriptOne",
                                          Wert = b.StandaloneDa ? "installed (standalone)"
                                               : b.PluginDa     ? "installed (" + b.PluginArt + " plugin)"
                                                                : "not installed",
                                          Farbe = (b.StandaloneDa || b.PluginDa) ? Gut : TinteMatt },
                new Zustandskarte.Zeile { Was = Schon(b) ? "Will update" : "Will install as",
                                          Wert = !unity ? "-" : WegText(b),
                                          Farbe = unity ? Akzent : TinteMatt },
            });

            _entfernen.Enabled  = unity;
            _install.Enabled    = unity;
            // Die Ausweichwahl gibt es nur, wenn sie sich vom Hauptknopf UNTERSCHEIDET.
            _alternativ.Enabled = unity && Installer.WaehleModus(b) != Modus.Standalone;
            _install.Text       = !unity ? "Install" : KnopfText(b);

            var h = "";
            if (!unity)
                h = "This does not look like a Unity game folder - no GameAssembly.dll and no _Data\\Managed\\Assembly-CSharp.dll.";
            else if (b.ZweiLader)
                h = "MelonLoader AND another Doorstop loader are installed here. Both replace the same entry in "
                  + "UnityPlayer.dll and neither chains the other - exactly one of them runs, silently.";
            else if (b.BepInExAktiv)
                h = "";   // Kein Warnton: Auto geht hier den BepInEx-Weg, es gibt nichts zu warnen.
            else if (b.FremdesDoorstop)
                h = "A foreign Doorstop is installed here. Installing standalone would overwrite it.";
            else if (b.Melon == MelonZustand.Kaputt)
                // ⚠ Der Satz stand hier als Ferndiagnose und war eine: derselbe Anblick entsteht
                //   bei einer voellig gesunden Installation ueber r2modman oder den Thunderstore
                //   Mod Manager, die MelonLoader im PROFIL halten und mit
                //   --melonloader.basedir starten. Im Spielordner bleibt dann genau version.dll.
                //   Was man SIEHT, gehoert in die Meldung; was es BEDEUTET, weiss nur der Nutzer.
                h = "version.dll is here, but no MelonLoader folder next to it. That is either a "
                  + "broken install - or a mod manager keeping MelonLoader in its profile.";
            else if (b.Backend == Backend.Il2Cpp && GameDetect.FindeCoreClr() == null)
                h = "No .NET 6 runtime found - an Il2Cpp game needs one. Install the .NET 6 Desktop Runtime first.";
            _hinweis.Text = h;
        }

        /// <summary>Der Weg im Klartext - beide Texte kommen aus Installer.WaehleModus.</summary>
        /// <summary>
        /// Liegt hier schon eine Installation? ⚠ Das entscheidet ueber die BESCHRIFTUNG, und die
        /// war falsch: bei einer vorhandenen BepInEx-Installation stand dort weiterhin "Will
        /// install as" und auf dem Knopf "Install as BepInEx plugin" - waehrend die DLL im Ordner
        /// lag. Wer eine Software fragt, ob sie installiert ist, und "nein" liest, obwohl er die
        /// Datei sieht, glaubt danach keiner ihrer Aussagen mehr. (Gemeldet 2026-08-20.)
        /// </summary>
        private static bool Schon(Befund b) { return b.StandaloneDa || b.PluginDa; }

        private static string WegText(Befund b)
        {
            switch (Installer.WaehleModus(b))
            {
                case Modus.Plugin:    return "MelonLoader plugin (your other mods keep working)";
                // ⚠ NICHT "BepInEx plugin". Genau die Falle, die der Klassenkopf von WaehleModus
                //   fuer den 2026-08-19 beschreibt, nur andersherum: der Lauf UEBERNIMMT hier den
                //   Einstiegspunkt und meldet danach "runs on its own here and starts BepInEx
                //   afterwards", waehrend die Karte "BepInEx plugin" versprach. Der Plugin-Weg ist
                //   seit dem 2026-08-20 nur noch der Rueckfall.
                case Modus.BepPlugin: return "standalone host, and it starts BepInEx itself "
                                           + "(your other plugins keep working)";
                default:              return "standalone (ScriptOne brings its own loader)";
            }
        }

        /// <summary>
        /// Wie der gefundene Lader benannt wird. ⚠ EINE Quelle fuer Fenster UND --status: die
        /// Kommandozeile baute sich diese Zeile sonst selbst zusammen und sagte dann
        /// "not installed | BepInEx present" ueber einen Ordner, den das Fenster im selben
        /// Moment als "BepInEx 5.4.23.5 (active)" auswies. Zwei Formulierungen fuer denselben
        /// Zustand sind zwei Wahrheiten, und der Nutzer glaubt danach keiner mehr.
        /// </summary>
        internal static string LaderText(Befund b)
        {
            return b.Melon == MelonZustand.Aktiv
                     ? "MelonLoader " + (b.MelonFassung == null ? "" : b.MelonFassung.ToString()) + " (active)"
                 : b.BepInExAktiv ? "BepInEx " + (b.BepFassung ?? "") + " (active)"
                 : b.BepVonUnsGestartet ? "BepInEx " + (b.BepFassung ?? "") + " (started by ScriptOne)"
                 : b.BepInEx      ? "BepInEx folder, but its loader is not installed"
                 : b.Melon != MelonZustand.NichtInstalliert ? b.MelonText
                 : "none";
        }

        private static string KnopfText(Befund b)
        {
            // Derselbe Weg, andere Aussage: "Install" verspricht etwas Neues, "Update" sagt die
            // Wahrheit ueber einen Ordner, in dem die DLL schon liegt. Der Lauf dahinter ist
            // unveraendert - der Installer ueberschreibt ohnehin und ist wiederholbar.
            var vor = Schon(b) ? "Update" : "Install";
            switch (Installer.WaehleModus(b))
            {
                case Modus.Plugin:    return vor + " as MelonLoader plugin";
                case Modus.BepPlugin: return vor + " (standalone, keeps BepInEx)";
                default:              return vor + " (standalone)";
            }
        }

        private void Lauf(Modus m)
        {
            var p = _pfad.Text.Trim().Trim('"');
            var b = GameDetect.Untersuche(p);

            if (m == Modus.Standalone && b.Melon == MelonZustand.Aktiv)
            {
                var a = MessageBox.Show(this,
                    "MelonLoader is active in this folder.\n\n" +
                    "Both it and the standalone loader replace the same import entry in UnityPlayer.dll, " +
                    "and neither chains the other - exactly one survives, silently.\n\n" +
                    "Installing standalone switches MelonLoader OFF. It is set aside, not deleted, and " +
                    "'Remove' brings it back.\n\nContinue?",
                    "MelonLoader is active", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (a != DialogResult.Yes) { Sag("cancelled - nothing was changed."); return; }
            }

            // WARNUNG: HIER FEHLTEN ZWEI RUECKFRAGEN, und weil der Aufruf darunter
            //   'trotzdem: true' uebergibt, waren die zugehoerigen Sperren im Installer damit
            //   WIRKUNGSLOS - das Fenster fragte nur bei aktivem MelonLoader. Wer "Install
            //   standalone instead" drueckte, ueberschrieb ein fremdes BepInEx-Doorstop ohne
            //   ein Wort. Das Fenster DARF die Sperre uebergehen, aber nur, weil es fragt -
            //   also muss es auch fragen.
            // ⚠ UND NACH DER UEBERNAHME IST FremdesDoorstop FALSE. Es ist definiert als
            //   doorDa && BepInEx && !StandaloneDa - nach der Verkettung ist StandaloneDa aber
            //   wahr, also erschien KEINE Rueckfrage mehr, waehrend der Knopf aktiv blieb und
            //   Lauf() mit trotzdem:true aufruft. Standalone() haette dann BepInEx' eigene
            //   winhttp.dll per VermerkeProxy als UNSERE vermerkt, und das folgende --remove
            //   haette sie geloescht. Also beide Zustaende fragen.
            if (m == Modus.Standalone && (b.FremdesDoorstop || b.BepVonUnsGestartet))
            {
                var aD = MessageBox.Show(this,
                    (b.BepVonUnsGestartet
                       ? "ScriptOne currently owns the entry point here and starts BepInEx itself." + Environment.NewLine + Environment.NewLine +
                         "Installing standalone the plain way would claim BepInEx's own winhttp.dll as " +
                         "ScriptOne's. A later 'Remove' would then DELETE it and leave BepInEx without " +
                         "a loader." + Environment.NewLine + Environment.NewLine +
                         "There is nothing to gain here - the normal install already runs ScriptOne as its " +
                         "own host." + Environment.NewLine + Environment.NewLine + "Continue anyway?"
                       : "A FOREIGN Doorstop is installed here - BepInEx uses the same winhttp.dll." + Environment.NewLine + Environment.NewLine +
                         "Installing standalone OVERWRITES it. That BepInEx installation stops working, " +
                         "and ScriptOne cannot put it back." + Environment.NewLine + Environment.NewLine + "Continue?"),
                    "A foreign loader is installed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (aD != DialogResult.Yes) { Sag("cancelled - nothing was changed."); return; }
            }

            if (m == Modus.Standalone && b.Melon == MelonZustand.Kaputt)
            {
                var aM = MessageBox.Show(this,
                    "version.dll is here, but there is no MelonLoader folder next to it." + Environment.NewLine + Environment.NewLine +
                    "That is ALSO what a mod manager looks like: r2modman and the Thunderstore " +
                    "Mod Manager keep MelonLoader inside their profile and start the game with " +
                    "--melonloader.basedir. In the game folder only version.dll stays behind." + Environment.NewLine + Environment.NewLine +
                    "Installing standalone would move that file aside - and silently disable " +
                    "every mod in that profile." + Environment.NewLine + Environment.NewLine + "Continue anyway?",
                    "This may be a mod manager", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (aM != DialogResult.Yes) { Sag("cancelled - nothing was changed."); return; }
            }

            Sag("");
            Sag("--- " + m.ToString().ToLowerInvariant() + " ---");
            Cursor = Cursors.WaitCursor;
            Ergebnis e;
            // ⚠ HIER wird ausgepackt, nicht beim Start: erst dieser Klick ist ein Anlass.
            //   Beim Entfernen braucht es den Beipack nicht - der Zweig kommt ohne aus.
            if (m != Modus.Entfernen) Nutzlast.Bereitstellen(Sag);
            try { e = Installer.Fuehre(p, m, true); }
            catch (Exception ex)
            {
                Sag("FAILED: " + ex.GetType().Name + ": " + ex.Message);
                Sag("Nothing is left in a state that stops the game: delete " + Pfade.DoorstopDll +
                    " and " + Pfade.DoorstopCfg + " from the game folder.");
                Cursor = Cursors.Default;
                Aktualisiere();
                return;
            }
            Cursor = Cursors.Default;

            foreach (var z in e.Zeilen) Sag(z);
            if (!e.Erfolg) Sag("STOPPED: " + e.Abbruch);
            else if (m != Modus.Entfernen)
            {
                Sag("Done. Now start the game once.");
                Sag("If ScriptOne comes up, it writes what your scripts can call in THIS game to "
                    + Pfade.Wurzel + "\\" + Pfade.Doku + "\\.");
                Sag("Put your .lua files into " + Pfade.SkriptOrdner + "\\.");
                Sag("If nothing appears, the reason is in " + (e.Gewaehlt == Modus.Plugin
                        ? Pfade.MelonOrdner + "\\Latest.log" : Pfade.Wurzel + "\\" + Pfade.LogDatei) + ".");
            }
            else Sag("Done.");
            Aktualisiere();
        }

        private void Sag(string z)
        {
            _log.AppendText(z + Environment.NewLine);
        }
    }

    internal static class Anwendung
    {
        internal static string Version
        {
            get
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "" : v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            // ⚠ DIESELBE EXE, ZWEI BEDIENUNGEN. Mit Argumenten laeuft sie STILL und schreibt
            //   nach stdout - ohne Fenster. Grund ist nicht Bequemlichkeit: der Paketpruefer
            //   muss das AUSGELIEFERTE Artefakt pruefen koennen. Vorher tat er das ueber die
            //   Konsolenfassung in der ZIP; faellt die weg, pruefte er etwas, das niemand
            //   bekommt. Eine exe, zwei Wege, eine Wahrheit.
            if (args != null && args.Length > 0) return Still(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        private static int Still(string[] args)
        {
            var spiel = "";
            var modus = Modus.Auto;
            var trotzdem = false;
            var nurStatus = false;
            // ⚠ MelonLoaders Plugins\/UserLibs\ liegen nicht zwangslaeufig im Spielordner -
            //   r2modman & Co. lenken die Basis per --melonloader.basedir um. Ohne diese Angabe
            //   schreibt der Installer in einen Ordner, aus dem der Loader nie liest.
            string melonBasis = null;
            foreach (var a in args)
            {
                var l = a.ToLowerInvariant();
                if (l == "--remove" || l == "-r")           modus = Modus.Entfernen;
                else if (l == "--standalone" || l == "-s")  modus = Modus.Standalone;
                else if (l == "--plugin" || l == "-p")      modus = Modus.Plugin;
                else if (l == "--quiet" || l == "-q")       { }
                else if (l == "--force" || l == "-f")       trotzdem = true;
                else if (l == "--status")                   nurStatus = true;
                else if (l.StartsWith("--melonloader-basedir=")) melonBasis = a.Substring(a.IndexOf('=') + 1).Trim('"');
                else if (l == "--help" || l == "-h" || l == "/?")
                {
                    Console.WriteLine("ScriptOne " + Version + " - installer");
                    Console.WriteLine("  <path>        game folder (required in silent mode)");
                    Console.WriteLine("  --standalone  force the standalone host");
                    Console.WriteLine("  --force       proceed even when a foreign loader is in the way");
                    Console.WriteLine("  --melonloader-basedir=<folder>");
                    Console.WriteLine("                where MelonLoader keeps Plugins and UserLibs - a mod");
                    Console.WriteLine("                manager (r2modman) puts them in its PROFILE, not the game folder");
                    Console.WriteLine("  --plugin      force the loader plugin");
                    Console.WriteLine("  --status      only report what is there, change nothing");
                    Console.WriteLine("  --remove      remove ScriptOne completely");
                    Console.WriteLine("  (no arguments opens the window)");
                    return 0;
                }
                else if (Directory.Exists(a)) spiel = a.TrimEnd(Path.DirectorySeparatorChar);
                else if (a.Length > 0 && (a[0] == '-' || a[0] == '/'))
                {
                    // ⚠ EIN UNBEKANNTES FLAG DARF NICHT INSTALLIEREN. Hier fiel jede
                    //   unbekannte Angabe stillschweigend durch, und der Lauf machte danach
                    //   das, was OHNE sie passiert waere - bei einem Installer also: er
                    //   installierte. Gemessen mit "--status", das es damals noch nicht gab:
                    //   der Aufruf sollte nur nachsehen und hat installiert. Ein Tippfehler
                    //   im Flag haette dieselbe Wirkung gehabt.
                    Console.Error.WriteLine("unknown option: " + a + " - see --help");
                    return 2;
                }
            }
            if (string.IsNullOrEmpty(spiel))
            {
                Console.Error.WriteLine("silent mode needs a game folder - see --help");
                return 2;
            }

            // ⚠ NUR NACHSEHEN, BEVOR IRGENDETWAS BEREITGESTELLT WIRD. Der Status muss den
            //   Zustand melden, den er VORFINDET - nicht den, den er selbst herstellt.
            if (nurStatus)
            {
                var bs = GameDetect.Untersuche(spiel, melonBasis);
                Console.WriteLine("  Game folder : " + spiel);
                Console.WriteLine("  Backend     : " + bs.BackendText + "  (" + bs.ArchText + ")");
                Console.WriteLine("  Mod loader  : " + MainForm.LaderText(bs));
                Console.WriteLine("  ScriptOne   : standalone " + (bs.StandaloneDa ? "yes" : "no") +
                                  " | plugin " + (bs.PluginDa ? bs.PluginArt.ToLowerInvariant() : "no"));
                if (bs.PluginDa) Console.WriteLine("                found at " + bs.PluginPfad);
                return 0;
            }

            // Auspacken erst hier - dieser Zweig installiert wirklich. '--status' und
            // '--remove' kommen gar nicht bis hierher und schreiben deshalb nichts.
            Nutzlast.Bereitstellen(Console.WriteLine);
            Ergebnis e;
            // WARNUNG: HIER STAND 'true'. Der stille Modus uebersprang damit JEDE Schutzabfrage
            //   des Installers - fremdes Doorstop ueberschreiben, aktives MelonLoader beiseite
            //   schieben, eine Mod-Manager-Installation abschalten - und zwar OHNE Rueckfrage,
            //   weil es im stillen Modus keine gibt. Das Fenster darf 'true' uebergeben, WEIL es
            //   vorher fragt; die Kommandozeile hat niemanden zu fragen und muss deshalb
            //   anhalten. Wer es trotzdem will, sagt --force.
            try { e = Installer.Fuehre(spiel, modus, trotzdem, melonBasis); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAILED: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
            foreach (var z in e.Zeilen) Console.WriteLine(z);
            if (!e.Erfolg) { Console.Error.WriteLine("STOPPED: " + e.Abbruch); return 1; }
            Console.WriteLine("Done.");
            return 0;
        }
    }
}
