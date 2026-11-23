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
    const sessions = this.ctx.getWebSockets();
    if (sessions.length >= MAX_CLIENTS) {
      return new Response("Room is full", { status: 503 });
    }

    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    const attachment = {
      playerId: crypto.randomUUID(),
      joinedAt: Date.now(),
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

      const entries = this.sessionEntries();
      const peers = entries
        .filter((entry) => entry.ws !== ws && entry.attachment.displayName)
        .map((entry) => publicPresence(entry.attachment));
      const own = entries.find((entry) => entry.ws === ws);
      if (own) {
        const role = this.calculateRole(entries, ws);
        attachment.role = role;
        ws.serializeAttachment(attachment);
        send(ws, { type: "welcome", role, peers });
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
    const attachment = ws.deserializeAttachment();
    if (attachment?.playerId) {
      this.broadcast({
        type: "peer_left",
        playerId: attachment.playerId,
        displayName: attachment.displayName || "参加者",
      }, ws);
    }
    try { ws.close(code, reason); } catch { }
    this.updateRoles(ws);
  }

  async webSocketError(ws) {
    const attachment = ws.deserializeAttachment();
    if (attachment?.playerId) {
      this.broadcast({
        type: "peer_left",
        playerId: attachment.playerId,
        displayName: attachment.displayName || "参加者",
      }, ws);
    }
    this.updateRoles(ws);
  }

  sessionEntries(excluded = null) {
    return this.ctx.getWebSockets()
      .filter((ws) => ws !== excluded)
      .map((ws) => ({ ws, attachment: ws.deserializeAttachment() ?? {} }))
      .sort((a, b) => (a.attachment.joinedAt ?? 0) - (b.attachment.joinedAt ?? 0));
  }

  calculateRole(entries, target) {
    return entries.findIndex((entry) => entry.ws === target) === 0 ? "host" : "client";
  }

  updateRoles(excluded = null) {
    const entries = this.sessionEntries(excluded);
    entries.forEach(({ ws, attachment }, index) => {
      const role = index === 0 ? "host" : "client";
      if (attachment.role !== role) {
        attachment.role = role;
        ws.serializeAttachment(attachment);
        send(ws, { type: "role", role });
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
