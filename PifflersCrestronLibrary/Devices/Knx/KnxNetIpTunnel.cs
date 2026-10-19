using System;
using System.Collections.Generic;
using Crestron.SimplSharp;
using PifflersCrestronLibrary.Communication;
using PifflersCrestronLibrary.Logger;

namespace PifflersCrestronLibrary.Devices.Knx
{
    /// <summary>
    /// KNXnet/IP-Tunnelling-Client (UDP 3671, KNX Standard 03.08.04) fuer Gruppen-Telegramme.
    /// Funktioniert mit jeder KNXnet/IP-Schnittstelle, z.B. Crestron CI-KNX (die kann
    /// Tunnelling v1 — damit braucht es weder das Crestron-SIMPL+-Modul noch eine
    /// ETS-Objekt-Konfiguration der CI-KNX; es werden direkt Gruppenadressen beschrieben).
    ///
    /// NAT-Modus (HPAI 0.0.0.0:0): das Gateway antwortet an Absender-IP/-Port, daher
    /// muss <c>localPort</c> fest gebunden sein.
    ///
    /// Sende-Regel: pro Kanal immer nur EIN offenes Telegramm. Die Queue wartet erst auf das
    /// TUNNELLING_ACK (Timeout 1 s, ein Retry) und dann auf das L_Data.con vom Bus (Timeout 3 s).
    /// Nur auf das ACK zu warten reicht nicht: die CI-KNX verwirft ein direkt folgendes
    /// Telegramm, solange das vorherige noch nicht auf dem Bus bestaetigt ist (Feld 2026-10-15:
    /// Szene Saal+Buehne -> Buehne kam nie an).
    /// </summary>
    public class KnxNetIpTunnel : BasicUDP
    {
        public const int DefaultPort = 3671;

        // KNXnet/IP Service-Typen
        private const ushort SvcConnectReq = 0x0205, SvcConnectRes = 0x0206;
        private const ushort SvcStateReq = 0x0207, SvcStateRes = 0x0208;
        private const ushort SvcDisconnectReq = 0x0209, SvcDisconnectRes = 0x020A;
        private const ushort SvcTunnelReq = 0x0420, SvcTunnelAck = 0x0421;

        // cEMI Message-Codes
        private const byte CemiLDataReq = 0x11, CemiLDataCon = 0x2E, CemiLDataInd = 0x29;

        private const int HeartbeatMs = 60000;      // Spec: Connectionstate alle 60 s
        private const int HeartbeatTimeoutMs = 10000;
        private const int ReconnectMs = 5000;
        private const int AckTimeoutMs = 1000;
        private const int ConTimeoutMs = 3000;

        private static readonly byte[] NatHpai = { 0x08, 0x01, 0, 0, 0, 0, 0, 0 };

        private readonly object sync = new object();
        private readonly Queue<byte[]> txQueue = new Queue<byte[]>();   // cEMI-Frames
        private byte[] inFlight;
        private int inFlightRetries;
        private byte channelId;
        private byte txSeq;
        private int lastRxSeq = -1;
        private bool wantConnected;
        private bool awaitingCon;
        private int missedHeartbeats;

        private CTimer heartbeatTimer, heartbeatTimeoutTimer, reconnectTimer, ackTimer, conTimer;

        public bool Connected { get; private set; }

        public delegate void GroupValueDelegate(string groupAddress, byte[] data, bool isResponse);
        /// <summary>
        /// GroupValueWrite/-Response vom Bus. <c>data</c>: bei 1..6-Bit-Werten 1 Byte (Wert im
        /// Low-Bit-Bereich), sonst die Nutzdaten-Bytes (z.B. 1 Byte bei DPT 5).
        /// </summary>
        public event GroupValueDelegate GroupValueReceived;

        public event Action<bool> ConnectionChanged;

        public KnxNetIpTunnel(string host, string friendlyName = "KNX", int port = DefaultPort, int localPort = DefaultPort)
            : base(host, friendlyName, port, localPort)
        {
            DataEvent += OnUdpData;
        }

        // ---------- Lifecycle ----------------------------------------------

        public void Connect()
        {
            wantConnected = true;
            EnableReceive();
            SendConnectRequest();
        }

        public void Disconnect()
        {
            wantConnected = false;
            StopTimers();
            if (Connected) SendFrame(SvcDisconnectReq, Concat(new byte[] { channelId, 0 }, NatHpai));
            SetConnected(false);
        }

        public override void Dispose()
        {
            Disconnect();
            base.Dispose();
        }

        // ---------- Public API (Gruppen-Telegramme) ------------------------

        /// <summary>DPT 1.x (Schalten): true = 1.</summary>
        public bool WriteBool(string groupAddress, bool value)
        {
            return EnqueueWrite(groupAddress, new byte[0], (byte)(value ? 1 : 0));
        }

