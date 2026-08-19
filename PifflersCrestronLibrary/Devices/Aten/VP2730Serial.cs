using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro;
using PifflersCrestronLibrary.Logger;

namespace PifflersCrestronLibrary.Devices.Aten
{
    /// <summary>
    /// Aten VP2730 (Video-Matrix) ueber RS-232.
    ///
    /// Wird an einem CP4-COM-Port gefahren (9600, 8N1, kein Flow-Control,
    /// Terminator CR+LF). Referenz: APIs/vp2730_rs232_commands.md.
    ///
    /// Public-API deckungsgleich zu <see cref="VP2730"/>, damit der
    /// <c>VideoMatrixController</c> ohne aendern der Aufrufer-Schnittstelle
    /// zwischen REST und Serial umschaltbar ist.
    ///
    /// Video-Blank/Mute pro Output gibt es serial nicht (nur Audio-Mute).
    /// <see cref="SetMute"/> mappt daher auf Freeze — mit einer einmaligen
    /// Warn-Ausgabe je Instanz, damit das im Log sichtbar bleibt.
    /// </summary>
    public class VP2730Serial : IDisposable
    {
        private readonly ComPort port;
        private readonly string friendlyName;

        private readonly Dictionary<string, int> lastInputByOutput = new Dictionary<string, int>();
        private readonly Dictionary<string, bool> lastFreezeByOutput = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> lastMuteByOutput = new Dictionary<string, bool>();

        private readonly object rxLock = new object();
        private string rxBuffer = string.Empty;
        private bool warnedAboutMuteAlias;

        // Send-Rate-Limit: das VP2730 quittiert bei zu schnellem Nachfeuern mit
        // "Command interval < 300ms" und **verwirft** das Kommando. Wir queuen alle
        // Sends und feuern sie im Timer-Takt >=320ms nacheinander ab.
        private const int MinIntervalMs = 320;
        private readonly object txLock = new object();
        private readonly Queue<string> txQueue = new Queue<string>();
        private CTimer txTimer;

        public delegate void RouteChangedDelegate(string outputId, int inputId);
        public event RouteChangedDelegate RouteChanged;

        public delegate void OutputBoolChangedDelegate(string outputId, bool value);
        public event OutputBoolChangedDelegate FreezeChanged;
        public event OutputBoolChangedDelegate MuteChanged;

        public bool Connected { get; private set; }

        public VP2730Serial(ComPort port, string friendlyName = "VP2730-Serial")
        {
            if (port == null) throw new ArgumentNullException("port");
            this.port = port;
            this.friendlyName = string.IsNullOrWhiteSpace(friendlyName) ? "VP2730-Serial" : friendlyName;

            Debug.Log("[VP2730-Serial] [" + this.friendlyName + "] created");
        }

        // ---------- Lifecycle ----------------------------------------------

        public bool Connect()
        {
            try
            {
                if (!port.Registered)
                {
                    var reg = port.Register();
                    if (reg != eDeviceRegistrationUnRegistrationResponse.Success)
                    {
                        Debug.Error("[VP2730-Serial] [" + friendlyName + "] Register fehlgeschlagen: " + reg);
                        return false;
                    }
                }

                port.SetComPortSpec(
                    ComPort.eComBaudRates.ComspecBaudRate9600,
                    ComPort.eComDataBits.ComspecDataBits8,
                    ComPort.eComParityType.ComspecParityNone,
                    ComPort.eComStopBits.ComspecStopBits1,
                    ComPort.eComProtocolType.ComspecProtocolRS232,
                    ComPort.eComHardwareHandshakeType.ComspecHardwareHandshakeNone,
                    ComPort.eComSoftwareHandshakeType.ComspecSoftwareHandshakeNone,
                    false);

                port.SerialDataReceived -= OnSerialDataReceived;
                port.SerialDataReceived += OnSerialDataReceived;

                Connected = true;
                Debug.Log("[VP2730-Serial] [" + friendlyName + "] verbunden auf COM" + port.ID);

                // Send-Queue starten (Rate-Limit 300ms des VP2730 respektieren)
                if (txTimer == null)
                    txTimer = new CTimer(OnTxTick, null, MinIntervalMs, MinIntervalMs);
                else
                    txTimer.Reset(MinIntervalMs, MinIntervalMs);

                // Direkt einmal Status ziehen, damit lastInput/... aufsteht.
                Send("read");
                return true;
            }
            catch (Exception e)
            {
                Debug.Error("[VP2730-Serial] [" + friendlyName + "] Connect Exception: " + e.Message);
                Connected = false;
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                port.SerialDataReceived -= OnSerialDataReceived;
            }
            catch { }
            if (txTimer != null) { txTimer.Stop(); txTimer.Dispose(); txTimer = null; }
            lock (txLock) txQueue.Clear();
            Connected = false;
        }

