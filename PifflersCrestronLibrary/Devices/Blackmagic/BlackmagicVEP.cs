using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Crestron.SimplSharp.CrestronSockets;
using PifflersCrestronLibrary.Communication;
using PifflersCrestronLibrary.Logger;

namespace PifflersCrestronLibrary.Devices.Blackmagic
{
    /// <summary>
    /// Blackmagic Videohub Ethernet Protocol (VEP, v2.3) ueber TCP 9990.
    /// Funktioniert mit Videohubs und Geraeten, die das Protokoll emulieren
    /// (z.B. ATEM Mini Extreme ISO G2 fuer seine Aux-/HDMI-Outputs).
    ///
    /// Protokoll: textbasiert, Bloecke "HEADER:" + Zeilen + Leerzeile. Nach dem
    /// Connect schickt der Server einen kompletten Status-Dump, danach nur Diffs.
    /// Ports sind im Protokoll 0-basiert — die Public-API dieses Treibers ist
    /// <b>1-basiert</b> (= Beschriftung am Geraet), umgerechnet wird nur hier.
    ///
    /// Routing wird NICHT optimistisch gesetzt: der State kommt ausschliesslich aus
    /// den Status-Updates des Servers (so verlangt es die Spec — Requests koennen von
    /// anderen Clients ueberholt werden).
    /// </summary>
    public class BlackmagicVEP : BasicTCP
    {
        public const int DefaultPort = 9990;

        private const string HdrPreamble = "PROTOCOL PREAMBLE:";
        private const string HdrDevice = "VIDEOHUB DEVICE:";
        private const string HdrInputLabels = "INPUT LABELS:";
        private const string HdrOutputLabels = "OUTPUT LABELS:";
        private const string HdrRouting = "VIDEO OUTPUT ROUTING:";

        private static readonly Regex IndexValueLine = new Regex(@"^(\d+)\s+(.*)$");
        private static readonly Regex KeyValueLine = new Regex(@"^([^:]+):\s*(.*)$");

        private readonly object rxLock = new object();
        private readonly StringBuilder rxBuffer = new StringBuilder();
        private string currentHeader;
        private readonly List<string> currentBlockLines = new List<string>();

        private readonly Dictionary<int, int> routeByOutput = new Dictionary<int, int>();       // out -> in (1-basiert)
        private readonly Dictionary<int, string> inputLabels = new Dictionary<int, string>();   // 1-basiert
        private readonly Dictionary<int, string> outputLabels = new Dictionary<int, string>();  // 1-basiert

        private string lastRequest;

        public string ProtocolVersion { get; private set; }
        public bool DevicePresent { get; private set; }
        public string ModelName { get; private set; }
        public int InputCount { get; private set; }
        public int OutputCount { get; private set; }
        public string LastError { get; private set; }

        public delegate void RouteChangedDelegate(int output, int input);
        /// <summary>Feuert bei jedem Routing-Update vom Server (Dump + Diffs), 1-basiert.</summary>
        public event RouteChangedDelegate RouteChanged;

        public BlackmagicVEP(string host, string friendlyName = "BlackmagicVEP", int port = DefaultPort)
            : base(host, friendlyName, port)
        {
            client.SocketStatusChange += OnSocketStatusChangeInternal;
        }

        // ---------- Public API ---------------------------------------------

        /// <summary>Routet Input auf Output (beide 1-basiert).</summary>
        public bool Route(int input, int output)
        {
            if (!ValidatePort(input, InputCount, "Input") || !ValidatePort(output, OutputCount, "Output"))
                return false;
            return SendBlock(HdrRouting, (output - 1) + " " + (input - 1));
        }

        /// <summary>Letzter vom Server gemeldeter Input fuer den Output, 0 = unbekannt.</summary>
        public int GetRoute(int output)
        {
            int input;
            return TryGetRoute(output, out input) ? input : 0;
        }

        public bool TryGetRoute(int output, out int input)
        {
            lock (rxLock) return routeByOutput.TryGetValue(output, out input);
        }

        public string GetInputLabel(int input)
        {
            string label;
            lock (rxLock) return inputLabels.TryGetValue(input, out label) ? label : string.Empty;
        }

        public string GetOutputLabel(int output)
        {
            string label;
            lock (rxLock) return outputLabels.TryGetValue(output, out label) ? label : string.Empty;
        }

        /// <summary>Fordert Labels + Routing komplett neu an (z.B. nach Verdacht auf verpasste Updates).</summary>
        public void RequestStatusDump()
        {
            SendBlock(HdrInputLabels);
            SendBlock(HdrOutputLabels);
            SendBlock(HdrRouting);
        }

        // ---------- BasicTCP-Overrides -------------------------------------

        protected override void InitializeRegexDictionary()
        {
            // Kein Regex-Dispatch: Bloecke koennen ueber mehrere Receive-Chunks verteilt
            // ankommen, daher eigener Zeilenpuffer in ProcessFeedback.
        }

        protected override void KeepAliveCallback(object _)
        {
            SendBlock("PING:");
        }

        protected override void ProcessFeedback(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            lock (rxLock)
            {
                rxBuffer.Append(message);
                var data = rxBuffer.ToString();
                var lastNl = data.LastIndexOf('\n');
                if (lastNl < 0) return;

                rxBuffer.Length = 0;
                rxBuffer.Append(data.Substring(lastNl + 1));

                foreach (var raw in data.Substring(0, lastNl).Split('\n'))
                    HandleLine(raw.TrimEnd('\r'));
            }
        }

        // ---------- Parser -------------------------------------------------