        /// <summary>DPT 5.001 (Prozent 0..100 -> 0..255), z.B. Dimmwert.</summary>
        public bool WritePercent(string groupAddress, int percent)
        {
            var p = Math.Max(0, Math.Min(100, percent));
            return WriteByte(groupAddress, (byte)Math.Round(p * 255.0 / 100.0));
        }

        /// <summary>DPT 5.x / 5.010 roh (0..255).</summary>
        public bool WriteByte(string groupAddress, byte value)
        {
            return EnqueueWrite(groupAddress, new[] { value }, 0);
        }

        /// <summary>DPT 17.001 Szene 1..64 (auf dem Bus 0..63).</summary>
        public bool WriteScene(string groupAddress, int sceneNumber)
        {
            if (sceneNumber < 1 || sceneNumber > 64) { Debug.Warn("[KNX] [" + friendlyName + "] Szene " + sceneNumber + " ausserhalb 1..64"); return false; }
            return WriteByte(groupAddress, (byte)(sceneNumber - 1));
        }

        /// <summary>GroupValueRead — Antwort kommt als <see cref="GroupValueReceived"/> mit isResponse=true.</summary>
        public bool Read(string groupAddress)
        {
            ushort ga;
            if (!TryParseGroupAddress(groupAddress, out ga)) return Invalid(groupAddress);
            Enqueue(BuildLData(ga, 0x00, 0x00, new byte[0]));
            return true;
        }

        /// <summary>DPT 5.001 roh (0..255) -> Prozent 0..100.</summary>
        public static int ToPercent(byte raw) { return (int)Math.Round(raw * 100.0 / 255.0); }

        public static bool TryParseGroupAddress(string text, out ushort ga)
        {
            ga = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var p = text.Trim().Split('/');
            int main, middle, sub;
            if (p.Length != 3 || !int.TryParse(p[0], out main) || !int.TryParse(p[1], out middle) || !int.TryParse(p[2], out sub)) return false;
            if (main < 0 || main > 31 || middle < 0 || middle > 7 || sub < 0 || sub > 255) return false;
            ga = (ushort)((main << 11) | (middle << 8) | sub);
            return ga != 0;   // 0/0/0 = "nicht konfiguriert"
        }

        public static string FormatGroupAddress(ushort ga)
        {
            return (ga >> 11) + "/" + ((ga >> 8) & 7) + "/" + (ga & 0xFF);
        }

        // ---------- Senden -------------------------------------------------

        private bool EnqueueWrite(string groupAddress, byte[] data, byte smallValue)
        {
            ushort ga;
            if (!TryParseGroupAddress(groupAddress, out ga)) return Invalid(groupAddress);
            // APCI GroupValueWrite = 0x080; Werte <= 6 Bit stecken im APCI-Byte selbst.
            Enqueue(BuildLData(ga, 0x00, (byte)(0x80 | (data.Length == 0 ? (smallValue & 0x3F) : 0)), data));
            Debug.Log("[KNX] [" + friendlyName + "] Write " + groupAddress + " = "
                      + (data.Length == 0 ? smallValue.ToString() : BitConverter.ToString(data)));
            return true;
        }

        private static byte[] BuildLData(ushort ga, byte tpci, byte apciLow, byte[] data)
        {
            var f = new byte[10 + data.Length + 1];
            f[0] = CemiLDataReq;
            f[1] = 0x00;                     // keine Zusatzinfos
            f[2] = 0xBC;                     // Standard-Frame, nicht wiederholen, Prio low
            f[3] = 0xE0;                     // Gruppenadresse, Hop-Count 6
            f[4] = 0x00; f[5] = 0x00;        // Quelle: setzt das Gateway
            f[6] = (byte)(ga >> 8); f[7] = (byte)ga;
            f[8] = (byte)(1 + data.Length);  // NPDU-Laenge
            f[9] = tpci;
            f[10] = apciLow;
            Array.Copy(data, 0, f, 11, data.Length);
            return f;
        }

        private void Enqueue(byte[] cemi)
        {
            lock (sync)
            {
                txQueue.Enqueue(cemi);
                if (inFlight == null) SendNextLocked();
            }
        }

        private void SendNextLocked()
        {
            awaitingCon = false;
            if (!Connected || txQueue.Count == 0) { inFlight = null; return; }
            inFlight = txQueue.Dequeue();
            inFlightRetries = 0;
            SendInFlightLocked();
        }

        private void SendInFlightLocked()
        {
            SendFrame(SvcTunnelReq, Concat(new byte[] { 0x04, channelId, txSeq, 0x00 }, inFlight));
            if (ackTimer == null) ackTimer = new CTimer(OnAckTimeout, AckTimeoutMs);
            else ackTimer.Reset(AckTimeoutMs);
        }

