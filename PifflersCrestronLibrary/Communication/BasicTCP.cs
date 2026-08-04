using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

using PifflersCrestronLibrary.Logger;

namespace PifflersCrestronLibrary.Communication
{ 
    public abstract class BasicTCP : IDisposable
    {
        protected Dictionary<string, Action<MatchCollection>> matchStringDict;
        protected TCPClient client;

        protected string friendlyName;
        protected string host;
        protected bool connect;
        protected bool connected;
        protected CTimer keepAliveTimer;
        protected CTimer reconnectTimer;
        protected CTimer connectWatchdogTimer;
        private readonly object connectWatchdogLock = new object();

        protected int port;
        protected const long ConnectTimeoutMs = 8000;

        public delegate void ConnectionStateDelegate(ushort state);
        public event ConnectionStateDelegate ConnectionStateEvent;

        public delegate void DataEventDelegate(string Argument, BasicTCP device);
        public event DataEventDelegate DataEvent;

        public bool Connected
        {
            get { return connected; }
        }

        public BasicTCP(string host, string friendlyName, int port)
        {
            this.host = (host ?? string.Empty).Trim();
            this.friendlyName = string.IsNullOrWhiteSpace(friendlyName) ? "TCPDevice" : friendlyName;
            this.port = port;
            this.matchStringDict = new Dictionary<string, Action<MatchCollection>>();

            InitializeRegexDictionary();

            client = new TCPClient(this.host, port, 1024);
            client.SocketStatusChange += SocketStatusChange;
            
            Debug.Log("[TCP] [" + this.friendlyName + "] created (" + this.host + ":" + this.port + ").");
        }

        public void Connect()
        {
            connect = true;
            TryConnectAsync("manual connect");
        }

        public void Disconnect()
        {
            connect = false;
            StopConnectWatchdog();
            reconnectTimer?.Stop();
            reconnectTimer?.Dispose();
            reconnectTimer = null;
            client.DisconnectFromServer();
            Debug.Log("[TCP] [" + friendlyName + "] " + client.ClientStatus);
        }

