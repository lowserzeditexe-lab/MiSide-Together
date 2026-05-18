using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MiSideCoop.Network
{
    /// <summary>
    /// Transport TCP relay-routed (v1.3.0+).
    ///
    /// Architecture : il n'y a plus de mode "hôte qui écoute". Les deux pairs
    /// se connectent au même serveur de relais (port unique, voir relay-server.js)
    /// et envoient un handshake binaire de 12 octets identifiant leur rôle
    /// (HOST ou JOIN) et le code de room. Le relais pair les deux sockets et
    /// route les paquets applicatifs en transparent.
    ///
    /// Avantage : aucun NAT/port-forwarding chez les joueurs. Seul l'opérateur
    /// du relais ouvre un port (côté FAI).
    ///
    /// Protocole handshake :
    ///   offset 0..3 : "MTOG"  (4 octets ASCII)
    ///   offset 4    : 0x01 HOST | 0x02 JOIN
    ///   offset 5    : 0x00 (reserved)
    ///   offset 6..11: code 6 chars ASCII uppercase
    ///
    /// Format des trames applicatives (inchangé depuis v1.0) :
    ///   [int32 length][byte msgId][payload]
    /// </summary>
    public class CoopTcpTransport
    {
        private const byte ROLE_HOST  = 0x01;
        private const byte ROLE_JOIN  = 0x02;
        private static readonly byte[] MAGIC = Encoding.ASCII.GetBytes("MTOG");
        private const int HANDSHAKE_SIZE = 12;

        private TcpClient     _socket;
        private NetworkStream _stream;
        private Thread        _readThread;
        private volatile bool _stopRequested;

        public readonly ConcurrentQueue<(MsgId id, byte[] payload)> Incoming
            = new ConcurrentQueue<(MsgId, byte[])>();

        public Action OnRemoteConnected;
        public Action OnRemoteDisconnected;
        public Action OnConnectedToHost;
        public Action<string> OnConnectionError;

        public bool IsConnected => _socket != null && _socket.Connected;

        // ─────────────────────────────────────────────────────────────────────
        // API publique
        // ─────────────────────────────────────────────────────────────────────
        public void StartHost(string relayHost, int relayPort, string roomCode)
        {
            ConnectToRelay(relayHost, relayPort, ROLE_HOST, roomCode, isHost: true);
        }

        public void StartGuest(string relayHost, int relayPort, string roomCode)
        {
            ConnectToRelay(relayHost, relayPort, ROLE_JOIN, roomCode, isHost: false);
        }

        public void Stop()
        {
            _stopRequested = true;
            try { _stream?.Close(); } catch { /* ignore */ }
            try { _socket?.Close(); } catch { /* ignore */ }
            _stream = null;
            _socket = null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Connexion + handshake (background thread)
        // ─────────────────────────────────────────────────────────────────────
        private void ConnectToRelay(string host, int port, byte role, string code, bool isHost)
        {
            _stopRequested = false;
            new Thread(() =>
            {
                try
                {
                    var c = new TcpClient { NoDelay = true };
                    var connectTask = c.ConnectAsync(host, port);
                    if (!connectTask.Wait(8000))
                    {
                        try { c.Close(); } catch { /* ignore */ }
                        OnConnectionError?.Invoke($"Relay '{host}:{port}' unreachable (timeout).");
                        return;
                    }
                    _socket = c;
                    _stream = c.GetStream();

                    // Send handshake
                    var hs = new byte[HANDSHAKE_SIZE];
                    Buffer.BlockCopy(MAGIC, 0, hs, 0, 4);
                    hs[4] = role;
                    hs[5] = 0x00;
                    var codeBytes = Encoding.ASCII.GetBytes(code.ToUpperInvariant().PadRight(6).Substring(0, 6));
                    Buffer.BlockCopy(codeBytes, 0, hs, 6, 6);
                    _stream.Write(hs, 0, hs.Length);
                    _stream.Flush();

                    // Start reader. The relay won't send us anything until the
                    // other peer connects — so the first read will block until
                    // pairing happens. That's our cue that the session is live.
                    _readThread = new Thread(() => ReadLoop(_stream, isHost))
                    {
                        IsBackground = true,
                        Name = isHost ? "CoopHostRead" : "CoopGuestRead"
                    };
                    _readThread.Start();

                    if (isHost) OnRemoteConnected?.Invoke();
                    else        OnConnectedToHost?.Invoke();
                }
                catch (Exception ex)
                {
                    OnConnectionError?.Invoke($"Connection to relay failed: {ex.Message}");
                }
            }) { IsBackground = true, Name = "CoopConnect" }.Start();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Lecture
        // ─────────────────────────────────────────────────────────────────────
        private void ReadLoop(NetworkStream stream, bool isHost)
        {
            try
            {
                var br = new BinaryReader(stream);
                while (!_stopRequested)
                {
                    int len = br.ReadInt32();
                    if (len <= 0 || len > 1 << 20) break;
                    byte msgId    = br.ReadByte();
                    byte[] payload = br.ReadBytes(len - 1);
                    Incoming.Enqueue(((MsgId)msgId, payload));
                }
            }
            catch
            {
                // socket fermé / pair déconnecté
            }
            finally
            {
                OnRemoteDisconnected?.Invoke();
                try { stream?.Close(); } catch { /* ignore */ }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Envoi
        // ─────────────────────────────────────────────────────────────────────
        public void Send<T>(T msg) where T : INetMessage
        {
            var s = _stream;
            if (s == null) return;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)msg.Id);
            msg.Write(bw);
            byte[] payload = ms.ToArray();

            using var frame = new MemoryStream();
            using var fw    = new BinaryWriter(frame);
            fw.Write(payload.Length);
            fw.Write(payload);
            byte[] data = frame.ToArray();

            try { s.Write(data, 0, data.Length); }
            catch { /* socket fermé */ }
        }
    }
}
