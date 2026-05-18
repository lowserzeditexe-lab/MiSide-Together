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
    /// ⚠️ THREADING — Toutes les notifications publiques (OnRemoteConnected,
    /// OnConnectedToHost, OnRemoteDisconnected, OnConnectionError) sont
    /// **pollées** via des flags volatiles consommés par le main thread Unity
    /// dans <c>CoopNetworkManager.Update()</c>. Les callbacks ne sont JAMAIS
    /// invoquées directement depuis le thread socket — sinon toute touche à
    /// une API Unity depuis le background thread cause un crash natif
    /// (STATUS_BREAKPOINT 0x80000003).
    ///
    /// Protocole handshake (12 octets) :
    ///   offset 0..3 : "MTOG"
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

        // ── Flags pollés par le main thread (cf. Pump() ci-dessous) ───────────
        private volatile bool _flagConnectedAsGuest;     // socket vers relais OK (côté guest)
        private volatile bool _flagRelayConnected;       // socket vers relais OK (côté host)
        private volatile bool _flagPeerJoined;           // 1ère trame applicative reçue
        private volatile bool _flagDisconnected;
        private volatile string _flagError;
        private volatile bool _peerJoinedFired;

        public Action OnRemoteConnected;    // peer effectivement pairé (côté HOST)
        public Action OnConnectedToHost;    // socket relais OK + handshake envoyé (côté GUEST)
        public Action OnRemoteDisconnected;
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

        /// <summary>
        /// À appeler depuis le main thread Unity à chaque Update().
        /// Drain les flags background → invoque les callbacks **sur le thread Unity**.
        /// </summary>
        public void Pump()
        {
            if (_flagError != null)
            {
                var err = _flagError;
                _flagError = null;
                OnConnectionError?.Invoke(err);
            }
            if (_flagConnectedAsGuest)
            {
                _flagConnectedAsGuest = false;
                OnConnectedToHost?.Invoke();
            }
            // côté HOST : on attend la 1ère trame entrante pour considérer
            // qu'un peer est vraiment pairé (le simple fait que le socket
            // relais soit ouvert ne signifie pas qu'un guest a rejoint).
            if (_flagPeerJoined && !_peerJoinedFired)
            {
                _peerJoinedFired = true;
                OnRemoteConnected?.Invoke();
            }
            if (_flagDisconnected)
            {
                _flagDisconnected = false;
                OnRemoteDisconnected?.Invoke();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Connexion + handshake (background thread)
        // ─────────────────────────────────────────────────────────────────────
        private void ConnectToRelay(string host, int port, byte role, string code, bool isHost)
        {
            _stopRequested = false;
            _peerJoinedFired = false;
            _flagConnectedAsGuest = false;
            _flagRelayConnected = false;
            _flagPeerJoined = false;
            _flagDisconnected = false;
            _flagError = null;

            new Thread(() =>
            {
                try
                {
                    var c = new TcpClient { NoDelay = true };
                    var connectTask = c.ConnectAsync(host, port);
                    if (!connectTask.Wait(8000))
                    {
                        try { c.Close(); } catch { /* ignore */ }
                        _flagError = $"Relay '{host}:{port}' unreachable (timeout).";
                        return;
                    }
                    if (connectTask.IsFaulted)
                    {
                        try { c.Close(); } catch { /* ignore */ }
                        _flagError = $"Relay connect error: {connectTask.Exception?.GetBaseException()?.Message ?? "unknown"}";
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

                    _flagRelayConnected = true;
                    if (!isHost)
                    {
                        // côté GUEST, le relais nous pair immédiatement (l'hôte
                        // était déjà en attente côté serveur) — on peut considérer
                        // qu'on est "connecté à l'hôte" dès le handshake envoyé.
                        _flagConnectedAsGuest = true;
                    }

                    // Start reader. The first frame received indicates that
                    // a peer is on the other side (relay finished pairing).
                    _readThread = new Thread(() => ReadLoop(_stream))
                    {
                        IsBackground = true,
                        Name = isHost ? "CoopHostRead" : "CoopGuestRead"
                    };
                    _readThread.Start();
                }
                catch (Exception ex)
                {
                    _flagError = $"Connection to relay failed: {ex.Message}";
                }
            }) { IsBackground = true, Name = "CoopConnect" }.Start();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Lecture
        // ─────────────────────────────────────────────────────────────────────
        private void ReadLoop(NetworkStream stream)
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
                    // 1ère trame applicative → un peer est confirmé pairé
                    _flagPeerJoined = true;
                }
            }
            catch
            {
                // socket fermé / pair déconnecté
            }
            finally
            {
                _flagDisconnected = true;
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
