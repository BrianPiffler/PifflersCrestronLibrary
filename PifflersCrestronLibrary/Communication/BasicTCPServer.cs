using System;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;
using PifflersCrestronLibrary.Logger;

namespace PifflersCrestronLibrary.Communication
{
    /// <summary>
    /// Schlanke TCP-Server-Basisklasse (Pendant zu <see cref="BasicTCP"/>).
    /// Nimmt mehrere Client-Verbindungen an, meldet Verbinden/Trennen und roh
    /// empfangene Daten je Client per Event. Ableiter (z.B. CompanionBridge)
    /// bauen darauf zeilen-/protokollbasierte Logik.
    ///
    /// Konvention (analog BasicTCP): Constructor (port, friendlyName, [maxClients]).
    /// </summary>
    public abstract class BasicTCPServer : IDisposable
    {
        protected readonly string friendlyName;
        protected readonly int port;
        protected readonly int maxClients;

        protected TCPServer server;
        protected bool running;

        public delegate void ClientConnectDelegate(uint clientIndex, string address);
        public event ClientConnectDelegate ClientConnected;

        public delegate void ClientDisconnectDelegate(uint clientIndex);
        public event ClientDisconnectDelegate ClientDisconnected;

        public delegate void DataReceivedDelegate(uint clientIndex, byte[] data, int length);
        public event DataReceivedDelegate DataReceived;

        public bool Running { get { return running; } }
        public int Port { get { return port; } }

        protected BasicTCPServer(int port, string friendlyName, int maxClients = 4)
        {
            this.port = port;
            this.friendlyName = string.IsNullOrWhiteSpace(friendlyName) ? "TCPServer" : friendlyName;
            this.maxClients = Math.Max(1, maxClients);

            Debug.Log("[TCPServer] [" + this.friendlyName + "] created (port " + this.port + ", max " + this.maxClients + ").");
        }

        /// <summary>Startet den Listener. Idempotent.</summary>
        public virtual void Start()
        {
            if (running) return;
            try
            {
                server = new TCPServer(port, maxClients);
                server.SocketStatusChange += OnServerSocketStatus;
                var status = server.WaitForConnectionAsync(OnConnect);
                running = status == SocketErrorCodes.SOCKET_OPERATION_PENDING || status == SocketErrorCodes.SOCKET_OK;
                Debug.Log("[TCPServer] [" + friendlyName + "] listening -> " + status);
            }
            catch (Exception e)
            {
                Debug.Error("[TCPServer] [" + friendlyName + "] Start failed: " + e.Message);
                running = false;
            }
        }

        public virtual void Stop()
        {
            if (!running) return;
            running = false;
            try
            {
                if (server != null)
                {
                    server.DisconnectAll();
                    server.Stop();
                }
                Debug.Log("[TCPServer] [" + friendlyName + "] stopped.");
            }
            catch (Exception e)
            {
                Debug.Error("[TCPServer] [" + friendlyName + "] Stop Exception: " + e.Message);
            }
        }

        private void OnServerSocketStatus(TCPServer myServer, uint clientIndex, SocketStatus status)
        {
            if (status != SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                var handler = ClientDisconnected;
                if (handler != null) handler(clientIndex);
                Debug.Log("[TCPServer] [" + friendlyName + "] client " + clientIndex + " -> " + status);
            }
        }

        private void OnConnect(TCPServer myServer, uint clientIndex)
        {
            if (!running) return;

            if (clientIndex == 0)
            {
                Debug.Error("[TCPServer] [" + friendlyName + "] OnConnect invalid clientIndex 0");
            }
            else
            {
                var addr = myServer.GetAddressServerAcceptedConnectionFromForSpecificClient(clientIndex);
                Debug.Log("[TCPServer] [" + friendlyName + "] client " + clientIndex + " connected (" + addr + ")");
                var handler = ClientConnected;
                if (handler != null) handler(clientIndex, addr);

                myServer.ReceiveDataAsync(clientIndex, OnDataReceived);
            }

            // Auf naechsten Client warten (bis maxClients erreicht ist)
            if (running) myServer.WaitForConnectionAsync(OnConnect);
        }

        private void OnDataReceived(TCPServer myServer, uint clientIndex, int numBytes)
        {
            if (!running) return;
            try
            {
                if (numBytes > 0)
                {
                    var buf = myServer.GetIncomingDataBufferForSpecificClient(clientIndex);
                    var handler = DataReceived;
                    if (handler != null && buf != null) handler(clientIndex, buf, numBytes);
                    // weiter horchen
                    myServer.ReceiveDataAsync(clientIndex, OnDataReceived);
                }
                else
                {
                    // 0/negativ = Verbindung weg
                    Debug.Log("[TCPServer] [" + friendlyName + "] client " + clientIndex + " closed (" + numBytes + " bytes)");
                }
            }
            catch (Exception e)
            {
                Debug.Error("[TCPServer] [" + friendlyName + "] Receive Exception (client " + clientIndex + "): " + e.Message);
            }
        }

        /// <summary>Sendet Rohdaten an genau einen Client.</summary>
        public bool SendToClient(uint clientIndex, byte[] data)
        {
            if (!running || server == null || data == null || data.Length == 0) return false;
            try
            {
                var status = server.SendData(clientIndex, data, data.Length);
                return status == SocketErrorCodes.SOCKET_OK;
            }
            catch (Exception e)
            {
                Debug.Error("[TCPServer] [" + friendlyName + "] SendToClient(" + clientIndex + ") Exception: " + e.Message);
                return false;
            }
        }

        /// <summary>Convenience: UTF-8-Text an einen Client.</summary>
        public bool SendToClient(uint clientIndex, string text)
        {
            return text != null && SendToClient(clientIndex, Encoding.UTF8.GetBytes(text));
        }

        /// <summary>Sendet Rohdaten an alle verbundenen Clients. Rueckgabe: Anzahl erfolgreich adressierter Clients.</summary>
        public int Broadcast(byte[] data)
        {
            if (!running || server == null || data == null || data.Length == 0) return 0;
            int ok = 0;
            for (uint i = 1; i <= server.MaxNumberOfClientSupported; i++)
            {
                if (server.ClientConnected(i) && SendToClient(i, data)) ok++;
            }
            return ok;
        }

        public int Broadcast(string text)
        {
            return text == null ? 0 : Broadcast(Encoding.UTF8.GetBytes(text));
        }

        public virtual void Dispose()
        {
            Stop();
            server = null;
        }
    }
}
