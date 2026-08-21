using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ScriptOne.Host
{
    /// <summary>
    /// Zeitgeber fuer Skripte: s1.after(sec, fn) und s1.every(sec, fn).
    /// </summary>
    /// <remarks>
    /// ZEITQUELLE IST EINE STOPWATCH, NICHT DAS SPIEL. Zwei Gruende:
    ///   1. Das Spiel setzt beim Pausieren timeScale auf 0. Jede Zeitrechnung ueber
    ///      Time.time oder Time.deltaTime steht dann still - ein "alle 5 Sekunden"-Timer
    ///      feuert im Pausenmenue nie und holt danach womoeglich alles nach.
    ///   2. Eine Stopwatch braucht keinen Spieltyp. Damit bleibt dieser ganze Teil des
    ///      Wirts spielfrei uebersetzbar und im Pruefstand messbar.
    ///
    /// Die Ausfuehrung selbst laeuft NICHT hier, sondern ueber den Rueckruf, den LuaEngine
    /// uebergibt - nur dort liegen die Kulturklammer, das Schrittbudget und die
    /// Fehlerisolierung je Skript.
    /// </remarks>
    internal sealed class Timers
    {
        private sealed class Eintrag
        {
            internal int Id;
            internal double DueAt;        // Sekunden seit Start der Stopwatch
            internal double Interval;     // 0 = einmalig
            internal object Owner;        // das Skript, zur Fehlerisolierung
            internal object Handler;      // DynValue, hier absichtlich als object
            internal bool Cancelled;
        }

        /// <summary>Untergrenze, damit ein Skript den Wirt nicht mit 0-Sekunden-Timern flutet.</summary>
        internal const double MinInterval = 0.05;
        /// <summary>Obergrenze je Skript - ein Versehen in einer Schleife soll nicht unbegrenzt wachsen.</summary>
        internal const int MaxPerScript = 128;

        private readonly Stopwatch _uhr = Stopwatch.StartNew();
        private readonly List<Eintrag> _liste = new List<Eintrag>();
        private readonly List<Eintrag> _faellig = new List<Eintrag>();
        private int _naechsteId = 1;

        internal double Now { get { return _uhr.Elapsed.TotalSeconds; } }

        internal int Count { get { return _liste.Count; } }

        internal int CountFor(object owner)
        {
            var n = 0;
            for (var i = 0; i < _liste.Count; i++)
                if (ReferenceEquals(_liste[i].Owner, owner) && !_liste[i].Cancelled) n++;
            return n;
        }

        /// <summary>Legt einen Zeitgeber an. interval &lt;= 0 heisst einmalig.</summary>
        internal int Add(object owner, object handler, double delay, double interval)
        {
            if (delay < MinInterval) delay = MinInterval;
            if (interval > 0 && interval < MinInterval) interval = MinInterval;

            var e = new Eintrag
            {
                Id = _naechsteId++,
                DueAt = Now + delay,
                Interval = interval > 0 ? interval : 0,
                Owner = owner,
                Handler = handler,
            };
            _liste.Add(e);
            return e.Id;
        }

        internal bool Cancel(object owner, int id)
        {
            for (var i = 0; i < _liste.Count; i++)
            {
                var e = _liste[i];
                // Ein Skript darf NUR eigene Zeitgeber abbrechen - sonst koennte eine
                // durchgezaehlte Schleife die Timer eines fremden Skripts abraeumen.
                if (e.Id == id && ReferenceEquals(e.Owner, owner)) { e.Cancelled = true; return true; }
            }
            return false;
        }

        internal void CancelAll(object owner)
        {
            for (var i = 0; i < _liste.Count; i++)
                if (ReferenceEquals(_liste[i].Owner, owner)) _liste[i].Cancelled = true;
        }

        /// <summary>
        /// Faellige Zeitgeber ausfuehren. 'run' bekommt (owner, handler) und ist dafuer
        /// zustaendig, den Aufruf abzusichern.
        /// </summary>
        internal void Tick(Action<object, object> run)
        {
            if (_liste.Count == 0) return;
            var jetzt = Now;

            // Erst einsammeln, dann ausfuehren: ein Handler darf waehrend seines eigenen
            // Laufs Zeitgeber anlegen oder abbrechen, ohne die Schleife zu zerlegen.
            _faellig.Clear();
            for (var i = _liste.Count - 1; i >= 0; i--)
            {
                var e = _liste[i];
                if (e.Cancelled) { _liste.RemoveAt(i); continue; }
                if (e.DueAt > jetzt) continue;

                _faellig.Add(e);
                if (e.Interval > 0)
                {
                    // Auf JETZT aufsetzen, nicht auf die Sollzeit: nach einem Ladebildschirm
                    // waeren sonst hunderte Wiederholungen nachzuholen.
                    e.DueAt = jetzt + e.Interval;
                }
                else
                {
                    _liste.RemoveAt(i);
                }
            }

            for (var i = 0; i < _faellig.Count; i++)
            {
                var e = _faellig[i];
                if (!e.Cancelled) run(e.Owner, e.Handler);
            }
        }
    }
}
