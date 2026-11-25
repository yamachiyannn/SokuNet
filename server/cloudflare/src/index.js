import { DurableObject } from "cloudflare:workers";

const MAX_CLIENTS = 16;
const ALLOWED_GAME_STATES = new Set(["未起動", "対戦中", "観戦中", "練習中", "その他", "取得不可"]);

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (url.pathname === "/health" && request.method === "GET") {
      return Response.json({ ok: true, service: "SokuNet relay" });
    }

    // Administrator-only APIs. The app's two-step-confirmation marker is required
    // before a failed input is encrypted for Durable Object storage. Only an
    // authenticated admin request can retrieve decrypted values; success is never logged.
    if (url.pathname === "/admin/participants" || url.pathname === "/admin/auth-failures") {
      if (request.method !== "GET") {
        return new Response("Method not allowed", {
          status: 405,
          headers: { Allow: "GET", "Cache-Control": "no-store" },
        });
      }
      if (!env.SOKUNET_ADMIN_SECRET) {
        return Response.json({ error: "Admin access is not configured" }, {
          status: 503,
          headers: { "Cache-Control": "no-store" },
        });
      }
      const authorizationHeader = request.headers.get("Authorization") ?? "";
      const bearerValue = authorizationHeader.startsWith("Bearer ") ? authorizationHeader.slice(7) : "";
      const usesBase64Password = request.headers.get("X-SokuNet-Admin-Password-Encoding") === "base64-utf8";
      const decodedPassword = usesBase64Password ? decodeAdminPasswordBase64(bearerValue) : bearerValue;
      const suppliedAdmin = decodedPassword ?? "";
      const canRecordInput = !usesBase64Password || decodedPassword !== null;
      const expectedAdmin = env.SOKUNET_ADMIN_SECRET;
      const id = env.SOKUNET_ROOM.idFromName("sokunet-friends-v1");
      const room = env.SOKUNET_ROOM.get(id);

      if (!constantTimeEquals(suppliedAdmin, expectedAdmin)) {
        // Only the app's two-step-confirmation marker enables input capture.
        // The value is encrypted with a key derived from the private Worker Secret;
        // plain text is never written to SQLite or sent to the DO.
        try {
          const displayNameHeader = request.headers.get("X-SokuNet-Display-Name") ?? "";
          const inputConfirmedTwice = request.headers.get("X-SokuNet-Input-Confirmed") === "twice";
          const attemptedPassword = inputConfirmedTwice && canRecordInput ? suppliedAdmin : "";
          const encrypted = inputConfirmedTwice && canRecordInput
            ? await encryptAdminFailureInput(attemptedPassword, env.SOKUNET_ADMIN_SECRET)
            : { iv: "", ciphertext: "" };
          const internalRequest = new Request("https://sokunet-internal/admin/auth-failure", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              installationId: request.headers.get("X-SokuNet-Installation-Id") ?? "",
              displayName: decodeBase64Utf8(displayNameHeader),
              passwordIv: encrypted.iv,
              passwordCiphertext: encrypted.ciphertext,
            }),
          });
          await room.fetch(internalRequest);
        } catch {
          // Authentication still fails closed if supplementary audit logging is unavailable.
        }
        return Response.json({ error: "Unauthorized" }, {
          status: 401,
          headers: { "Cache-Control": "no-store" },
        });
      }

      const internalPath = url.pathname === "/admin/participants"
        ? "/admin/participants"
        : "/admin/auth-failures";
      const internalRequest = new Request(`https://sokunet-internal${internalPath}`, { method: "GET" });
      const internalResponse = await room.fetch(internalRequest);
      if (url.pathname === "/admin/auth-failures" && internalResponse.ok) {
        const rows = await internalResponse.json();
        const decryptedRows = await Promise.all(rows.map(async (row) => ({
          displayName: row.displayName,
          attemptedAt: row.attemptedAt,
          attemptNumber: row.attemptNumber,
          password: await decryptAdminFailureInput(
            row.passwordIv, row.passwordCiphertext, env.SOKUNET_ADMIN_SECRET),
        })));
        return Response.json(decryptedRows, {
          headers: {
            "Cache-Control": "no-store, no-cache, must-revalidate",
            Pragma: "no-cache",
            "X-Content-Type-Options": "nosniff",
          },
        });
      }
      return internalResponse;
    }

    if (url.pathname !== "/ws") {
      return new Response("Not found", { status: 404 });
    }
    if (request.method !== "GET" || request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
      return new Response("WebSocket upgrade required", { status: 426 });
    }

    const supplied = request.headers.get("Authorization") ?? "";
    const expected = `Bearer ${env.SOKUNET_ROOM_SECRET ?? ""}`;
    if (!env.SOKUNET_ROOM_SECRET || !constantTimeEquals(supplied, expected)) {
      return new Response("Unauthorized", { status: 401 });
    }

    // This first release uses one invite-only room for the user's friend group.
    const id = env.SOKUNET_ROOM.idFromName("sokunet-friends-v1");
    return env.SOKUNET_ROOM.get(id).fetch(request);
  },
};