        private void OnAckTimeout(object _)
        {
            lock (sync)
            {
                if (inFlight == null) return;
                if (inFlightRetries++ < 1)
                {
                    Debug.Warn("[KNX] [" + friendlyName + "] kein ACK auf Seq " + txSeq + " -> Retry");
                    SendInFlightLocked();
                    return;
                }
                Debug.Warn("[KNX] [" + friendlyName + "] kein ACK auf Seq " + txSeq + " -> Telegramm verworfen, Reconnect");
                inFlight = null;
            }
            // Spec: zweimal kein ACK -> Verbindung neu aufbauen.
            Reconnect("ACK-Timeout");
        }

        private void SendConnectRequest()
        {
            // CRI: Tunnel-Verbindung (0x04), Link-Layer (0x02)
            SendFrame(SvcConnectReq, Concat(NatHpai, NatHpai, new byte[] { 0x04, 0x04, 0x02, 0x00 }));
            Debug.Log("[KNX] [" + friendlyName + "] CONNECT_REQUEST -> " + host + ":" + port);
            ScheduleReconnect();   // falls keine Antwort kommt
        }

        private void SendFrame(ushort service, byte[] body)
        {
            var total = 6 + body.Length;
            var f = new byte[total];
            f[0] = 0x06; f[1] = 0x10;
            f[2] = (byte)(service >> 8); f[3] = (byte)service;
            f[4] = (byte)(total >> 8); f[5] = (byte)total;
            Array.Copy(body, 0, f, 6, body.Length);
            Send(f);
        }

        // ---------- Empfang ------------------------------------------------

        private void OnUdpData(byte[] buffer, int length, BasicUDP _)
        {
            if (length < 6 || buffer[0] != 0x06 || buffer[1] != 0x10) return;
            var service = (ushort)((buffer[2] << 8) | buffer[3]);
            var d = new byte[length];
            Array.Copy(buffer, d, length);

            switch (service)
            {
                case SvcConnectRes: OnConnectResponse(d); break;
                case SvcStateRes: OnStateResponse(d); break;
                case SvcTunnelAck: OnTunnelAck(d); break;
                case SvcTunnelReq: OnTunnelRequest(d); break;
                case SvcDisconnectReq:
                    if (d.Length >= 8 && d[6] == channelId)
                    {
                        SendFrame(SvcDisconnectRes, new byte[] { channelId, 0 });
                        Debug.Warn("[KNX] [" + friendlyName + "] Gateway hat getrennt");
                        Reconnect("Disconnect vom Gateway");
                    }
                    break;
                case SvcDisconnectRes: break;
            }
        }

        private void OnConnectResponse(byte[] d)
        {
            if (d.Length < 8) return;
            if (d[7] != 0)
            {
                // 0x24 = keine freie Verbindung (z.B. ETS haelt den Tunnel), 0x22 = Typ nicht unterstuetzt
                Debug.Warn("[KNX] [" + friendlyName + "] CONNECT abgelehnt, Status 0x" + d[7].ToString("X2"));
                return;   // Reconnect-Timer laeuft bereits
            }
            lock (sync)
            {
                channelId = d[6];
                txSeq = 0;
                lastRxSeq = -1;
                missedHeartbeats = 0;
            }
            StopTimer(ref reconnectTimer);
            Debug.Log("[KNX] [" + friendlyName + "] verbunden, Kanal " + channelId);
            SetConnected(true);
            if (heartbeatTimer == null) heartbeatTimer = new CTimer(OnHeartbeat, null, HeartbeatMs, HeartbeatMs);
            else heartbeatTimer.Reset(HeartbeatMs, HeartbeatMs);
            lock (sync) { if (inFlight == null) SendNextLocked(); }
        }

        private void OnTunnelAck(byte[] d)
        {
            if (d.Length < 10 || d[7] != channelId) return;
            lock (sync)
            {
                if (inFlight == null || d[8] != txSeq) return;
                if (d[9] != 0) Debug.Warn("[KNX] [" + friendlyName + "] ACK mit Fehler 0x" + d[9].ToString("X2"));
                txSeq++;
                StopTimer(ref ackTimer);
                // Naechstes Telegramm erst nach L_Data.con (siehe Klassenkommentar).
                awaitingCon = true;
                if (conTimer == null) conTimer = new CTimer(OnConTimeout, ConTimeoutMs);
                else conTimer.Reset(ConTimeoutMs);
            }
        }

