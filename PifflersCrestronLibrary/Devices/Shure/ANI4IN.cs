using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;

using System;
using System.Collections.Generic;

using System.Text;

using System.Text.RegularExpressions;
using PifflersCrestronLibrary.Logger;

namespace PifflersCrestronLibrary.Devices.Shure
{
    public class Shure_ANI4IN : IDisposable
    {
        private Dictionary<string, Action<MatchCollection>> matchStringDict = new Dictionary<string, Action<MatchCollection>>();
        private TCPClient client;

        private string friendlyName;

        private const int port = 2202;
        private CTimer keepAliveTimer;
        private CTimer reconnectTimer;
        private bool connect;

        private string host;

        public delegate void ConnectionStateDelegate(ushort state);
        public event ConnectionStateDelegate ConnectionStateEvent;

        public delegate void DataEventDelegate(string Argument, Shure_ANI4IN device);
        public event DataEventDelegate DataEvent;

        private bool connected;
        public bool Connected => connected;

        private bool logicSwitchOut1Status;
        private bool logicSwitchOut2Status;
        private bool logicSwitchOut3Status;
        private bool logicSwitchOut4Status;

        public bool LogicSwitchOut1Status
        {
            get
            {
                return logicSwitchOut1Status;
            }
        }

        public bool LogicSwitchOut2Status
        {
            get
            {
                return logicSwitchOut2Status;
            }
        }

        public bool LogicSwitchOut3Status
        {
            get
            {
                return logicSwitchOut3Status;
            }
        }

        public bool LogicSwitchOut4Status
        {
            get
            {
                return logicSwitchOut4Status;
            }
        }

        public Shure_ANI4IN(string host, string friendlyName)
        {
            try
            {
                this.friendlyName = friendlyName;
                this.host = host;

                InitializeRegexDictionary();

                client = new TCPClient(host, port, 1024);
                client.SocketStatusChange += SocketStatusChange;
                
                Debug.Log(friendlyName + " created.");
            }
            catch (Exception e)
            {
                ErrorLog.Error(friendlyName + ": Error in Constructor: " + e.Message);
            }
        }

        private void InitializeRegexDictionary()
        {
            matchStringDict.Add(@"< REP ([0-4]) HW_GATING_LOGIC (ON|OFF) >", __MatchMicLogicSwitchOutStatus);
        }
        
        private void ConnectCallBack(TCPClient client)
        {
            if (client.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                Debug.Log($"{this.friendlyName} starting keep Alive for {host}");
                client.ReceiveDataAsync(ReceiveDataCallback);
                if (keepAliveTimer == null)
                {
                    keepAliveTimer = new CTimer(keepAlive, null, 60000, 60000);
                }
                else
                {
                    keepAliveTimer.Reset(60000, 60000);
                }
            }
            else
            {
                if (connected)
                {
                    Debug.Log("Unwanted disconnect from Vaddio");
                    ScheduleReconnect();
                    Debug.Log("Attempting to reconnect Vaddio Camera");
                }
                else
                {
                    Debug.Log("Vaddio Disconnect by Program");
                    keepAliveTimer?.Stop();
                }
            }
        }

        private void ScheduleReconnect()
        {
            if (!connect)
                return;

            if (reconnectTimer != null)
            {
                reconnectTimer.Stop();
                reconnectTimer.Dispose();
            }

            // Schedule a reconnect attempt after 2 seconds
            reconnectTimer = new CTimer(o => TryConnectAsync("reconnect timer"), null, 2000);
        }

        private void SocketStatusChange(TCPClient myTCPClient, SocketStatus clientSocketStatus)
        {
            Debug.Log($"{this.friendlyName} LAN client ({host}) reports: {clientSocketStatus}");

            if (clientSocketStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                connected = true;
                reconnectTimer?.Stop();
                reconnectTimer?.Dispose();
                reconnectTimer = null;
                Debug.Log("SOCKET CONNECTED!");
                ConnectionStateEvent?.Invoke(1);
            }
            else
            {
                connected = false;
                if (connect)
                    ScheduleReconnect();
                ConnectionStateEvent?.Invoke(0);
            }
        }
        
        private void ReceiveDataCallback(TCPClient client, int QtyBytesReceived)
        {
            if (QtyBytesReceived > 0)
            {
                string dataReceived = Encoding.Default.GetString(client.IncomingDataBuffer, 0, QtyBytesReceived);
                Debug.Log("DATA REC: " + dataReceived);
                feedbackProcess(dataReceived);
                client.ReceiveDataAsync(ReceiveDataCallback);
            }
            else
            {
                if (client.ClientStatus != SocketStatus.SOCKET_STATUS_CONNECTED)
                {
                    if (connect)
                        ScheduleReconnect();
                }
            }
        }

        public void Connect()
        {
            connect = true;
            TryConnectAsync("manual connect");
            Debug.Log(client.ClientStatus.ToString());
        }

        public void Disconnect()
        {
            connect = false;
            reconnectTimer?.Stop();
            reconnectTimer?.Dispose();
            reconnectTimer = null;
            client.DisconnectFromServer();
            Debug.Log(client.ClientStatus.ToString());
        }

        public void keepAlive(object userSpecificObjects)
        {
            __SetHelper("< GET 0 CHAN_LED_IN_STATE >");
        }

        private void __SetHelper(string message)
        {
            if (connected)
            {
                Debug.Log(message);
                if (client.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
                {
                    byte[] bytes = Encoding.GetEncoding(1252).GetBytes(message);
                    client.SendData(bytes, bytes.Length);
                }
                else
                {
                    if (connect)
                        ScheduleReconnect();
                }
            }
        }

        private void TryConnectAsync(string reason)
        {
            if (!connect)
                return;

            if (client.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
                return;

            Debug.Log($"{friendlyName} connect attempt: {reason}");
            client.ConnectToServerAsync(ConnectCallBack);
        }

        public void SetMicLogicLED(int channel, bool status)
        {
            __SetHelper($"< SET {channel} CHAN_LED_IN_STATE {status} >");
        }

        private void feedbackProcess(string m)
        {
            foreach (var matchString in matchStringDict.Keys)
            {
                var regex = new Regex(matchString, RegexOptions.Compiled);
                var match = regex.Matches(m);
                if (match.Count > 0)
                {
                    try
                    {
                        matchStringDict[matchString].Invoke(match);
                    }
                    catch (Exception e)
                    {
                        Debug.Log(e.Message);
                    }
                    break;
                }
            }
        }

        private void __MatchMicLogicSwitchOutStatus(MatchCollection match) 
        { 
            string input = match[0].Groups[1].Value;
            string status = match[0].Groups[2].Value;

            if(input == "1")
            {
                logicSwitchOut1Status = status == "ON" ? true : false;
            }
            else if(input == "2")
            {
                logicSwitchOut2Status = status == "ON" ? true : false;
            }
            else if(input == "3")
            {
                logicSwitchOut3Status = status == "ON" ? true : false;
            }
            else if(input == "4")
            {
                logicSwitchOut4Status = status == "ON" ? true : false;
            }

            DataEvent?.Invoke("LogicSwitchOutStatus", this);
        }

        public void Dispose()
        {
            keepAliveTimer?.Dispose();
            reconnectTimer?.Dispose();
            client?.Dispose();
        }
    }

}