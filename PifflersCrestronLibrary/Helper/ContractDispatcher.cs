using System;
using System.Collections.Generic;
using Crestron.SimplSharpPro;
using Crestron.SimplSharpPro.DeviceSupport;
using PifflersCrestronLibrary.Logger;

namespace PifflersCrestronLibrary.Helper
{
    /// <summary>
    /// Duenner Dispatcher zwischen einem <see cref="BasicTriListWithSmartObject"/> und
    /// Handler-Delegates, die per Signal-Name (aus der Contract-Codegen-Datei
    /// <c>Signals.g.cs</c>) registriert werden. Ersetzt die vom Crestron Contract Editor
    /// generierten <c>*.g.cs</c>-Klassen mit einem projektneutralen Runtime-Dispatch.
    ///
    /// Verwendung im Controller:
    ///   var dispatcher = new ContractDispatcher(_tp);
    ///   dispatcher.OnPress(Signals.System.TechnikBeschallungToggle_press, () =&gt; ...);
    ///   dispatcher.OnUShort(Signals.Mikrofone.Taschen1Level_set, v =&gt; _dsp.SetLevel(1, v));
    ///   dispatcher.SetBool(Signals.System.TechnikBeschallung_fb, true); // Feedback
    ///
    /// Voraussetzung: Panel + Controller nutzen ein SmartObject-basiertes CH5-Contract
    /// (dann liefert <c>args.Sig.Name</c> den vollen Signal-Namen).
    /// </summary>
    public class ContractDispatcher
    {
        private readonly BasicTriListWithSmartObject tp;

        private readonly Dictionary<string, Action<bool>> boolHandlers =
            new Dictionary<string, Action<bool>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Action<ushort>> ushortHandlers =
            new Dictionary<string, Action<ushort>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Action<string>> stringHandlers =
            new Dictionary<string, Action<string>>(StringComparer.Ordinal);

        public ContractDispatcher(BasicTriListWithSmartObject touchpanel)
        {
            if (touchpanel == null) throw new ArgumentNullException("touchpanel");
            tp = touchpanel;
            tp.SigChange += OnSigChange;
        }

        // --- Handler-Registrierung ---------------------------------------------

        /// <summary>Reagiert auf Boolean-Puls (nur bei True-Flanke = Button-Press).</summary>
        public void OnPress(string signalName, Action handler)
        {
            if (string.IsNullOrEmpty(signalName) || handler == null) return;
            boolHandlers[signalName] = value => { if (value) handler(); };
        }

        /// <summary>Reagiert auf beliebige Bool-Aenderungen (True und False).</summary>
        public void OnBool(string signalName, Action<bool> handler)
        {
            if (string.IsNullOrEmpty(signalName) || handler == null) return;
            boolHandlers[signalName] = handler;
        }

        public void OnUShort(string signalName, Action<ushort> handler)
        {
            if (string.IsNullOrEmpty(signalName) || handler == null) return;
            ushortHandlers[signalName] = handler;
        }

        public void OnString(string signalName, Action<string> handler)
        {
            if (string.IsNullOrEmpty(signalName) || handler == null) return;
            stringHandlers[signalName] = handler;
        }

        // --- Feedback / Set --------------------------------------------------

        public void SetBool(string signalName, bool value)
        {
            if (!tp.BooleanInput.Contains(signalName)) { Debug.Warn("[Contract] Boolean-Feedback-Signal nicht gefunden: " + signalName); return; }
            tp.BooleanInput[signalName].BoolValue = value;
        }

        public void SetUShort(string signalName, ushort value)
        {
            if (!tp.UShortInput.Contains(signalName)) { Debug.Warn("[Contract] UShort-Feedback-Signal nicht gefunden: " + signalName); return; }
            tp.UShortInput[signalName].UShortValue = value;
        }

        public void SetString(string signalName, string value)
        {
            if (!tp.StringInput.Contains(signalName)) { Debug.Warn("[Contract] String-Feedback-Signal nicht gefunden: " + signalName); return; }
            tp.StringInput[signalName].StringValue = value ?? string.Empty;
        }

        // --- Intern -----------------------------------------------------------

        private void OnSigChange(BasicTriList dev, SigEventArgs args)
        {
            try
            {
                var name = args.Sig.Name;
                if (string.IsNullOrEmpty(name)) return;

                switch (args.Sig.Type)
                {
                    case eSigType.Bool:
                        Action<bool> b;
                        if (boolHandlers.TryGetValue(name, out b)) b(args.Sig.BoolValue);
                        break;
                    case eSigType.UShort:
                        Action<ushort> u;
                        if (ushortHandlers.TryGetValue(name, out u)) u(args.Sig.UShortValue);
                        break;
                    case eSigType.String:
                        Action<string> s;
                        if (stringHandlers.TryGetValue(name, out s)) s(args.Sig.StringValue);
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.Error("[Contract] Handler-Exception fuer '" + args.Sig.Name + "': " + e.Message);
            }
        }

        // Feedback-Setter arbeiten direkt auf tp.*Input[i] und benoetigen keine
        // Zwischenklassen — die SDK-typenkommen mit unterschiedlichen Namen je Version.
    }
}
