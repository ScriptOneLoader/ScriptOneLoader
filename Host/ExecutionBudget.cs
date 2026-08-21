using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;

namespace ScriptOne.Host
{
    /// <summary>Wird geworfen, wenn ein Skript sein Schrittbudget aufbraucht.</summary>
    internal sealed class ScriptBudgetExceededException : Exception
    {
        internal ScriptBudgetExceededException(long budget)
            : base("Script exceeded its execution budget of " + budget + " steps (endless loop?)")
        {
        }
    }

    /// <summary>
    /// Notbremse gegen Endlosschleifen im Skript.
    /// </summary>
    /// <remarks>
    /// MoonSharp 2.0.0 kennt KEINE Abbruchaktion - DebuggerAction.ActionType hat Run, StepIn,
    /// StepOver, Breakpoints usw., aber nichts zum Beenden. Der einzige Hebel ist, dass der
    /// Interpreter vor jedem Schritt IsPauseRequested() fragt. Ein Wurf von dort heraus beendet
    /// die Ausfuehrung.
    ///
    /// Gemessen (Probelauf, MoonSharp 2.0.0 / netstandard1.6 auf net6.0):
    ///   * "while true do i = i + 1 end" bricht nach 200.001 Schritten in 18 ms ab
    ///   * derselbe Script laeuft danach WEITER ("return 7*6" liefert 42) - der Wirt muss also
    ///     nicht neu aufgebaut werden
    ///   * Aufpreis gegenueber "ohne Debugger" nur 1,13x (47 ms -> 53 ms bei 200.000 Schleifen)
    /// Deshalb haengt der Haken dauerhaft und nicht nur bei Verdacht.
    /// </remarks>
    internal sealed class ExecutionBudget : IDebugger
    {
        private readonly long _maxSteps;
        private long _steps;

        internal ExecutionBudget(long maxSteps)
        {
            _maxSteps = maxSteps;
        }

        /// <summary>Vor jedem Aufruf ins Skript zuruecksetzen - das Budget gilt je Aufruf, nicht je Sitzung.</summary>
        internal void Reset()
        {
            _steps = 0;
        }

        internal long StepsUsed
        {
            get { return _steps; }
        }

        public bool IsPauseRequested()
        {
            if (++_steps > _maxSteps)
            {
                _steps = 0;                       // sonst wirft der naechste Schritt sofort wieder
                throw new ScriptBudgetExceededException(_maxSteps);
            }
            return false;
        }

        // Ab hier nur Pflichtteile der Schnittstelle - dieser "Debugger" debuggt nichts.
        public DebuggerCaps GetDebuggerCaps() { return DebuggerCaps.CanDebugSourceCode; }
        public void SetDebugService(DebugService debugService) { }
        public void SetSourceCode(SourceCode sourceCode) { }
        public void SetByteCode(string[] byteCode) { }
        public bool SignalRuntimeException(ScriptRuntimeException ex) { return false; }
        public DebuggerAction GetAction(int ilOffset, SourceRef sourceref)
        {
            return new DebuggerAction { Action = DebuggerAction.ActionType.Run };
        }
        public void Update(WatchType watchType, IEnumerable<WatchItem> items) { }
        public List<DynamicExpression> GetWatchItems() { return new List<DynamicExpression>(); }
        public void RefreshBreakpoints(IEnumerable<SourceRef> refs) { }
        public void SignalExecutionEnded() { }
    }
}
