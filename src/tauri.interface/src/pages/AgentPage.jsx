import { useState } from "react";
import { formatTimestamp, glintAsk } from "../glint";

function todayKey() {
  const d = new Date();
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
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
 */
function AgentPage() {
  const [messages, setMessages] = useState([]);
  const [input, setInput] = useState("");
  const [scope, setScope] = useState("all");
  const [day, setDay] = useState(todayKey());
  const [busy, setBusy] = useState(false);

  async function ask(question) {
    const text = question.trim();
    if (!text || busy) return;
    setBusy(true);
    setMessages((prev) => [...prev, { role: "user", text }]);
    try {
      const response = await glintAsk(
        text,
        scope,
        scope === "day" ? day : null,
      );
      setMessages((prev) => [
        ...prev,
        {
          role: "agent",
          text: response.answer,
          citations: response.citations ?? [],
        },
      ]);
    } catch (error) {
      setMessages((prev) => [
        ...prev,
        { role: "error", text: String(error?.message ?? error) },
      ]);
    } finally {
      setBusy(false);
    }
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

  return (
    <div className="glint-page">
      <div className="glint-page-inner narrow chat-layout">
        {messages.length === 0 ? (
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
                <path d="M5.5 8h5M8 5.5v5" strokeLinecap="round" />
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
              <button
                className="glint-btn"
                onClick={() => {
                  setMessages([]);
                }}
              >
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
  );
}

export default AgentPage;
