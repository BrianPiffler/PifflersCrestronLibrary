using Crestron.SimplSharp;
using Crestron.SimplSharp.Ssh.Common;
using Crestron.SimplSharp.Ssh;

using System;
using System.Threading.Tasks; 

using PifflersCrestronLibrary.Logger;

namespace PifflersCrestronLibrary.Communication
{ 
    public class SSHClient : IDisposable
    {
        public delegate void ConnectionStateHandler(ushort state);
        public delegate void ReceivedDataHandler(SimplSharpString data);

        public ReceivedDataHandler ReceivedData { get; set; }
        public ConnectionStateHandler ConnectionState { get; set; }

        private readonly object stateLock = new object();
        private readonly object sendLock = new object();

        private readonly string hostname;
        private readonly string username;
        private readonly string password;
        private readonly int port;
        private readonly string friendlyName;

        private SshClient client;
        private ShellStream stream;

        private bool disposed;
        private bool isConnecting;
        private ushort currentConnectionState;

        private int connectionGeneration;

        public SSHClient(string hostname, string username, string password, int port, string friendlyName)
        {
            this.hostname = hostname;
            this.username = username;
            this.password = password;
            this.port = port;
            this.friendlyName = friendlyName;
            
            Debug.Log("SSHClient for " + friendlyName + " created.");
            Debug.Log("SSHClient config - Hostname: " + hostname + ", Port: " + port + ", Username: " + username);
        }

        public bool IsConnected
        {
            get
            {
                SshClient localClient;
                ShellStream localStream;

                lock (stateLock)
                {
                    localClient = client;
                    localStream = stream;
                }

                return localClient != null &&
                       localClient.IsConnected &&
                       localStream != null &&
                       !disposed;
            }
        }

        public void Connect()
        {
            int generation;

            lock (stateLock)
            {
                if (disposed)
                {
                    Debug.Log("Connect() ignored because client is disposed.");
                    return;
                }

                if (isConnecting)
                {
                    Debug.Log("Connect() ignored because connection attempt is already running.");
                    return;
                }

                if (client != null && client.IsConnected && stream != null)
                {
                    Debug.Log("Connect() ignored because SSH is already connected.");
                    FireConnectionStateAsync(1);
                    return;
                }

                isConnecting = true;
                connectionGeneration++;
                generation = connectionGeneration;
            }

            CleanupConnectionObjects();

            Task.Run(() => ConnectInternal(generation));
        }

        private void ConnectInternal(int generation)
        {
            SshClient newClient = null;
            ShellStream newStream = null;

            try
            {
                Debug.Log("SSH connect started.");

                KeyboardInteractiveAuthenticationMethod authMethod =
                    new KeyboardInteractiveAuthenticationMethod(username);

                authMethod.AuthenticationPrompt += AuthenticationPromptHandler;

                ConnectionInfo connectionInfo = new ConnectionInfo(hostname, port, username, authMethod);

                try
                {
                    connectionInfo.Timeout = TimeSpan.FromSeconds(10);
                }
                catch
                {
                    // Some Crestron SSH library versions may not expose Timeout.
                }

                newClient = new SshClient(connectionInfo);
                newClient.ErrorOccurred += ClientErrorHandler;
                newClient.HostKeyReceived += HostKeyReceivedHandler;

                newClient.Connect();

                if (!newClient.IsConnected)
                {
                    throw new Exception("SSH client did not reach connected state.");
                }

                newStream = newClient.CreateShellStream("terminal", 80, 24, 800, 600, 4096);
                newStream.DataReceived += StreamDataReceivedHandler;
                newStream.ErrorOccurred += StreamErrorOccurredHandler;

                bool acceptConnection = false;

                lock (stateLock)
                {
                    if (!disposed && generation == connectionGeneration)
                    {
                        client = newClient;
                        stream = newStream;
                        isConnecting = false;
                        acceptConnection = true;
                    }
                }

                if (!acceptConnection)
                {
                    Debug.Log("SSH connect result discarded because client was disposed or replaced.");

                    SafeDisposeStream(newStream);
                    SafeDisposeClient(newClient);
                    return;
                }

                Debug.Log("SSH connected.");
                SetConnectionState(1);
            }
            catch (Exception ex)
            {
                Debug.Log("SSH connect error: " + ex.Message);

                SafeDisposeStream(newStream);
                SafeDisposeClient(newClient);

                lock (stateLock)
                {
                    if (generation == connectionGeneration)
                    {
                        isConnecting = false;
                    }
                }

                SetConnectionState(0);
            }
        }

        public void Disconnect()
        {
            Debug.Log("Disconnect() called.");

            lock (stateLock)
            {
                connectionGeneration++;
                isConnecting = false;
            }

            CleanupConnectionObjects();
            SetConnectionState(0);
        }

        public void SendCommand(string command)
        {
            if (string.IsNullOrEmpty(command))
            {
                return;
            }

            SshClient localClient;
            ShellStream localStream;

            lock (stateLock)
            {
                localClient = client;
                localStream = stream;
            }

            if (localClient == null || !localClient.IsConnected || localStream == null || !localStream.CanWrite)
            {
                Debug.Log("SendCommand() ignored because SSH is not connected.");
                return;
            }

            try
            {
                lock (sendLock)
                {
                    if (command.IndexOf('\r') >= 0 || command.IndexOf('\n') >= 0)
                    {
                        localStream.Write(command);
                    }
                    else
                    {
                        localStream.WriteLine(command);
                    }

                    localStream.Flush();
                }

                Debug.Log("SSH SEND: " + command.Replace("\r", "\\r").Replace("\n", "\\n"));
            }
            catch (Exception ex)
            {
                Debug.Log("SendCommand exception: " + ex.Message);
                BeginDisconnectFromError();
            }
        }