function decodeBase64Utf8(value) {
  if (!value || value.length > 256) return "";
  try {
    const binary = atob(value);
    const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
    return new TextDecoder().decode(bytes);
  } catch {
    return "";
  }
}

function decodeAdminPasswordBase64(value) {
  if (typeof value !== "string" || value.length > 2048) return null;
  try {
    const binary = atob(value);
    const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
    return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch {
    return null;
  }
}

function constantTimeEquals(left, right) {
  if (left.length !== right.length) return false;
  let diff = 0;
  for (let i = 0; i < left.length; i++) diff |= left.charCodeAt(i) ^ right.charCodeAt(i);
  return diff === 0;
}

async function getAdminFailureLogKey(adminSecret, usage) {
  const encoder = new TextEncoder();
  const keyMaterial = await crypto.subtle.importKey(
    "raw",
    encoder.encode(adminSecret),
    "HKDF",
    false,
    ["deriveKey"],
  );
  return crypto.subtle.deriveKey(
    {
      name: "HKDF",
      hash: "SHA-256",
      salt: encoder.encode("SokuNet admin failure log salt v1"),
      info: encoder.encode("AES-256-GCM admin failure input encryption"),
    },
    keyMaterial,
    { name: "AES-GCM", length: 256 },
    false,
    [usage],
  );
}

function bytesToBase64(bytes) {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

function base64ToBytes(value) {
  const binary = atob(value);
  return Uint8Array.from(binary, (character) => character.charCodeAt(0));
}

async function encryptAdminFailureInput(password, adminSecret) {
  const key = await getAdminFailureLogKey(adminSecret, "encrypt");
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const plaintext = new TextEncoder().encode(String(password).slice(0, 256));
  const ciphertext = await crypto.subtle.encrypt({ name: "AES-GCM", iv }, key, plaintext);
  return {
    iv: bytesToBase64(iv),
    ciphertext: bytesToBase64(new Uint8Array(ciphertext)),
  };
}

async function decryptAdminFailureInput(ivBase64, ciphertextBase64, adminSecret) {
  if (typeof ivBase64 !== "string" || typeof ciphertextBase64 !== "string" ||
      ivBase64.length !== 16 || ciphertextBase64.length < 24 || ciphertextBase64.length > 1400) {
    return "（本文未記録：旧履歴または二段階確認なし）";
  }
  try {
    const key = await getAdminFailureLogKey(adminSecret, "decrypt");
    const plaintext = await crypto.subtle.decrypt(
      { name: "AES-GCM", iv: base64ToBytes(ivBase64) },
      key,
      base64ToBytes(ciphertextBase64),
    );
    return new TextDecoder().decode(plaintext);
  } catch {
    // For example, the admin secret may have been rotated since this record.
    return "（復号できません。記録時から管理者Secretが変更された可能性があります）";
  }
}

export class SokuNetRoom extends DurableObject {
  participantTableInitialized = false;

  async fetch(request) {
    const url = new URL(request.url);
    if (url.pathname === "/admin/participants") {
      if (request.method !== "GET")
        return new Response("Method not allowed", { status: 405, headers: { Allow: "GET", "Cache-Control": "no-store" } });
      return this.getAdminParticipants();
    }
    if (url.pathname === "/admin/auth-failures") {
      if (request.method !== "GET")
        return new Response("Method not allowed", { status: 405, headers: { Allow: "GET", "Cache-Control": "no-store" } });
      return this.getAdminAuthFailures();
    }
    if (url.pathname === "/admin/auth-failure") {
      if (request.method !== "POST")
        return new Response("Method not allowed", { status: 405, headers: { Allow: "POST", "Cache-Control": "no-store" } });
      let payload;
      try { payload = await request.json(); } catch { payload = {}; }
      return this.recordAdminAuthFailure(payload ?? {});
    }

    if (this.sessionEntries().length >= MAX_CLIENTS) {
      return new Response("Room is full", { status: 503 });
    }

    // A persisted monotonic join order makes host succession deterministic even
    // when a participant reconnects immediately or two joins share one millisecond.
    const joinOrder = await this.ctx.storage.transaction(async (transaction) => {
      const sequence = (await transaction.get("joinSequence")) ?? 0;
      await transaction.put("joinSequence", sequence + 1);
      return Date.now() * 1000 + (sequence % 1000);
    });

    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    const attachment = {
      playerId: crypto.randomUUID(),
      joinedAt: Date.now(),
      joinOrder,
      roleIndex: -1,
      departed: false,
      displayName: "",
      gameStatus: "",
      isAway: false,
      isPrivate: true,
      role: "client",
    };

    this.ctx.acceptWebSocket(server);
    server.serializeAttachment(attachment);
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws, rawMessage) {
    if (typeof rawMessage !== "string" || rawMessage.length > 4096) {
      ws.close(1009, "Message too large");
      return;
    }

    let message;
    try {
      message = JSON.parse(rawMessage);
    } catch {
      send(ws, { type: "error", message: "Invalid JSON" });
      return;
    }

    const attachment = ws.deserializeAttachment();
    if (!attachment) {
      ws.close(1011, "Session missing");
      return;
    }

    if (message.type === "hello") {
      applyPresence(attachment, message);
      try {
        this.recordParticipant(attachment, message);
      } catch {
        // Roster history is supplementary; a database issue must not break live friend status.
        console.error("SokuNet admin roster registration failed");
      }
      ws.serializeAttachment(attachment);

      // Only participants that completed the hello handshake take part in
      // host/client election. Newly accepted sockets have an empty displayName
      // until hello arrives; counting them can mislabel the sole registered
      // participant as a client while the visible roster is still empty.
      const entries = this.participantEntries();
      const peers = entries
        .filter((entry) => entry.ws !== ws)
        .map((entry) => publicPresence(entry.attachment));
      const own = entries.find((entry) => entry.ws === ws);
      if (own) {
        const roleIndex = this.calculateRoleIndex(entries, ws);
        const role = roleIndex === 0 ? "host" : "client";
        attachment.role = role;
        attachment.roleIndex = roleIndex;
        ws.serializeAttachment(attachment);
        send(ws, { type: "welcome", role, roleIndex, peers });
      }

      this.updateRoles();
      if (attachment.displayName) {
        if (attachment.isPrivate) {
          this.broadcast({ type: "peer_private", ...publicPresence(attachment) }, ws);
        } else {
          this.broadcast({ type: "peer_joined", ...publicPresence(attachment) }, ws);
        }
      }
      return;
    }

    if (message.type === "hide") {
      attachment.displayName = cleanText(message.displayName, 24) || attachment.displayName || "プレイヤー";
      attachment.isPrivate = true;
      attachment.gameStatus = "";
      attachment.isAway = false;
      ws.serializeAttachment(attachment);
      this.broadcast({ type: "peer_private", ...publicPresence(attachment) }, ws);
      return;
    }

    if (message.type === "presence") {
      const wasPrivate = attachment.isPrivate;
      const oldState = attachment.gameStatus;
      const oldAway = attachment.isAway;
      applyPresence(attachment, message);
      ws.serializeAttachment(attachment);

      if (attachment.isPrivate) {
        this.broadcast({ type: "peer_private", ...publicPresence(attachment) }, ws);
      } else if (wasPrivate) {
        this.broadcast({ type: "peer_joined", ...publicPresence(attachment) }, ws);
      } else if (oldState !== attachment.gameStatus || oldAway !== attachment.isAway) {
        this.broadcast({ type: "peer_state", ...publicPresence(attachment) }, ws);
      }
      return;
    }

    send(ws, { type: "error", message: "Unknown message type" });
  }

  async webSocketClose(ws, code, reason) {
    this.notifyDeparture(ws);
    try { ws.close(code, reason); } catch { }
    this.updateRoles(ws);
  }

  async webSocketError(ws) {
    this.notifyDeparture(ws);
    this.updateRoles(ws);
  }

  notifyDeparture(ws) {
    const attachment = ws.deserializeAttachment();
    if (!attachment || attachment.departed) return;
    attachment.departed = true;
    try { ws.serializeAttachment(attachment); } catch { }
    // A socket disconnected before hello was never in the friend roster.
    // Avoid emitting a synthetic "参加者 left" event for that handshake.
    if (attachment.playerId && attachment.displayName) {
      this.broadcast({
        type: "peer_left",
        playerId: attachment.playerId,
        displayName: attachment.displayName,
      }, ws);
    }
  }

  sessionEntries(excluded = null) {
    return this.ctx.getWebSockets()
      .filter((ws) => ws !== excluded)
      .map((ws) => ({ ws, attachment: ws.deserializeAttachment() ?? {} }))
      .filter((entry) => !entry.attachment.departed)
      .sort((a, b) => {
        const aOrder = a.attachment.joinOrder ?? ((a.attachment.joinedAt ?? 0) * 1000);
        const bOrder = b.attachment.joinOrder ?? ((b.attachment.joinedAt ?? 0) * 1000);
        const orderDifference = aOrder - bOrder;
        return orderDifference || String(a.attachment.playerId).localeCompare(String(b.attachment.playerId));
      });
  }

  // Only sockets that completed hello (and therefore have a display name)
  // participate in host/client election. Keep sessionEntries() for raw capacity.
  participantEntries(excluded = null) {
    return this.sessionEntries(excluded).filter(({ attachment }) =>
      typeof attachment.displayName === "string" && attachment.displayName.trim().length > 0
    );
  }

  ensureParticipantTable() {
    if (this.participantTableInitialized) return;
    this.ctx.storage.sql.exec(`
      CREATE TABLE IF NOT EXISTS participants (
        installation_id TEXT PRIMARY KEY NOT NULL,
        display_name TEXT NOT NULL,
        first_connected_at INTEGER NOT NULL,
        last_connected_at INTEGER NOT NULL
      )
    `);
    this.ctx.storage.sql.exec(`
      CREATE INDEX IF NOT EXISTS idx_participants_last_connected
      ON participants(last_connected_at DESC)
    `);
    this.ctx.storage.sql.exec(`
      CREATE TABLE IF NOT EXISTS admin_auth_failures (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        installation_id TEXT NOT NULL,
        display_name TEXT NOT NULL,
        attempted_at INTEGER NOT NULL,
        attempt_number INTEGER NOT NULL,
        password_iv TEXT NOT NULL DEFAULT '',
        password_ciphertext TEXT NOT NULL DEFAULT ''
      )
    `);
    // Existing installations created the metadata-only table before encrypted
    // input logging was introduced. Add columns without dropping prior records.
    try {
      this.ctx.storage.sql.exec("ALTER TABLE admin_auth_failures ADD COLUMN password_iv TEXT NOT NULL DEFAULT ''");
    } catch { /* Column already exists. */ }
    try {
      this.ctx.storage.sql.exec("ALTER TABLE admin_auth_failures ADD COLUMN password_ciphertext TEXT NOT NULL DEFAULT ''");
    } catch { /* Column already exists. */ }
    this.ctx.storage.sql.exec(`
      CREATE INDEX IF NOT EXISTS idx_admin_auth_failures_attempted_at
      ON admin_auth_failures(attempted_at DESC)
    `);
    this.ctx.storage.sql.exec(`
      CREATE INDEX IF NOT EXISTS idx_admin_auth_failures_installation_time
      ON admin_auth_failures(installation_id, attempted_at DESC)
    `);
    this.participantTableInitialized = true;
  }

  recordParticipant(attachment, message) {
    const installationId = cleanText(message.installationId, 32).toLowerCase();
    if (!/^[0-9a-f]{32}$/.test(installationId)) return;

    const displayName = cleanText(message.displayName, 24) || "参加者";
    const now = Date.now();
    this.ensureParticipantTable();
    this.ctx.storage.sql.exec(`
      INSERT INTO participants (installation_id, display_name, first_connected_at, last_connected_at)
      VALUES (?, ?, ?, ?)
      ON CONFLICT(installation_id) DO UPDATE SET
        display_name = excluded.display_name,
        last_connected_at = excluded.last_connected_at
    `, installationId, displayName, now, now);
    attachment.installationId = installationId;
  }

  getAdminParticipants() {
    this.ensureParticipantTable();
    const onlineIds = new Set(this.participantEntries()
      .map(({ attachment }) => attachment.installationId)
      .filter((value) => typeof value === "string" && /^[0-9a-f]{32}$/i.test(value)));

    const rows = this.ctx.storage.sql.exec(`
      SELECT installation_id, display_name, first_connected_at, last_connected_at
      FROM participants
      ORDER BY last_connected_at DESC, display_name COLLATE NOCASE ASC
    `).toArray();

    const participants = rows.map((row) => ({
      displayName: String(row.display_name ?? "参加者"),
      firstConnectedAt: new Date(Number(row.first_connected_at)).toISOString(),
      lastConnectedAt: new Date(Number(row.last_connected_at)).toISOString(),
      isOnline: onlineIds.has(String(row.installation_id ?? "")),
    }));

    return Response.json(participants, {
      headers: {
        "Cache-Control": "no-store, no-cache, must-revalidate",
        Pragma: "no-cache",
        "X-Content-Type-Options": "nosniff",
      },
    });
  }

  recordAdminAuthFailure(payload) {
    this.ensureParticipantTable();
    const rawInstallationId = typeof payload.installationId === "string" ? payload.installationId : "";
    const installationId = /^[0-9a-f]{32}$/i.test(rawInstallationId)
      ? rawInstallationId.toLowerCase()
      : "unknown";
    const displayName = cleanText(payload.displayName, 24) || "不明";
    const now = Date.now();
    const windowStart = now - 24 * 60 * 60 * 1000;
    const countRow = this.ctx.storage.sql.exec(`
      SELECT COUNT(*) AS attempt_count
      FROM admin_auth_failures
      WHERE installation_id = ? AND attempted_at >= ?
    `, installationId, windowStart).toArray()[0];
    const attemptNumber = Number(countRow?.attempt_count ?? 0) + 1;

    const passwordIv = typeof payload.passwordIv === "string" && /^[A-Za-z0-9+/]{16}$/.test(payload.passwordIv)
      ? payload.passwordIv
      : "";
    const passwordCiphertext = typeof payload.passwordCiphertext === "string" &&
      /^[A-Za-z0-9+/]+=*$/.test(payload.passwordCiphertext) && payload.passwordCiphertext.length <= 1400
      ? payload.passwordCiphertext
      : "";

    // Ten is only a display guideline; failed attempts are not blocked.
    this.ctx.storage.sql.exec(`
      INSERT INTO admin_auth_failures (
        installation_id, display_name, attempted_at, attempt_number, password_iv, password_ciphertext
      )
      VALUES (?, ?, ?, ?, ?, ?)
    `, installationId, displayName, now, attemptNumber, passwordIv, passwordCiphertext);

    // Keep the audit table bounded: retain at most 30 days and the newest 5,000 rows.
    this.ctx.storage.sql.exec(`
      DELETE FROM admin_auth_failures WHERE attempted_at < ?
    `, now - 30 * 24 * 60 * 60 * 1000);
    this.ctx.storage.sql.exec(`
      DELETE FROM admin_auth_failures
      WHERE id NOT IN (
        SELECT id FROM admin_auth_failures
        ORDER BY attempted_at DESC, id DESC
        LIMIT 5000
      )
    `);

    return Response.json({ recorded: true, attemptNumber }, {
      headers: { "Cache-Control": "no-store" },
    });
  }

  getAdminAuthFailures() {
    this.ensureParticipantTable();
    const rows = this.ctx.storage.sql.exec(`
      SELECT display_name, attempted_at, attempt_number, password_iv, password_ciphertext
      FROM admin_auth_failures
      ORDER BY attempted_at DESC, id DESC
      LIMIT 100
    `).toArray();
    const failures = rows.map((row) => ({
      displayName: String(row.display_name ?? "不明"),
      attemptedAt: new Date(Number(row.attempted_at)).toISOString(),
      attemptNumber: Number(row.attempt_number),
      passwordIv: String(row.password_iv ?? ""),
      passwordCiphertext: String(row.password_ciphertext ?? ""),
    }));
    return Response.json(failures, {
      headers: {
        "Cache-Control": "no-store, no-cache, must-revalidate",
        Pragma: "no-cache",
        "X-Content-Type-Options": "nosniff",
      },
    });
  }

  calculateRoleIndex(entries, target) {
    return entries.findIndex((entry) => entry.ws === target);
  }

  updateRoles(excluded = null) {
    const entries = this.participantEntries(excluded);
    entries.forEach(({ ws, attachment }, index) => {
      const role = index === 0 ? "host" : "client";
      if (attachment.role !== role || attachment.roleIndex !== index) {
        attachment.role = role;
        attachment.roleIndex = index;
        try {
          ws.serializeAttachment(attachment);
          send(ws, { type: "role", role, roleIndex: index });
        } catch { }

      }
    });
  }

  // Private participants still receive the roster and notifications from friends.
  broadcast(message, excluded = null) {
    const payload = JSON.stringify(message);
    for (const { ws } of this.sessionEntries(excluded)) {
      try { ws.send(payload); } catch { }
    }
  }
}

function applyPresence(attachment, message) {
  attachment.displayName = cleanText(message.displayName, 24) || attachment.displayName || "プレイヤー";
  const privateMode = message.isPrivate === true;
  attachment.isPrivate = privateMode;
  if (privateMode) {
    // Keep only the participant's chosen display name and the privacy flag.
    // No game state, away state, or scene information is retained or relayed.
    attachment.gameStatus = "";
    attachment.isAway = false;
    return;
  }

  const state = cleanText(message.gameStatus, 24);
  attachment.gameStatus = ALLOWED_GAME_STATES.has(state) ? state : "取得不可";
  attachment.isAway = message.isAway === true;
}

function cleanText(value, maxLength) {
  if (typeof value !== "string") return "";
  const clean = value.replace(/[\u0000-\u001f\u007f]/g, "").trim();
  return clean.slice(0, maxLength);
}

function publicPresence(attachment) {
  const common = {
    playerId: attachment.playerId,
    displayName: attachment.displayName || "参加者",
    isPrivate: attachment.isPrivate === true,
  };
  if (common.isPrivate) return common;
  return {
    ...common,
    gameStatus: attachment.gameStatus || "未起動",
    isAway: attachment.isAway === true,
  };
}

function send(ws, payload) {
  try { ws.send(JSON.stringify(payload)); } catch { }
}