        protected virtual void TryConnectAsync(string reason)
        {
            if (!connect)
                return;

            if (string.IsNullOrWhiteSpace(host))
            {
                Debug.Error("[TCP] [" + friendlyName + "] connect aborted: host is empty");
                return;
            }

            if (client.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
                return;

            if (client.ClientStatus == SocketStatus.SOCKET_STATUS_WAITING)
                return;
            
            StartConnectWatchdog();
            Debug.Log("[TCP] [" + friendlyName + "] connect attempt: " + reason + " (" + host + ":" + port + ")");
            client.ConnectToServerAsync(ConnectCallBack);
        }

        public void Dispose()
        {
            StopConnectWatchdog();
            if (keepAliveTimer != null) keepAliveTimer.Dispose();
            if (reconnectTimer != null) reconnectTimer.Dispose();
            if (client != null) client.Dispose();
        }

        protected virtual void ConnectCallBack(TCPClient client)
        {
            if (client.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                StopConnectWatchdog();
                reconnectTimer?.Stop();
                reconnectTimer?.Dispose();
                reconnectTimer = null;
                Debug.Log("[TCP] [" + friendlyName + "] connected to " + host);
                client.ReceiveDataAsync(ReceiveDataCallback);
                StartKeepAlive();
            }
            else
            {
                StopConnectWatchdog();
                Debug.Warn("[TCP] [" + friendlyName + "] connect failed: " + client.ClientStatus);
                StopKeepAlive();
                if (connect)
                {
                    Debug.Log("[TCP] [" + friendlyName + "] disconnected unexpectedly");
                    ScheduleReconnect();
                }
                else
                {
                    Debug.Log("[TCP] [" + friendlyName + "] disconnected by program");
                    StopKeepAlive();
                }
            }
        }

        protected void SocketStatusChange(TCPClient client, SocketStatus status)
        {
            Debug.Log("[TCP] [" + friendlyName + "] status: " + status);
            connected = (status == SocketStatus.SOCKET_STATUS_CONNECTED);
            if (ConnectionStateEvent != null)
                ConnectionStateEvent((ushort)(connected ? 1 : 0));

            if (connected)
            {
                StopConnectWatchdog();
                reconnectTimer?.Stop();
                reconnectTimer?.Dispose();
                reconnectTimer = null;
            }
        }

        protected void StartConnectWatchdog()
        {
            lock (connectWatchdogLock)
            {
                if (connectWatchdogTimer == null)
                    connectWatchdogTimer = new CTimer(OnConnectWatchdogTimeout, null, ConnectTimeoutMs);
                else
                    connectWatchdogTimer.Reset(ConnectTimeoutMs);
            }
        }

        protected void StopConnectWatchdog()
        {
            CTimer timer;
            lock (connectWatchdogLock)
            {
                timer = connectWatchdogTimer;
                connectWatchdogTimer = null;
            }

            if (timer == null)
                return;

            try { timer.Stop(); }
            catch (Exception ex) { Debug.Warn("[TCP] [" + friendlyName + "] watchdog stop failed: " + ex.Message); }

            try { timer.Dispose(); }
            catch (Exception ex) { Debug.Warn("[TCP] [" + friendlyName + "] watchdog dispose failed: " + ex.Message); }
        }

        protected void OnConnectWatchdogTimeout(object _)
        {
            if (!connect || connected)
                return;

            Debug.Warn("[TCP] [" + friendlyName + "] connect timeout after " + ConnectTimeoutMs + "ms (status: " + client.ClientStatus + ")");
            client.DisconnectFromServer();
            ScheduleReconnect();
        }

        protected void ReceiveDataCallback(TCPClient client, int QtyBytesReceived)
        {
            if (QtyBytesReceived > 0)
            {
                string data = Encoding.Default.GetString(client.IncomingDataBuffer, 0, QtyBytesReceived);
                Debug.Log("[TCP] [" + friendlyName + "] [RX] " + data);
                ProcessFeedback(data);
                client.ReceiveDataAsync(ReceiveDataCallback);
            }
            else if (client.ClientStatus != SocketStatus.SOCKET_STATUS_CONNECTED && connect)
            {
                ScheduleReconnect();
            }
        }

        protected void ScheduleReconnect()
        {
            if (reconnectTimer != null)
            {
                reconnectTimer.Stop();
                reconnectTimer.Dispose();
            }

            reconnectTimer = new CTimer(o => TryConnectAsync("reconnect timer"), null, 2000);
        }

        protected void StartKeepAlive()
        {
            if (keepAliveTimer == null)
            {
                keepAliveTimer = new CTimer(KeepAliveCallback, null, 60000, 60000);
            }
            else
            {
                keepAliveTimer.Reset(60000, 60000);
            }
        }

        protected void StopKeepAlive()
        {
            if (keepAliveTimer != null)
                keepAliveTimer.Stop();
        }

        protected virtual void KeepAliveCallback(object _)
        {
            SendRaw("*KEEPALIVE\n");
        }

        protected void SendRaw(string message)
        {
            if (connected && client.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                byte[] bytes = Encoding.GetEncoding(1252).GetBytes(message);
                client.SendData(bytes, bytes.Length);
                Debug.Log("[TCP] [" + friendlyName + "] [TX] " + message);
            }
        }

        protected void SendRaw(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return;

            if (connected && client.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                client.SendData(payload, payload.Length);
                Debug.Log("[TCP] [" + friendlyName + "] [TX-HEX] " + BitConverter.ToString(payload));
            }
        }

        protected void ProcessFeedback(string message)
        {
            foreach (var kv in matchStringDict)
            {
                var match = Regex.Matches(message, kv.Key);
                if (match.Count > 0)
                {
                    try { kv.Value(match); }
                    catch (Exception e) { Debug.Warn("[TCP] [" + friendlyName + "] feedback handler exception: " + e.Message); }
                    break;
                }
            }
        }

        protected void RaiseDataEvent(string key)
        {
            if (DataEvent != null)
                DataEvent(key, this);
        }

        protected abstract void InitializeRegexDictionary();
    }

}