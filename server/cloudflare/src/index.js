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

    // This deployment contains one private friend group. Add another named room only
    // if separate groups are needed in a later version.
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
      clientId: crypto.randomUUID(),
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
      const oldVisible = !attachment.isPrivate && attachment.displayName.length > 0;
      const previousState = attachment.gameStatus;
      applyPresence(attachment, message);
      ws.serializeAttachment(attachment);

      const peers = this.visibleSessions(ws).map(({ attachment: peer }) => publicPresence(peer));
      const entries = this.sessionEntries();
      const own = entries.find((entry) => entry.ws === ws);
      if (own) {
        own.attachment = attachment;
        const role = this.calculateRole(entries, ws);
        attachment.role = role;
        ws.serializeAttachment(attachment);
        send(ws, { type: "welcome", role, peers });
      }

      this.updateRoles();
      if (!oldVisible && !attachment.isPrivate && attachment.displayName) {
        this.broadcast({ type: "peer_joined", ...publicPresence(attachment) }, ws);
      } else if (oldVisible && !attachment.isPrivate && previousState !== attachment.gameStatus) {
        this.broadcast({ type: "peer_state", ...publicPresence(attachment) }, ws);
      }
      return;
    }

    if (message.type === "hide") {
      const wasVisible = !attachment.isPrivate && attachment.displayName.length > 0;
      attachment.isPrivate = true;
      attachment.displayName = "";
      attachment.gameStatus = "";
      attachment.isAway = false;
      ws.serializeAttachment(attachment);
      if (wasVisible) this.broadcast({ type: "peer_left", displayName: "参加者" }, ws);
      return;
    }

    if (message.type === "presence") {
      const wasVisible = !attachment.isPrivate && attachment.displayName.length > 0;
      const oldState = attachment.gameStatus;
      const oldAway = attachment.isAway;
      applyPresence(attachment, message);
      ws.serializeAttachment(attachment);
      const isVisible = !attachment.isPrivate && attachment.displayName.length > 0;

      if (wasVisible && !isVisible) {
        this.broadcast({ type: "peer_left", displayName: "参加者" }, ws);
      } else if (!wasVisible && isVisible) {
        this.broadcast({ type: "peer_joined", ...publicPresence(attachment) }, ws);
      } else if (wasVisible && isVisible && (oldState !== attachment.gameStatus || oldAway !== attachment.isAway)) {
        this.broadcast({ type: "peer_state", ...publicPresence(attachment) }, ws);
      }
      return;
    }

    send(ws, { type: "error", message: "Unknown message type" });
  }

  async webSocketClose(ws, code, reason) {
    const attachment = ws.deserializeAttachment();
    if (attachment && !attachment.isPrivate && attachment.displayName) {
      this.broadcast({ type: "peer_left", displayName: attachment.displayName }, ws);
    }
    // Compatibility dates on/after 2026-04-07 auto-acknowledge close frames;
    // calling close is harmless and keeps this robust on older compatibility dates.
    try { ws.close(code, reason); } catch { }
    this.updateRoles(ws);
  }

  async webSocketError(ws) {
    const attachment = ws.deserializeAttachment();
    if (attachment && !attachment.isPrivate && attachment.displayName) {
      this.broadcast({ type: "peer_left", displayName: attachment.displayName }, ws);
    }
    this.updateRoles(ws);
  }

  sessionEntries(excluded = null) {
    return this.ctx.getWebSockets()
      .filter((ws) => ws !== excluded)
      .map((ws) => ({ ws, attachment: ws.deserializeAttachment() ?? {} }))
      .sort((a, b) => (a.attachment.joinedAt ?? 0) - (b.attachment.joinedAt ?? 0));
  }

  visibleSessions(excluded = null) {
    return this.sessionEntries(excluded).filter(({ attachment }) =>
      !attachment.isPrivate && attachment.displayName);
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

  broadcast(message, excluded = null) {
    const payload = JSON.stringify(message);
    for (const { ws } of this.visibleSessions(excluded)) {
      try { ws.send(payload); } catch { }
    }
  }
}

function applyPresence(attachment, message) {
  const privateMode = message.isPrivate === true;
  attachment.isPrivate = privateMode;
  if (privateMode) {
    attachment.displayName = "";
    attachment.gameStatus = "";
    attachment.isAway = false;
    return;
  }

  attachment.displayName = cleanText(message.displayName, 24) || "プレイヤー";
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
  return {
    displayName: attachment.displayName,
    gameStatus: attachment.gameStatus,
    isAway: attachment.isAway,
  };
}

function send(ws, payload) {
  try { ws.send(JSON.stringify(payload)); } catch { }
}