        public void Dispose()
        {
            Disconnect();
        }

        // ---------- Public API (identisch zu VP2730-REST) -------------------

        public bool Route(int inputId, string outputId)
        {
            if (!ValidateOutputId(outputId) || !ValidateInputId(inputId)) return false;
            var ok = Send("sw o" + Two(outputId) + " i" + Two(inputId));
            if (ok)
            {
                lastInputByOutput[outputId] = inputId;
                var h = RouteChanged; if (h != null) h(outputId, inputId);
            }
            return ok;
        }

        public bool SetFreeze(string outputId, bool on)
        {
            if (!ValidateOutputId(outputId)) return false;
            var ok = Send("freeze o" + Two(outputId) + (on ? " on" : " off"));
            if (ok) UpdateBool(outputId, on, lastFreezeByOutput, FreezeChanged);
            return ok;
        }

        /// <summary>
        /// Video-Mute (Blank) am VP2730: seriell **nicht** unterstuetzt (kein Blank-Kommando).
        /// Im Projekt Pausenhalle wird Video-Mute per PJLink-AV-Mute auf den Beamer geroutet.
        /// Methode bleibt aus API-Kompatibilitaet erhalten und loggt einmalig eine Warnung.
        /// </summary>
        public bool SetMute(string outputId, bool on)
        {
            if (!warnedAboutMuteAlias)
            {
                Debug.Warn("[VP2730-Serial] [" + friendlyName + "] SetMute() ignoriert — VP2730 hat seriell kein Video-Blank. Auf PJLink-AVMT umleiten.");
                warnedAboutMuteAlias = true;
            }
            return false;
        }

        public int  GetLastInput(string outputId)  { int v;  return lastInputByOutput.TryGetValue(outputId, out v)  ? v : 0; }
        public bool GetLastFreeze(string outputId) { bool v; return lastFreezeByOutput.TryGetValue(outputId, out v) && v; }
        public bool GetLastMute(string outputId)   { bool v; return lastMuteByOutput.TryGetValue(outputId, out v)   && v; }

        // ---------- Serial IO -----------------------------------------------

        /// <summary>Reiht ein Kommando in die 320-ms-getaktete Send-Queue ein.</summary>
        private bool Send(string command)
        {
            if (!Connected) { Debug.Warn("[VP2730-Serial] [" + friendlyName + "] Send: nicht verbunden"); return false; }
            lock (txLock) txQueue.Enqueue(command);
            return true;
        }

        private void OnTxTick(object _)
        {
            string next = null;
            lock (txLock)
            {
                if (txQueue.Count == 0) return;
                next = txQueue.Dequeue();
            }
            try
            {
                port.Send(next + "\r\n");
                Debug.Log("[VP2730-Serial] [" + friendlyName + "] TX: " + next);
            }
            catch (Exception e)
            {
                Debug.Error("[VP2730-Serial] [" + friendlyName + "] TX Exception: " + e.Message);
            }
        }

        private void OnSerialDataReceived(ComPort port, ComPortSerialDataEventArgs args)
        {
            if (args == null || string.IsNullOrEmpty(args.SerialData)) return;
            string chunk = args.SerialData;

            List<string> lines = null;
            lock (rxLock)
            {
                rxBuffer += chunk;
                int idx;
                while ((idx = rxBuffer.IndexOfAny(new[] { '\r', '\n' })) >= 0)
                {
                    var line = rxBuffer.Substring(0, idx);
                    // ueberspringe alle nachfolgenden CR/LF
                    while (idx < rxBuffer.Length && (rxBuffer[idx] == '\r' || rxBuffer[idx] == '\n')) idx++;
                    rxBuffer = rxBuffer.Substring(idx);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        if (lines == null) lines = new List<string>();
                        lines.Add(line);
                    }
                }
            }
            if (lines != null)
                foreach (var l in lines) ProcessLine(l.Trim());
        }

