import { DurableObject } from "cloudflare:workers";

const MAX_CLIENTS = 16;
const ALLOWED_GAME_STATES = new Set(["未起動", "対戦中", "観戦中", "練習中", "その他", "取得不可"]);

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (url.pathname === "/health" && request.method === "GET") {
      return Response.json({ ok: true, service: "SokuNet relay" });
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

function constantTimeEquals(left, right) {
  if (left.length !== right.length) return false;
  let diff = 0;
  for (let i = 0; i < left.length; i++) diff |= left.charCodeAt(i) ^ right.charCodeAt(i);
  return diff === 0;
}

export class SokuNetRoom extends DurableObject {
  async fetch(request) {
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
