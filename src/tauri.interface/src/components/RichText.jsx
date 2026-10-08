/**
 * Renders an answer's light formatting: paragraphs, "•" / "-" bullets,
 * "◦" sub-bullets nested under the bullet above them, and *italic* /
 * **bold**. Built from React elements only, never injected HTML, so text
 * that came from the screen can never become markup.
 */

function inline(text, keyPrefix) {
  const parts = [];
  const pattern = /(\*\*[^*]+\*\*|\*[^*\s][^*]*\*)/g;
  let last = 0;
  let match;
  let index = 0;
  while ((match = pattern.exec(text)) !== null) {
    if (match.index > last) parts.push(text.slice(last, match.index));
    const token = match[0];
    parts.push(
      token.startsWith("**") ? (
        <strong key={`${keyPrefix}-${index++}`}>{token.slice(2, -2)}</strong>
      ) : (
        <em key={`${keyPrefix}-${index++}`}>{token.slice(1, -1)}</em>
      ),
    );
    last = match.index + token.length;
  }
  if (last < text.length) parts.push(text.slice(last));
  return parts;
}

const BULLET = /^\s*(?:[•\-*]|\d+[.)])\s+/;
const SUB_BULLET = /^\s*◦\s+|^\s{2,}(?:[•\-*])\s+/;

function parse(text) {
  const blocks = [];
  for (const paragraph of String(text ?? "").split(/\n\s*\n/)) {
    const lines = paragraph.split("\n").filter((line) => line.trim().length > 0);
    let list = null;
    let prose = [];
    const flushProse = () => {
      if (prose.length > 0) blocks.push({ type: "p", text: prose.join(" ") });
      prose = [];
    };
    for (const line of lines) {
      if (SUB_BULLET.test(line) && list && list.items.length > 0) {
        list.items[list.items.length - 1].children.push(line.replace(SUB_BULLET, ""));
      } else if (BULLET.test(line)) {
        flushProse();
        if (!list) {
          list = { type: "ul", items: [] };
          blocks.push(list);
        }
        list.items.push({ text: line.replace(BULLET, ""), children: [] });
      } else {
        list = null;
        prose.push(line.trim());
      }
    }
    flushProse();
  }
  return blocks;
}

function RichText({ text }) {
  return (
    <div className="rich-text">
      {parse(text).map((block, b) =>
        block.type === "p" ? (
          <p key={b}>{inline(block.text, `p${b}`)}</p>
        ) : (
          <ul key={b}>
            {block.items.map((item, i) => (
              <li key={i}>
                {inline(item.text, `l${b}-${i}`)}
                {item.children.length > 0 && (
                  <ul>
                    {item.children.map((child, c) => (
                      <li key={c}>{inline(child, `c${b}-${i}-${c}`)}</li>
                    ))}
                  </ul>
                )}
              </li>
            ))}
          </ul>
        ),
      )}
    </div>
  );
}

export default RichText;