        private void StreamDataReceivedHandler(object sender, ShellDataEventArgs e)
        {
            ShellStream localStream;

            lock (stateLock)
            {
                if (disposed || stream == null)
                {
                    return;
                }

                localStream = stream;
            }

            string receivedData = "";

            try
            {
                while (localStream != null && localStream.DataAvailable)
                {
                    receivedData += localStream.Read();
                }
            }
            catch (Exception ex)
            {
                Debug.Log("Stream read exception: " + ex.Message);
                BeginDisconnectFromError();
                return;
            }

            if (string.IsNullOrEmpty(receivedData))
            {
                return;
            }

            Debug.Log("SSH REC: " + receivedData);

            try
            {
                ReceivedData?.Invoke(receivedData);
            }
            catch (Exception ex)
            {
                Debug.Log("ReceivedData invoke exception: " + ex.Message);
            }
        }

        private void StreamErrorOccurredHandler(object sender, ExceptionEventArgs e)
        {
            string errorMessage = e != null && e.Exception != null
                ? e.Exception.Message
                : "unknown stream error";

            Debug.Log("SSH stream error: " + errorMessage);
            BeginDisconnectFromError();
        }

        private void ClientErrorHandler(object sender, ExceptionEventArgs e)
        {
            string errorMessage = e != null && e.Exception != null
                ? e.Exception.Message
                : "unknown client error";

            Debug.Log("SSH client error: " + errorMessage);
            BeginDisconnectFromError();
        }

        private void AuthenticationPromptHandler(object sender, AuthenticationPromptEventArgs e)
        {
            Debug.Log("SSH authentication prompt received.");

            foreach (AuthenticationPrompt prompt in e.Prompts)
            {
                if (prompt.Request.IndexOf("Password:", StringComparison.InvariantCultureIgnoreCase) != -1 ||
                    prompt.Request.IndexOf("Password", StringComparison.InvariantCultureIgnoreCase) != -1)
                {
                    prompt.Response = password;
                }
            }
        }

        private void HostKeyReceivedHandler(object sender, HostKeyEventArgs e)
        {
            Debug.Log("SSH host key received.");
            e.CanTrust = true;
        }

        private void BeginDisconnectFromError()
        {
            Task.Run(() =>
            {
                try
                {
                    Debug.Log("SSH error disconnect started.");

                    lock (stateLock)
                    {
                        connectionGeneration++;
                        isConnecting = false;
                    }

                    CleanupConnectionObjects();
                    SetConnectionState(0);
                }
                catch (Exception ex)
                {
                    Debug.Log("BeginDisconnectFromError exception: " + ex.Message);
                }
            });
        }

        private void SetConnectionState(ushort state)
        {
            bool shouldFire = false;

            lock (stateLock)
            {
                if (currentConnectionState != state)
                {
                    currentConnectionState = state;
                    shouldFire = true;
                }
            }

            if (shouldFire)
            {
                FireConnectionStateAsync(state);
            }
        }

        private void FireConnectionStateAsync(ushort state)
        {
            Task.Run(() =>
            {
                try
                {
                    ConnectionState?.Invoke(state);
                }
                catch (Exception ex)
                {
                    Debug.Log("ConnectionState invoke exception: " + ex.Message);
                }
            });
        }

        private void CleanupConnectionObjects()
        {
            SshClient oldClient = null;
            ShellStream oldStream = null;

            lock (stateLock)
            {
                oldClient = client;
                oldStream = stream;

                client = null;
                stream = null;
            }

            SafeDisposeStream(oldStream);
            SafeDisposeClient(oldClient);
        }

        private void SafeDisposeStream(ShellStream oldStream)
        {
            if (oldStream == null)
            {
                return;
            }

            try
            {
                oldStream.DataReceived -= StreamDataReceivedHandler;
                oldStream.ErrorOccurred -= StreamErrorOccurredHandler;
            }
            catch (Exception ex)
            {
                Debug.Log("Stream event detach exception: " + ex.Message);
            }

            try
            {
                oldStream.Dispose();
            }
            catch (Exception ex)
            {
                Debug.Log("Stream dispose exception: " + ex.Message);
            }
        }

        private void SafeDisposeClient(SshClient oldClient)
        {
            if (oldClient == null)
            {
                return;
            }

            try
            {
                oldClient.ErrorOccurred -= ClientErrorHandler;
                oldClient.HostKeyReceived -= HostKeyReceivedHandler;
            }
            catch (Exception ex)
            {
                Debug.Log("Client event detach exception: " + ex.Message);
            }

            try
            {
                if (oldClient.IsConnected)
                {
                    oldClient.Disconnect();
                }
            }
            catch (Exception ex)
            {
                Debug.Log("Client disconnect exception: " + ex.Message);
            }

            try
            {
                oldClient.Dispose();
            }
            catch (Exception ex)
            {
                Debug.Log("Client dispose exception: " + ex.Message);
            }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                connectionGeneration++;
                isConnecting = false;
            }

            CleanupConnectionObjects();
            SetConnectionState(0);
        }
    }
}