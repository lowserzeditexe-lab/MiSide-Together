using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace MiSideCoop.Network
{
    /// <summary>
    /// Transport TCP minimaliste (remplace Mirror + Telepathy).
    /// Communication 1-vs-1 (hôte ↔ invité). Threading : 1 thread reader par socket.
    /// Messages reçus poussés dans une ConcurrentQueue lue depuis le thread Unity.
    /// </summary>
    public class CoopTcpTransport
    {
        // ── Format des trames sur le fil ─────────────────────────────────────
        // [int32 length][byte msgId][payload …]
        // Toutes les chaînes utilisent le BinaryWriter standard (préfixe varint).

        private TcpListener _listener;
        private TcpClient   _serverPeer;     // côté hôte : la connexion vers l'invité
        private TcpClient   _client;         // côté invité : la connexion vers l'hôte
        private NetworkStream _serverStream;
        private NetworkStream _clientStream;

        private Thread _acceptThread;
        private Thread _serverReadThread;
        private Thread _clientReadThread;

        public readonly ConcurrentQueue<(MsgId id, byte[] payload)> Incoming
            = new ConcurrentQueue<(MsgId, byte[])>();

        public Action OnRemoteConnected;
        public Action OnRemoteDisconnected;
        public Action OnConnectedToHost;

        public bool IsServerListening { get; private set; }
        public bool IsClientConnected { get; private set; }
        public bool HasRemotePeer      => _serverPeer != null && _serverPeer.Connected;

        private volatile bool _stopRequested;

        // ── Côté hôte ─────────────────────────────────────────────────────────
        public void StartHost(int port)
        {
            _stopRequested = false;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            IsServerListening = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "CoopAccept" };
            _acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (!_stopRequested)
            {
                try
                {
                    if (!_listener.Pending()) { Thread.Sleep(50); continue; }
                    _serverPeer = _listener.AcceptTcpClient();
                    _serverPeer.NoDelay = true;
                    _serverStream = _serverPeer.GetStream();
                    _serverReadThread = new Thread(() => ReadLoop(_serverStream, true))
                        { IsBackground = true, Name = "CoopServerRead" };
                    _serverReadThread.Start();
                    OnRemoteConnected?.Invoke();
                }
                catch { /* listener closed */ break; }
            }
        }

        // ── Côté invité ──────────────────────────────────────────────────────
        public void ConnectClient(string host, int port)
        {
            _stopRequested = false;
            _client = new TcpClient();
            _client.NoDelay = true;
            _client.BeginConnect(host, port, OnConnectCallback, null);
        }

        private void OnConnectCallback(IAsyncResult ar)
        {
            try
            {
                _client.EndConnect(ar);
                _clientStream = _client.GetStream();
                IsClientConnected = true;
                _clientReadThread = new Thread(() => ReadLoop(_clientStream, false))
                    { IsBackground = true, Name = "CoopClientRead" };
                _clientReadThread.Start();
                OnConnectedToHost?.Invoke();
            }
            catch (Exception)
            {
                IsClientConnected = false;
            }
        }

        // ── Boucle de lecture commune ─────────────────────────────────────────
        private void ReadLoop(NetworkStream stream, bool isServer)
        {
            try
            {
                var br = new BinaryReader(stream);
                while (!_stopRequested)
                {
                    int len = br.ReadInt32();
                    if (len <= 0 || len > 1 << 20) break;
                    byte msgId = br.ReadByte();
                    byte[] payload = br.ReadBytes(len - 1);
                    Incoming.Enqueue(((MsgId)msgId, payload));
                }
            }
            catch
            {
                // socket fermé
            }
            finally
            {
                if (isServer)
                {
                    OnRemoteDisconnected?.Invoke();
                    try { _serverPeer?.Close(); } catch { }
                    _serverPeer = null;
                }
                else
                {
                    IsClientConnected = false;
                    OnRemoteDisconnected?.Invoke();
                    try { _client?.Close(); } catch { }
                    _client = null;
                }
            }
        }

        // ── Envoi ────────────────────────────────────────────────────────────
        public void Send<T>(T msg) where T : INetMessage
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)msg.Id);
            msg.Write(bw);
            byte[] payload = ms.ToArray();

            using var frame = new MemoryStream();
            using var fw = new BinaryWriter(frame);
            fw.Write(payload.Length);
            fw.Write(payload);
            byte[] data = frame.ToArray();

            var s = _serverStream ?? _clientStream;
            if (s == null) return;
            try { s.Write(data, 0, data.Length); }
            catch { /* socket fermé */ }
        }

        public void Stop()
        {
            _stopRequested = true;
            try { _listener?.Stop(); } catch { }
            try { _serverPeer?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            IsServerListening = false;
            IsClientConnected = false;
            _listener = null; _serverPeer = null; _client = null;
        }
    }
}
