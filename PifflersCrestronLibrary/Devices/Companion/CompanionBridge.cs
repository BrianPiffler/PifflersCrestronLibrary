using System;
using System.Collections.Generic;
using System.Text;
using PifflersCrestronLibrary.Communication;
using PifflersCrestronLibrary.Logger;

namespace PifflersCrestronLibrary.Devices.Companion
{
    /// <summary>
    /// TCP-Bridge fuer Bitfocus Companion (Streamdeck) und aehnliche einfache TCP-Controller.
    /// Sitzt auf <see cref="BasicTCPServer"/> und bietet ein Subscribe-Pattern:
    ///
    ///   RegisterCommand("mute", args =&gt; dsp.Mute(bool.Parse(args[0])));
    ///   PublishState("mic1.mute", "true");   // an alle Clients broadcasten
    ///
    /// Zeilenprotokoll (UTF-8, LF- oder CRLF-terminiert):
    ///   Eingehend:  <c>CMD:&lt;name&gt;[:&lt;arg1&gt;,&lt;arg2&gt;,...]</c>
    ///   Ausgehend:  <c>STATE:&lt;key&gt;:&lt;value&gt;</c>
    ///               <c>OK:&lt;name&gt;</c>        (bei erfolgreichem Kommando)
    ///               <c>ERR:&lt;name&gt;:&lt;reason&gt;</c>
    ///   Meta-Command <c>CMD:LIST</c>  ->  je registriertem Kommando eine Zeile
    ///               <c>CMDLIST:&lt;name&gt;</c>
    ///               <c>END:CMDLIST</c>
    ///   Meta-Command <c>CMD:STATES</c> -> gecachte States nachliefern (<c>STATE:&lt;key&gt;:&lt;value&gt;</c>)
    ///               <c>END:STATES</c>
    ///
    /// Bei jedem Neuanschluss werden LIST + STATES automatisch an den neuen Client geschickt,
    /// damit Companion sich orientieren und Button-LEDs sofort korrekt setzen kann.
    /// </summary>
    public class CompanionBridge : BasicTCPServer
    {
        private readonly object handlersLock = new object();
        private readonly Dictionary<string, Action<string[]>> handlers =
            new Dictionary<string, Action<string[]>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> stateCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Roh-Puffer je Client fuer Zeilen-Reassembly.
        private readonly Dictionary<uint, StringBuilder> rxBuffers = new Dictionary<uint, StringBuilder>();

        public CompanionBridge(int port, string friendlyName = "Companion", int maxClients = 4)
            : base(port, friendlyName, maxClients)
        {
            ClientConnected += OnClientConnected;
            ClientDisconnected += OnClientDisconnected;
            DataReceived += OnDataReceived;
        }

        // --- Public API ---------------------------------------------------------

        /// <summary>Registriert einen Handler fuer <c>CMD:&lt;name&gt;</c>. Namen case-insensitiv.</summary>
        public void RegisterCommand(string name, Action<string[]> handler)
        {
            if (string.IsNullOrWhiteSpace(name) || handler == null) return;
            lock (handlersLock) handlers[name.Trim()] = handler;
            Debug.Log("[Companion] [" + friendlyName + "] command registered: " + name);
        }

        /// <summary>Entfernt einen Kommando-Handler.</summary>
        public void UnregisterCommand(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            lock (handlersLock) handlers.Remove(name.Trim());
        }

        /// <summary>Publiziert einen State an alle verbundenen Clients (und cached ihn fuer neu anschliessende).</summary>
        public void PublishState(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            var v = value ?? string.Empty;
            lock (handlersLock) stateCache[key] = v;
            Broadcast("STATE:" + key + ":" + v + "\n");
        }

        /// <summary>Convenience: bool-State als "true"/"false".</summary>
        public void PublishState(string key, bool value) { PublishState(key, value ? "true" : "false"); }