        private void OnTunnelRequest(byte[] d)
        {
            if (d.Length < 12 || d[7] != channelId) return;
            var seq = d[8];
            SendFrame(SvcTunnelAck, new byte[] { 0x04, channelId, seq, 0x00 });

            // Duplikat (Gateway wiederholt, weil unser ACK verloren ging) nicht doppelt auswerten.
            if (seq == lastRxSeq) return;
            lastRxSeq = seq;

            var c = 10;
            var msgCode = d[c];
            if (msgCode == CemiLDataCon) { OnConfirm(d, c); return; }
            if (msgCode != CemiLDataInd) return;

            c += 2 + d[c + 1];                              // Zusatzinfos ueberspringen
            if (d.Length < c + 8) return;
            if ((d[c + 1] & 0x80) == 0) return;             // keine Gruppenadresse
            var ga = (ushort)((d[c + 4] << 8) | d[c + 5]);
            var npduLen = d[c + 6];
            if (d.Length < c + 8 + npduLen || npduLen < 1) return;

            var apci = ((d[c + 7] & 0x03) << 2) | (d[c + 8] >> 6);
            if (apci != 1 && apci != 2) return;             // nur Response (1) / Write (2)

            byte[] data;
            if (npduLen == 1) data = new[] { (byte)(d[c + 8] & 0x3F) };
            else { data = new byte[npduLen - 1]; Array.Copy(d, c + 9, data, 0, data.Length); }

            var h = GroupValueReceived;
            if (h == null) return;
            try { h(FormatGroupAddress(ga), data, apci == 1); }
            catch (Exception e) { Debug.Error("[KNX] [" + friendlyName + "] GroupValue-Handler: " + e.Message); }
        }

        private void OnConfirm(byte[] d, int c)
        {
            var ctrl1Index = c + 2 + d[c + 1];
            if (ctrl1Index < d.Length && (d[ctrl1Index] & 0x01) != 0)
                Debug.Warn("[KNX] [" + friendlyName + "] L_Data.con meldet Fehler (Telegramm nicht auf dem Bus)");
            lock (sync)
            {
                if (!awaitingCon) return;
                StopTimer(ref conTimer);
                SendNextLocked();
            }
        }

        private void OnConTimeout(object _)
        {
            lock (sync)
            {
                if (!awaitingCon) return;
                Debug.Warn("[KNX] [" + friendlyName + "] kein L_Data.con nach " + ConTimeoutMs + " ms -> naechstes Telegramm");
                SendNextLocked();
            }
        }

        // ---------- Heartbeat / Reconnect ----------------------------------

        private void OnHeartbeat(object _)
        {
            if (!Connected) return;
            SendFrame(SvcStateReq, Concat(new byte[] { channelId, 0 }, NatHpai));
            if (heartbeatTimeoutTimer == null) heartbeatTimeoutTimer = new CTimer(OnHeartbeatTimeout, HeartbeatTimeoutMs);
            else heartbeatTimeoutTimer.Reset(HeartbeatTimeoutMs);
        }

        private void OnStateResponse(byte[] d)
        {
            if (d.Length < 8 || d[6] != channelId) return;
            StopTimer(ref heartbeatTimeoutTimer);
            if (d[7] == 0) { missedHeartbeats = 0; return; }
            Reconnect("Connectionstate 0x" + d[7].ToString("X2"));
        }

        private void OnHeartbeatTimeout(object _)
        {
            // Spec: nach 3 verpassten Antworten trennen.
            if (++missedHeartbeats < 3) { OnHeartbeat(null); return; }
            Reconnect("Heartbeat-Timeout");
        }

        private void Reconnect(string reason)
        {
            Debug.Warn("[KNX] [" + friendlyName + "] Reconnect: " + reason);
            StopTimers();
            SetConnected(false);
            lock (sync) { inFlight = null; awaitingCon = false; }
            if (wantConnected) ScheduleReconnect();
        }

        private void ScheduleReconnect()
        {
            if (!wantConnected) return;
            if (reconnectTimer == null) reconnectTimer = new CTimer(o => { if (!Connected && wantConnected) SendConnectRequest(); }, ReconnectMs);
            else reconnectTimer.Reset(ReconnectMs);
        }

        private void SetConnected(bool value)
        {
            if (Connected == value) return;
            Connected = value;
            var h = ConnectionChanged;
            if (h != null) h(value);
        }

        private void StopTimers()
        {
            StopTimer(ref heartbeatTimer);
            StopTimer(ref heartbeatTimeoutTimer);
            StopTimer(ref reconnectTimer);
            StopTimer(ref ackTimer);
            StopTimer(ref conTimer);
        }

        private static void StopTimer(ref CTimer t)
        {
            if (t == null) return;
            try { t.Stop(); t.Dispose(); } catch { }
            t = null;
        }

        private bool Invalid(string groupAddress)
        {
            Debug.Warn("[KNX] [" + friendlyName + "] ungueltige/leere Gruppenadresse '" + groupAddress + "'");
            return false;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var len = 0;
            foreach (var p in parts) len += p.Length;
            var r = new byte[len];
            var o = 0;
            foreach (var p in parts) { Array.Copy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }
    }
}
