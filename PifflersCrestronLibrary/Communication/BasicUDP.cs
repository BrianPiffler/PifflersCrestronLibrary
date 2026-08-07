using System;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;

using PifflersCrestronLibrary.Logger;

namespace PifflersCrestronLibrary.Communication
{
    /// <summary>
    /// Schlanke UDP-Basisklasse fuer Send-orientierte Protokolle (Art-Net, sACN,
    /// simple UDP-APIs). Sendezielt an <c>host:port</c>. Optionales Empfangen
    /// ueber <see cref="DataEvent"/> nach <see cref="EnableReceive"/>.
    ///
    /// Konvention (analog <see cref="BasicTCP"/>):
    /// - Constructor: (host, friendlyName, port)
    /// - <see cref="Send(byte[])"/> als Kernmethode; grosse Datenmengen wandern auf einmal raus.
    /// - Debug.Log fuer TX/Enable, Debug.Error fuer Fehlerpfade.
    /// </summary>
    public abstract class BasicUDP : IDisposable
    {
        protected readonly string friendlyName;
        protected readonly string host;
        protected readonly int port;
        protected readonly int localPort;

        protected UDPServer udp;
        protected bool enabled;

        public delegate void DataEventDelegate(byte[] data, int length, BasicUDP device);
        public event DataEventDelegate DataEvent;

        public bool Enabled { get { return enabled; } }
        public string Host { get { return host; } }
        public int Port { get { return port; } }

        /// <summary>Sendeziel-Basis. localPort=0 -&gt; System vergibt (nur senden). Fuer Empfang localPort setzen (z.B. Art-Net 6454).</summary>
        protected BasicUDP(string host, string friendlyName, int port, int localPort = 0)
        {
            this.host = (host ?? string.Empty).Trim();
            this.friendlyName = string.IsNullOrWhiteSpace(friendlyName) ? "UDPDevice" : friendlyName;
            this.port = port;
            this.localPort = localPort;

            Debug.Log("[UDP] [" + this.friendlyName + "] created (" + this.host + ":" + this.port + ").");
        }

        /// <summary>Aktiviert den UDPServer als Sende-Endpunkt. Idempotent.</summary>
        public virtual void Enable()
        {
            if (enabled) return;
            if (string.IsNullOrWhiteSpace(host))
            {
                Debug.Error("[UDP] [" + friendlyName + "] Enable abgebrochen: host ist leer");
                return;
            }

            try
            {
                udp = new UDPServer();
                var status = udp.EnableUDPServer(host, port, localPort);
                enabled = status == SocketErrorCodes.SOCKET_OK;
                Debug.Log("[UDP] [" + friendlyName + "] enabled -> " + status);
            }
            catch (Exception e)
            {
                Debug.Error("[UDP] [" + friendlyName + "] Enable failed: " + e.Message);
                enabled = false;
            }
        }

        /// <summary>Startet asynchrones Empfangen. Wenn nicht aktiviert, wird zuvor <see cref="Enable"/> aufgerufen.</summary>
        public void EnableReceive()
        {
            Enable();
            if (!enabled) return;
            udp.ReceiveDataAsync(ReceiveCallback);
        }

        private void ReceiveCallback(UDPServer server, int numBytes)
        {
            try
            {
                if (numBytes > 0)
                {
                    var handler = DataEvent;
                    if (handler != null) handler(server.IncomingDataBuffer, numBytes, this);
                }
            }
            catch (Exception e)
            {
                Debug.Error("[UDP] [" + friendlyName + "] Receive-Handler Exception: " + e.Message);
            }
            finally
            {
                if (enabled && udp != null)
                    udp.ReceiveDataAsync(ReceiveCallback);
            }
        }

        /// <summary>Sendet ein Paket ans konfigurierte Ziel (<c>host:port</c>).</summary>
        public bool Send(byte[] data)
        {
            if (data == null || data.Length == 0) return false;
            if (!enabled) Enable();
            if (!enabled) return false;

            try
            {
                var status = udp.SendData(data, data.Length);
                if (status != SocketErrorCodes.SOCKET_OK)
                    Debug.Error("[UDP] [" + friendlyName + "] Send failed: " + status);
                return status == SocketErrorCodes.SOCKET_OK;
            }
            catch (Exception e)
            {
                Debug.Error("[UDP] [" + friendlyName + "] Send Exception: " + e.Message);
                return false;
            }
        }

        public virtual void Dispose()
        {
            try
            {
                if (udp != null)
                {
                    udp.DisableUDPServer();
                    udp.Dispose();
                }
            }
            catch (Exception e)
            {
                Debug.Error("[UDP] [" + friendlyName + "] Dispose Exception: " + e.Message);
            }
            finally
            {
                udp = null;
                enabled = false;
            }
        }
    }
}
