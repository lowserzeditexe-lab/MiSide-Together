/**
 * relay-server.js — Serveur de relais TCP MiSide Together (v2)
 *
 * Architecture v1.3.0+ :
 *   • Plus de HTTP. Un seul port TCP brut écoute pour TOUT le trafic.
 *   • Chaque client (hôte OU invité) ouvre une connexion TCP au relais et
 *     envoie en premier un "handshake" de 12 octets :
 *
 *         offset 0..3   : magic "MTOG"  (MiSide Together)
 *         offset 4      : role byte     (0x01 = HOST, 0x02 = JOIN)
 *         offset 5      : reserved      (0x00)
 *         offset 6..11  : code 6 ASCII  (A-Z 0-9, uppercase, no I/O/0/1)
 *
 *   • Le relais maintient une map { code → socket_de_l_hote_en_attente }.
 *   • Quand le 2e participant (HOST attendant un JOIN, ou JOIN cherchant un
 *     HOST déjà présent) arrive, le relais "pipe" les deux sockets en plein
 *     duplex et toutes les données binaires applicatives passent en transparent.
 *   • Une fois pairé, le code est retiré de la map (la room est consommée).
 *
 * Démarrage : node relay-server.js
 * Port par défaut : 8001 (variable d'env PORT pour override).
 */

'use strict';

const net   = require('net');
const PORT  = parseInt(process.env.PORT || '8001', 10);
const HOST  = process.env.HOST || '0.0.0.0';

const MAGIC          = Buffer.from('MTOG');     // 4 octets
const ROLE_HOST      = 0x01;
const ROLE_JOIN      = 0x02;
const HANDSHAKE_SIZE = 12;
const HANDSHAKE_TIMEOUT_MS = 8000;
const ROOM_TTL_MS    = 2 * 60 * 60 * 1000;       // 2h

/** @type {Map<string, {socket: net.Socket, created: number}>} */
const waitingHosts = new Map();

// ─── Nettoyage des hôtes en attente trop longtemps ─────────────────────────
setInterval(() => {
  const now = Date.now();
  for (const [code, w] of waitingHosts) {
    if (now - w.created > ROOM_TTL_MS) {
      try { w.socket.destroy(); } catch { /* ignore */ }
      waitingHosts.delete(code);
      console.log(`[Relay] Room '${code}' expirée (>2h sans match).`);
    }
  }
}, 5 * 60 * 1000);

// ─── Helpers ───────────────────────────────────────────────────────────────
function readExact(socket, nBytes, timeoutMs) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let received = 0;
    let timer = null;

    const cleanup = () => {
      socket.removeListener('data', onData);
      socket.removeListener('error', onError);
      socket.removeListener('end', onEnd);
      if (timer) clearTimeout(timer);
    };

    const onData = (chunk) => {
      chunks.push(chunk);
      received += chunk.length;
      if (received >= nBytes) {
        cleanup();
        const full = Buffer.concat(chunks, received);
        const head = full.slice(0, nBytes);
        const rest = full.slice(nBytes);
        if (rest.length > 0) socket.unshift(rest);
        resolve(head);
      }
    };
    const onError = (err) => { cleanup(); reject(err); };
    const onEnd   = ()    => { cleanup(); reject(new Error('socket ended during handshake')); };

    socket.on('data',  onData);
    socket.on('error', onError);
    socket.on('end',   onEnd);
    if (timeoutMs > 0) {
      timer = setTimeout(() => { cleanup(); reject(new Error('handshake timeout')); }, timeoutMs);
    }
  });
}

function pipeSockets(a, b, code) {
  let closed = false;
  const tearDown = (reason) => {
    if (closed) return;
    closed = true;
    console.log(`[Relay] Session '${code}' terminée (${reason}).`);
    try { a.destroy(); } catch { /* ignore */ }
    try { b.destroy(); } catch { /* ignore */ }
  };

  a.on('error', (err) => tearDown(`A error: ${err.message}`));
  b.on('error', (err) => tearDown(`B error: ${err.message}`));
  a.on('end',   () => tearDown('A ended'));
  b.on('end',   () => tearDown('B ended'));
  a.on('close', () => tearDown('A closed'));
  b.on('close', () => tearDown('B closed'));

  a.pipe(b);
  b.pipe(a);
}

// ─── Serveur TCP ────────────────────────────────────────────────────────────
const server = net.createServer((socket) => {
  socket.setNoDelay(true);
  const peer = `${socket.remoteAddress}:${socket.remotePort}`;
  console.log(`[Relay] Nouvelle connexion de ${peer}.`);

  readExact(socket, HANDSHAKE_SIZE, HANDSHAKE_TIMEOUT_MS).then((hs) => {
    const magic = hs.slice(0, 4);
    if (!magic.equals(MAGIC)) {
      console.log(`[Relay] ${peer}: magic invalide. Connexion fermée.`);
      socket.destroy();
      return;
    }
    const role = hs[4];
    const code = hs.slice(6, 12).toString('ascii').toUpperCase();

    if (!/^[A-Z0-9]{6}$/.test(code)) {
      console.log(`[Relay] ${peer}: code '${code}' invalide.`);
      socket.destroy();
      return;
    }

    if (role === ROLE_HOST) {
      // Si un hôte est déjà en attente pour ce code, on remplace l'ancien.
      const existing = waitingHosts.get(code);
      if (existing) {
        console.log(`[Relay] Room '${code}' : ancien hôte remplacé.`);
        try { existing.socket.destroy(); } catch { /* ignore */ }
      }
      waitingHosts.set(code, { socket, created: Date.now() });
      console.log(`[Relay] HOST en attente sur room '${code}' (de ${peer}).`);
      socket.on('close', () => {
        const w = waitingHosts.get(code);
        if (w && w.socket === socket) {
          waitingHosts.delete(code);
          console.log(`[Relay] HOST '${code}' déconnecté avant match.`);
        }
      });
    } else if (role === ROLE_JOIN) {
      const host = waitingHosts.get(code);
      if (!host) {
        console.log(`[Relay] JOIN '${code}' échoué : pas d'hôte en attente.`);
        socket.destroy();
        return;
      }
      waitingHosts.delete(code);
      console.log(`[Relay] Pairing OK : room '${code}' (${peer} ↔ HOST).`);
      pipeSockets(host.socket, socket, code);
    } else {
      console.log(`[Relay] ${peer}: rôle inconnu ${role}. Fermé.`);
      socket.destroy();
    }
  }).catch((err) => {
    console.log(`[Relay] ${peer}: handshake échoué (${err.message}).`);
    try { socket.destroy(); } catch { /* ignore */ }
  });
});

server.listen(PORT, HOST, () => {
  console.log('');
  console.log('================================================');
  console.log(` MiSide Together — Relay TCP v2 (single-port)`);
  console.log('================================================');
  console.log(` Écoute sur ${HOST}:${PORT}`);
  console.log(` Protocole : HOST/JOIN binaire (cf. en-tête du fichier).`);
  console.log(` Le port doit être ouvert/forwardé sur votre routeur.`);
  console.log('================================================');
  console.log('');
});

server.on('error', (err) => {
  console.error(`[Relay] Erreur fatale : ${err.message}`);
  if (err.code === 'EADDRINUSE') {
    console.error(`[Relay] Port ${PORT} déjà utilisé. Variable PORT=xxxx pour changer.`);
  }
  process.exit(1);
});

process.on('SIGINT',  () => { console.log('\n[Relay] Arrêt.'); process.exit(0); });
process.on('SIGTERM', () => { console.log('\n[Relay] Arrêt.'); process.exit(0); });
