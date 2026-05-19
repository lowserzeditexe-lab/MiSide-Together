/**
 * relay-server.js — Serveur de relais MiSide Co-op
 *
 * Mappe les codes de room à 6 caractères aux adresses IP publiques des hôtes.
 * Démarrage : node relay-server.js
 * Port par défaut : 3000 (modifiable via variable d'env PORT)
 *
 * Endpoints :
 *   POST   /register         { "code": "XK4F2A", "port": 7777 }
 *   GET    /resolve/:code
 *   DELETE /room/:code
 *   GET    /health
 */

'use strict';

const http    = require('http');
const PORT    = parseInt(process.env.PORT || '3000', 10);
const ROOM_TTL = 2 * 60 * 60 * 1000;  // 2 heures en ms

/** @type {Map<string, {ip: string, port: number, created: number}>} */
const rooms = new Map();

// ── Nettoyage automatique des rooms expirées (toutes les 5 min) ──────────────
setInterval(() => {
  const now = Date.now();
  for (const [code, data] of rooms) {
    if (now - data.created > ROOM_TTL) {
      rooms.delete(code);
      console.log(`[Relay] Room ${code} expirée et supprimée.`);
    }
  }
}, 5 * 60 * 1000);

// ── Utilitaires ───────────────────────────────────────────────────────────────
function getClientIp(req) {
  const forwarded = req.headers['x-forwarded-for'];
  return forwarded ? forwarded.split(',')[0].trim() : req.socket.remoteAddress;
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end',  ()    => resolve(body));
    req.on('error', reject);
  });
}

function send(res, status, data) {
  const json = JSON.stringify(data);
  res.writeHead(status, {
    'Content-Type'                : 'application/json',
    'Access-Control-Allow-Origin' : '*',
    'Access-Control-Allow-Methods': 'GET, POST, DELETE, OPTIONS',
    'Content-Length'              : Buffer.byteLength(json)
  });
  res.end(json);
}

// ── Serveur HTTP ──────────────────────────────────────────────────────────────
const server = http.createServer(async (req, res) => {
  const method = req.method.toUpperCase();
  const url    = req.url.toLowerCase();

  // CORS preflight
  if (method === 'OPTIONS') {
    res.writeHead(204, { 'Access-Control-Allow-Origin': '*', 'Access-Control-Allow-Methods': 'GET, POST, DELETE, OPTIONS' });
    res.end();
    return;
  }

  // ── POST /register ─────────────────────────────────────────────────────────
  if (method === 'POST' && url === '/register') {
    let body;
    try { body = JSON.parse(await readBody(req)); }
    catch { return send(res, 400, { error: 'Corps JSON invalide.' }); }

    const { code, port } = body;
    if (typeof code !== 'string' || code.length !== 6) {
      return send(res, 400, { error: 'Le code doit être une chaîne de 6 caractères.' });
    }
    if (typeof port !== 'number' || port < 1 || port > 65535) {
      return send(res, 400, { error: 'Port invalide (1–65535).' });
    }

    const ip  = getClientIp(req);
    const key = code.toUpperCase();
    rooms.set(key, { ip, port, created: Date.now() });

    console.log(`[Relay] Room enregistrée : ${key} → ${ip}:${port}`);
    return send(res, 200, { success: true, code: key, ip, port });
  }

  // ── GET /resolve/:code ─────────────────────────────────────────────────────
  const resolveMatch = url.match(/^\/resolve\/([a-z0-9]{6})$/);
  if (method === 'GET' && resolveMatch) {
    const key  = resolveMatch[1].toUpperCase();
    const room = rooms.get(key);
    if (!room) return send(res, 404, { error: `Room '${key}' introuvable.` });

    console.log(`[Relay] Résolution : ${key} → ${room.ip}:${room.port}`);
    return send(res, 200, { ip: room.ip, port: room.port });
  }

  // ── DELETE /room/:code ─────────────────────────────────────────────────────
  const deleteMatch = url.match(/^\/room\/([a-z0-9]{6})$/);
  if (method === 'DELETE' && deleteMatch) {
    const key     = deleteMatch[1].toUpperCase();
    const existed = rooms.delete(key);
    if (!existed) return send(res, 404, { error: `Room '${key}' introuvable.` });

    console.log(`[Relay] Room supprimée : ${key}`);
    return send(res, 200, { success: true });
  }

  // ── GET /health ────────────────────────────────────────────────────────────
  if (method === 'GET' && url === '/health') {
    return send(res, 200, { status: 'ok', rooms: rooms.size, uptime: process.uptime() });
  }

  send(res, 404, { error: `Endpoint '${req.method} ${req.url}' non trouvé.` });
});

// ── Démarrage ─────────────────────────────────────────────────────────────────
server.listen(PORT, '0.0.0.0', () => {
  console.log(`\n================================================`);
  console.log(` MiSide Co-op — Serveur de relais v1.0.0`);
  console.log(`================================================`);
  console.log(` Écoute sur le port : ${PORT}`);
  console.log(` Endpoints disponibles :`);
  console.log(`   POST   /register`);
  console.log(`   GET    /resolve/:code`);
  console.log(`   DELETE /room/:code`);
  console.log(`   GET    /health`);
  console.log(`================================================\n`);
});

server.on('error', err => {
  console.error(`[Relay] Erreur fatale : ${err.message}`);
  if (err.code === 'EADDRINUSE') {
    console.error(`[Relay] Port ${PORT} déjà utilisé. Changez via : PORT=3001 node relay-server.js`);
  }
  process.exit(1);
});

process.on('SIGINT',  () => { console.log('\n[Relay] Arrêt du serveur.'); process.exit(0); });
process.on('SIGTERM', () => { console.log('\n[Relay] Arrêt du serveur.'); process.exit(0); });