        private void HandleLine(string line)
        {
            if (currentHeader == null)
            {
                if (line.Length == 0) return;
                if (line == "ACK") { lastRequest = null; return; }
                if (line == "NAK")
                {
                    LastError = "NAK auf: " + (lastRequest ?? "?");
                    Debug.Warn("[VEP] [" + friendlyName + "] " + LastError);
                    RaiseDataEvent("Error");
                    lastRequest = null;
                    return;
                }
                if (line.EndsWith(":"))
                {
                    currentHeader = line;
                    currentBlockLines.Clear();
                }
                // alles andere ausserhalb eines Blocks: laut Spec ignorieren
                return;
            }

            if (line.Length == 0)
            {
                var header = currentHeader;
                currentHeader = null;
                HandleBlock(header, currentBlockLines);
                currentBlockLines.Clear();
                return;
            }

            currentBlockLines.Add(line);
        }

        private void HandleBlock(string header, List<string> lines)
        {
            switch (header)
            {
                case HdrPreamble:
                    foreach (var l in lines)
                    {
                        var kv = KeyValueLine.Match(l);
                        if (kv.Success && kv.Groups[1].Value.Trim() == "Version") ProtocolVersion = kv.Groups[2].Value.Trim();
                    }
                    Debug.Log("[VEP] [" + friendlyName + "] Protokoll-Version " + ProtocolVersion);
                    break;

                case HdrDevice:
                    HandleDeviceBlock(lines);
                    break;

                case HdrInputLabels:
                    HandleLabels(lines, inputLabels, "In");
                    RaiseDataEvent("InputLabels");
                    break;

                case HdrOutputLabels:
                    HandleLabels(lines, outputLabels, "Out");
                    RaiseDataEvent("OutputLabels");
                    break;

                case HdrRouting:
                    HandleRouting(lines);
                    break;

                // Locks, Monitoring, Serial, Hardware-Status: fuer uns irrelevant -> ignorieren.
            }
        }

        private void HandleDeviceBlock(List<string> lines)
        {
            foreach (var l in lines)
            {
                var kv = KeyValueLine.Match(l);
                if (!kv.Success) continue;
                var key = kv.Groups[1].Value.Trim();
                var value = kv.Groups[2].Value.Trim();
                int n;
                switch (key)
                {
                    case "Device present": DevicePresent = value == "true"; break;
                    case "Model name": ModelName = value; break;
                    case "Video inputs": if (int.TryParse(value, out n)) InputCount = n; break;
                    case "Video outputs": if (int.TryParse(value, out n)) OutputCount = n; break;
                }
            }

            if (DevicePresent)
                Debug.Log("[VEP] [" + friendlyName + "] Geraet: '" + ModelName + "', " + InputCount + " In / " + OutputCount + " Out");
            else
                Debug.Warn("[VEP] [" + friendlyName + "] kein Geraet bzw. Firmware-Update noetig (Device present != true)");
            RaiseDataEvent("DeviceInfo");
        }

        private void HandleLabels(List<string> lines, Dictionary<int, string> target, string prefix)
        {
            foreach (var l in lines)
            {
                var m = IndexValueLine.Match(l);
                if (!m.Success) continue;
                var port = int.Parse(m.Groups[1].Value) + 1;
                target[port] = m.Groups[2].Value;
                // Bewusst geloggt: vor Ort sieht man so direkt die Port-Nummern fuer die config.json.
                Debug.Log("[VEP] [" + friendlyName + "] " + prefix + " " + port + " = '" + target[port] + "'");
            }
        }

        private void HandleRouting(List<string> lines)
        {
            foreach (var l in lines)
            {
                var m = IndexValueLine.Match(l);
                int outIdx, inIdx;
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out outIdx) || !int.TryParse(m.Groups[2].Value.Trim(), out inIdx))
                    continue;

                var output = outIdx + 1;
                var input = inIdx + 1;
                routeByOutput[output] = input;
                Debug.Log("[VEP] [" + friendlyName + "] Route Out " + output + " <- In " + input);

                var h = RouteChanged;
                if (h != null)
                {
                    try { h(output, input); }
                    catch (Exception e) { Debug.Error("[VEP] [" + friendlyName + "] RouteChanged-Handler: " + e.Message); }
                }
            }
            RaiseDataEvent("Route");
        }

        // ---------- intern -------------------------------------------------

        private bool SendBlock(string header, params string[] lines)
        {
            if (!Connected)
            {
                Debug.Warn("[VEP] [" + friendlyName + "] nicht verbunden, verworfen: " + header);
                return false;
            }

            var sb = new StringBuilder(header).Append('\n');
            foreach (var l in lines) sb.Append(l).Append('\n');
            sb.Append('\n');

            lastRequest = header + (lines.Length > 0 ? " " + string.Join(" | ", lines) : string.Empty);
            SendRaw(sb.ToString());
            return true;
        }

        private bool ValidatePort(int port, int count, string what)
        {
            if (port < 1 || (count > 0 && port > count))
            {
                Debug.Warn("[VEP] [" + friendlyName + "] " + what + " " + port + " ausserhalb 1.." + (count > 0 ? count.ToString() : "?"));
                return false;
            }
            return true;
        }

        private void OnSocketStatusChangeInternal(TCPClient _, SocketStatus status)
        {
            if (status == SocketStatus.SOCKET_STATUS_CONNECTED) return;
            // Halbe Bloecke einer abgebrochenen Verbindung nicht in die naechste mitschleppen.
            lock (rxLock)
            {
                rxBuffer.Length = 0;
                currentHeader = null;
                currentBlockLines.Clear();
            }
        }
    }
}