        private void ProcessLine(string line)
        {
            Debug.Log("[VP2730-Serial] [" + friendlyName + "] RX: " + line);

            // Route-State: sowohl das Echo eines `sw`-Kommandos ("sw o01 i02") als auch
            // die Zeilen der `read`-Antwort ("o01 i01") sollen den Cache aktualisieren.
            var sw = Regex.Match(line, @"\bo(\d+)\s+i(\d+)\b", RegexOptions.IgnoreCase);
            if (sw.Success)
            {
                int outN, inN;
                if (int.TryParse(sw.Groups[1].Value, out outN) && int.TryParse(sw.Groups[2].Value, out inN))
                {
                    var outId = outN.ToString();
                    lastInputByOutput[outId] = inN;
                    var h = RouteChanged; if (h != null) h(outId, inN);
                }
                return;
            }

            var fz = Regex.Match(line, @"\bfreeze\s+o(\d+)\s+(on|off)\b", RegexOptions.IgnoreCase);
            if (fz.Success)
            {
                int outN;
                if (int.TryParse(fz.Groups[1].Value, out outN))
                {
                    var outId = outN.ToString();
                    var on = fz.Groups[2].Value.Equals("on", StringComparison.OrdinalIgnoreCase);
                    UpdateBool(outId, on, lastFreezeByOutput, FreezeChanged);
                }
                return;
            }

            // `read`-Antwort enthaelt u.a. Input->Output-Assignments.
            // Beispielzeilen wie "Output A: Input 2" — Aten formatiert die Ausgabe
            // vielfaeltig; wir parsen konservativ und lassen unbekanntes stehen.
            var assign = Regex.Match(line, @"Output\s+([ABCabc\d]+)\s*:\s*Input\s+(\d+)", RegexOptions.IgnoreCase);
            if (assign.Success)
            {
                var outLabel = assign.Groups[1].Value.ToUpperInvariant();
                var outId = LabelToOutputId(outLabel);
                int inN;
                if (outId != null && int.TryParse(assign.Groups[2].Value, out inN))
                {
                    lastInputByOutput[outId] = inN;
                    var h = RouteChanged; if (h != null) h(outId, inN);
                }
            }
        }

        // Aten spricht in der Read-Antwort von "Output A/B/C". Wir mappen auf
        // die numerische ID (1..3), passend zur config-Vergabe im Controller.
        private static string LabelToOutputId(string label)
        {
            switch (label)
            {
                case "A": case "1": return "1";
                case "B": case "2": return "2";
                case "C": case "3": return "3";
                default: return null;
            }
        }

        private void UpdateBool(string outputId, bool newVal, Dictionary<string, bool> cache, OutputBoolChangedDelegate ev)
        {
            bool prev; var known = cache.TryGetValue(outputId, out prev);
            if (!known || prev != newVal)
            {
                cache[outputId] = newVal;
                if (ev != null) ev(outputId, newVal);
            }
        }

        // ---------- Validation / Helpers -----------------------------------

        private static bool ValidateOutputId(string outputId)
        {
            int n;
            if (int.TryParse(outputId, out n) && n >= 1 && n <= 3) return true;
            Debug.Warn("[VP2730-Serial] outputId '" + outputId + "' ausserhalb 1..3");
            return false;
        }

        private static bool ValidateInputId(int inputId)
        {
            if (inputId >= 1 && inputId <= 7) return true;
            Debug.Warn("[VP2730-Serial] inputId " + inputId + " ausserhalb 1..7");
            return false;
        }

        private static string Two(int n) { return n.ToString("D2"); }
        private static string Two(string s) { int n; return int.TryParse(s, out n) ? n.ToString("D2") : s; }
    }
}