        /// <summary>Convenience: numerischer State.</summary>
        public void PublishState(string key, int value) { PublishState(key, value.ToString()); }

        // --- Client-Handling ----------------------------------------------------

        private void OnClientConnected(uint clientIndex, string address)
        {
            lock (rxBuffers) rxBuffers[clientIndex] = new StringBuilder();
            // Neuen Client mit LIST + STATES abholen, damit UI sofort synchron ist.
            SendCommandList(clientIndex);
            SendStateSnapshot(clientIndex);
        }

        private void OnClientDisconnected(uint clientIndex)
        {
            lock (rxBuffers) rxBuffers.Remove(clientIndex);
        }

        private void OnDataReceived(uint clientIndex, byte[] data, int length)
        {
            StringBuilder buf;
            lock (rxBuffers)
            {
                if (!rxBuffers.TryGetValue(clientIndex, out buf))
                {
                    buf = new StringBuilder();
                    rxBuffers[clientIndex] = buf;
                }
            }

            buf.Append(Encoding.UTF8.GetString(data, 0, length));

            // Zeilenweise verarbeiten.
            while (true)
            {
                var current = buf.ToString();
                var nl = current.IndexOf('\n');
                if (nl < 0) break;

                var line = current.Substring(0, nl).TrimEnd('\r').Trim();
                buf.Remove(0, nl + 1);
                if (line.Length == 0) continue;
                HandleLine(clientIndex, line);
            }
        }

        private void HandleLine(uint clientIndex, string line)
        {
            if (!line.StartsWith("CMD:", StringComparison.OrdinalIgnoreCase))
            {
                SendToClient(clientIndex, "ERR::not a CMD line\n");
                return;
            }

            // CMD:name[:arg,arg,...]
            var rest = line.Substring(4);
            string name;
            string[] args;
            var colon = rest.IndexOf(':');
            if (colon < 0)
            {
                name = rest.Trim();
                args = Array.Empty<string>();
            }
            else
            {
                name = rest.Substring(0, colon).Trim();
                var argstr = rest.Substring(colon + 1);
                args = argstr.Length == 0 ? Array.Empty<string>() : argstr.Split(',');
            }

            // Meta-Commands
            if (name.Equals("LIST", StringComparison.OrdinalIgnoreCase)) { SendCommandList(clientIndex); return; }
            if (name.Equals("STATES", StringComparison.OrdinalIgnoreCase)) { SendStateSnapshot(clientIndex); return; }

            Action<string[]> handler;
            lock (handlersLock) handlers.TryGetValue(name, out handler);

            if (handler == null)
            {
                SendToClient(clientIndex, "ERR:" + name + ":unknown command\n");
                Debug.Log("[Companion] [" + friendlyName + "] unknown command from client " + clientIndex + ": " + name);
                return;
            }

            try
            {
                handler(args);
                SendToClient(clientIndex, "OK:" + name + "\n");
            }
            catch (Exception e)
            {
                SendToClient(clientIndex, "ERR:" + name + ":" + e.Message.Replace('\n', ' ') + "\n");
                Debug.Error("[Companion] [" + friendlyName + "] handler '" + name + "' threw: " + e.Message);
            }
        }

        private void SendCommandList(uint clientIndex)
        {
            var sb = new StringBuilder();
            lock (handlersLock)
            {
                foreach (var name in handlers.Keys) sb.Append("CMDLIST:").Append(name).Append('\n');
            }
            sb.Append("END:CMDLIST\n");
            SendToClient(clientIndex, sb.ToString());
        }

        private void SendStateSnapshot(uint clientIndex)
        {
            var sb = new StringBuilder();
            lock (handlersLock)
            {
                foreach (var kv in stateCache) sb.Append("STATE:").Append(kv.Key).Append(':').Append(kv.Value).Append('\n');
            }
            sb.Append("END:STATES\n");
            SendToClient(clientIndex, sb.ToString());
        }
    }
}
