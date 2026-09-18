import { useCallback, useEffect, useRef, useState } from "react";
import {
  formatClock,
  formatTimestamp,
  glintAsk,
  glintChatCreate,
  glintChatDelete,
  glintChatRename,
  glintChatThread,
  glintChatThreads,
} from "../glint";

function todayKey() {
  const d = new Date();
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

function dayKey(ms) {
  const d = new Date(Number(ms));
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** Claude-style buckets: Today / Yesterday / Previous 7 days / Older. */
function threadGroups(threads) {
  const today = todayKey();
  const yesterday = new Date();
  yesterday.setDate(yesterday.getDate() - 1);
  const pad = (n) => String(n).padStart(2, "0");
  const yesterdayKey = `${yesterday.getFullYear()}-${pad(yesterday.getMonth() + 1)}-${pad(yesterday.getDate())}`;
  const weekAgo = Date.now() - 7 * 86400000;
  const groups = { Today: [], Yesterday: [], "Previous 7 days": [], Older: [] };
  for (const thread of threads ?? []) {
    const day = dayKey(thread.updatedAtMilliseconds);
    const at = Number(thread.updatedAtMilliseconds);
    if (day === today) groups["Today"].push(thread);
    else if (day === yesterdayKey) groups["Yesterday"].push(thread);
    else if (at >= weekAgo) groups["Previous 7 days"].push(thread);
    else groups["Older"].push(thread);
  }
  return Object.entries(groups).filter(([, items]) => items.length > 0);
}

function parseCitations(json) {
  if (!json) return [];
  try {
    const parsed = JSON.parse(json);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function toBubble(message) {
  return {
    role: message.role,
    text: message.text ?? "",
    citations:
      message.citations ?? parseCitations(message.citationsJson),
    scopedCount: message.scopedCount,
    at: message.at ?? message.createdAtMilliseconds ?? Date.now(),
  };
}

const SUGGESTIONS = [
  {
    text: "What did I decide recently?",
    icon: (
      <svg
        viewBox="0 0 16 16"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.5"
        aria-hidden="true"
      >
        <circle cx="8" cy="8" r="6.2" />
        <path
          d="M5.5 8.2l1.8 1.8 3.2-3.8"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
      </svg>
    ),
  },
  {
    text: "What remains unfinished?",
    icon: (
      <svg
        viewBox="0 0 16 16"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.5"
        aria-hidden="true"
      >
        <path
          d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9M13.5 1.8v2.6h-2.6"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
      </svg>
    ),
  },
  {
    text: "Summarize my work on this day.",
    icon: (
      <svg
        viewBox="0 0 16 16"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.5"
        aria-hidden="true"
      >
        <rect x="2" y="2.5" width="12" height="4" rx="1.5" />
        <rect x="2" y="9.5" width="12" height="4" rx="1.5" />
      </svg>
    ),
  },
];

/**
 * Agent tab: chat over captured history. Questions run against the local
 * Gemma worker via glint_ask; citations are the scans actually placed in
 * context — never model-invented references.
 *
 * Conversations persist as threads in the encrypted store: the left panel
 * lists them by recency (timed, grouped by day bucket), clicking one loads
 * it, and follow-ups travel with the thread so corrections land on the same
 * evidence instead of re-asking from scratch.
 */
function AgentPage() {
  const [threads, setThreads] = useState([]);
  const [activeThreadId, setActiveThreadId] = useState(null);
  const [messages, setMessages] = useState([]);
  const [input, setInput] = useState("");
  const [scope, setScope] = useState("all");
  const [day, setDay] = useState(todayKey());
  const [busy, setBusy] = useState(false);
  const scrollRef = useRef(null);
  const stickToBottom = useRef(true);

  function onScroll() {
    const el = scrollRef.current;
    if (!el) return;
    // Only yank the viewport when the user is already near the bottom;
    // reading history above must never jump.
    stickToBottom.current = el.scrollHeight - el.scrollTop - el.clientHeight < 150;
  }

  // Follow the conversation: new turns (and newly opened threads) land at
  // the bottom unless the user deliberately scrolled up to read.
  useEffect(() => {
    if (!stickToBottom.current) return;
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages, busy]);

  const refreshThreads = useCallback(() => {
    glintChatThreads(50)
      .then((response) => setThreads(response.threads ?? []))
      .catch(() => {});
  }, []);

  useEffect(() => {
    refreshThreads();
  }, [refreshThreads]);

  function openThread(id) {
    if (busy) return;
    stickToBottom.current = true;
    setActiveThreadId(id);
    glintChatThread(id, 200)
      .then((response) => {
        setMessages((response.messages ?? []).map(toBubble));
      })
      .catch(() => {
        setMessages([]);
      });
  }

  function newChat() {
    if (busy) return;
    setActiveThreadId(null);
    setMessages([]);
  }

  async function ask(question) {
    const text = question.trim();
    if (!text || busy) return;
    stickToBottom.current = true;
    setBusy(true);
    try {
      // No active thread: title from the first question (truncated,
      // deterministic) and let the ask persist its turns.
      let threadId = activeThreadId;
      if (!threadId) {
        const title =
          text.length > 42 ? `${text.slice(0, 42).trimEnd()}…` : text;
        const created = await glintChatCreate(title, scope);
        threadId = created.id;
        setActiveThreadId(threadId);
        refreshThreads();
      }
      setMessages((prev) => [
        ...prev,
        { role: "user", text, at: Date.now() },
      ]);
      const response = await glintAsk(
        text,
        scope,
        scope === "day" ? day : null,
        threadId,
      );
      setMessages((prev) => [
        ...prev,
        {
          role: "agent",
          text: response.answer,
          citations: response.citations ?? [],
          scopedCount: response.scopedCount,
          at: Date.now(),
        },
      ]);
      refreshThreads();
    } catch (error) {
      setMessages((prev) => [
        ...prev,
        { role: "error", text: String(error?.message ?? error), at: Date.now() },
      ]);
    } finally {
      setBusy(false);
    }
  }

  function removeThread(id) {
    if (busy) return;
    const thread = threads.find((t) => t.id === id);
    if (!thread) return;
    if (!window.confirm(`Delete "${thread.title}" and its messages?`)) return;
    glintChatDelete(id)
      .then(() => {
        setThreads((prev) => prev.filter((t) => t.id !== id));
        if (activeThreadId === id) {
          setActiveThreadId(null);
          setMessages([]);
        }
      })
      .catch(() => {});
  }

  function renameThread(id) {
    if (busy) return;
    const thread = threads.find((t) => t.id === id);
    if (!thread) return;
    const next = window.prompt("Rename chat", thread.title);
    if (!next || !next.trim() || next.trim() === thread.title) return;
    glintChatRename(id, next.trim())
      .then(() => {
        setThreads((prev) =>
          prev.map((t) => (t.id === id ? { ...t, title: next.trim() } : t)),
        );
      })
      .catch(() => {});
  }

  function send() {
    if (!input.trim()) return;
    setInput("");
    ask(input);
  }

  function onKeyDown(e) {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      send();
    }
  }

  const scopeLabel =
    scope === "all" ? "All time" : scope === "week" ? "Past week" : day;
  const showingChat = activeThreadId !== null || messages.length > 0;

  return (
    <div className="glint-page">
      <div className="glint-page-inner narrow chat-layout agent-full">
        <div className="chat-shell">
          <aside className="chat-threads" aria-label="Chat history">
            <button
              className="glint-btn chat-new-full"
              onClick={newChat}
              disabled={busy}
              title="Start a new chat"
            >
              + New
            </button>
            <div className="chat-threads-head">
              <span>Chats and tasks</span>
            </div>
            <div className="chat-threads-list">
              {threadGroups(threads).map(([label, items]) => (
                <div key={label}>
                  <div className="chat-group-label">{label}</div>
                  {items.map((thread) => (
                    <div
                      key={thread.id}
                      role="button"
                      tabIndex={0}
                      className={`chat-thread${thread.id === activeThreadId ? " active" : ""}`}
                      onClick={() => openThread(thread.id)}
                      onKeyDown={(e) => {
                        if (e.key === "Enter") openThread(thread.id);
                      }}
                      onDoubleClick={() => renameThread(thread.id)}
                      title={`${thread.title} — double-click to rename`}
                    >
                      <span className="chat-thread-title">{thread.title}</span>
                      <span className="chat-thread-time">
                        {formatClock(thread.updatedAtMilliseconds)}
                      </span>
                      <button
                        className="chat-thread-delete"
                        onClick={(e) => {
                          e.stopPropagation();
                          removeThread(thread.id);
                        }}
                        disabled={busy}
                        title="Delete chat"
                        aria-label={`Delete ${thread.title}`}
                      >
                        ×
                      </button>
                    </div>
                  ))}
                </div>
              ))}
              {threads.length === 0 && (
                <div className="chat-threads-empty">No chats yet.</div>
              )}
            </div>
          </aside>

          <div className="chat-main" ref={scrollRef} onScroll={onScroll}>
            <div className="chat-conversation">
            {!showingChat ? (
              <>
                <div className="ask-hero">
                  <svg
                    className="ask-spark"
                    viewBox="0 0 32 32"
                    fill="none"
                    aria-hidden="true"
                  >
                    <path
                      d="M16 2c.7 7.5 4.5 11.3 12 12-7.5.7-11.3 4.5-12 12-.7-7.5-4.5-11.3-12-12 7.5-.7 11.3-4.5 12-12z"
                      fill="#5ec8f2"
                    />
                    <circle cx="24.5" cy="8.5" r="1.6" fill="#5ec8f2" />
                  </svg>
                  <h1 className="glint-title ask-title">
                    What do you want to remember?
                  </h1>
                  <p className="glint-hint">
                    Glint answers from your local memory and shows the activity
                    behind each answer.
                  </p>
                </div>

                <div className="ask-scope-pill">
                  <svg
                    viewBox="0 0 16 16"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="1.5"
                    aria-hidden="true"
                  >
                    <circle cx="8" cy="8" r="6.2" />
                    <path d="M8 5.5v5M5.5 8h5" strokeLinecap="round" />
                  </svg>
                  <span>Searching all captured activity</span>
                  <select
                    className="ask-scope-select"
                    value={scope}
                    onChange={(e) => setScope(e.target.value)}
                    aria-label="Search scope"
                  >
                    <option value="all">All time</option>
                    <option value="week">Past week</option>
                    <option value="day">Day</option>
                  </select>
                  {scope === "day" && (
                    <input
                      type="date"
                      className="chat-date"
                      value={day}
                      max={todayKey()}
                      onChange={(e) => setDay(e.target.value || todayKey())}
                      aria-label="Scope day"
                    />
                  )}
                </div>

                <div className="ask-suggest-list">
                  {SUGGESTIONS.map((item) => (
                    <button
                      key={item.text}
                      className="ask-suggest"
                      onClick={() => ask(item.text)}
                      disabled={busy}
                    >
                      <span className="ask-suggest-ico">{item.icon}</span>
                      <span className="ask-suggest-text">{item.text}</span>
                      <svg
                        className="ask-suggest-arrow"
                        viewBox="0 0 16 16"
                        fill="none"
                        stroke="currentColor"
                        strokeWidth="1.5"
                        aria-hidden="true"
                      >
                        <path
                          d="M3 8h10M9 4.5L12.5 8 9 11.5"
                          strokeLinecap="round"
                          strokeLinejoin="round"
                        />
                      </svg>
                    </button>
                  ))}
                </div>
              </>
            ) : (
              <>
                <div className="ask-scope-pill compact">
                  <span>Searching {scopeLabel}</span>
                  <button className="glint-btn" onClick={newChat} disabled={busy}>
                    New question
                  </button>
                </div>
                <div className="chat-list">
                  {messages.map((msg, i) => (
                    <div key={i} className={`chat-msg ${msg.role}`}>
                      <div className="chat-text">{msg.text}</div>
                      {msg.citations && msg.citations.length > 0 && (
                        <details className="chat-citations" close>
                          <summary className="chat-cite-head">
                            Based on {msg.citations.length} scan
                            {msg.citations.length === 1 ? "" : "s"}
                          </summary>
                          {msg.citations.map((cite) => (
                            <div className="chat-cite" key={cite.id ?? cite.label}>
                              <strong>{cite.label}</strong>
                              <span>
                                {cite.processName} ·{" "}
                                {formatTimestamp(cite.capturedAtMilliseconds)}
                              </span>
                            </div>
                          ))}
                        </details>
                      )}
                      {msg.at != null && (
                        <div className="chat-time">{formatClock(msg.at)}</div>
                      )}
                    </div>
                  ))}
                  {busy && (
                    <div className="chat-msg agent">
                      <div className="typing">
                        <span />
                        <span />
                        <span />
                      </div>
                    </div>
                  )}
                </div>
              </>
            )}

            <div className="ask-input-bar">
              <svg
                className="ask-input-spark"
                viewBox="0 0 32 32"
                fill="none"
                aria-hidden="true"
              >
                <path
                  d="M16 2c.7 7.5 4.5 11.3 12 12-7.5.7-11.3 4.5-12 12-.7-7.5-4.5-11.3-12-12 7.5-.7 11.3-4.5 12-12z"
                  fill="#5ec8f2"
                />
              </svg>
              <input
                value={input}
                onChange={(e) => setInput(e.target.value)}
                onKeyDown={onKeyDown}
                placeholder="Ask a question about your work…"
                aria-label="Ask the agent"
                disabled={busy}
              />
              <button
                className="ask-send"
                onClick={send}
                disabled={busy || !input.trim()}
                aria-label="Send question"
                title="Send"
              >
                <svg
                  viewBox="0 0 16 16"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                  aria-hidden="true"
                >
                  <path
                    d="M8 13V3M4.5 6.5L8 3l3.5 3.5"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                  />
                </svg>
              </button>
            </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

export default AgentPage;
