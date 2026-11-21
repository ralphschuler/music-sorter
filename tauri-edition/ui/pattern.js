import {btn, el, glyph} from "./dom.js";

const fields = {
  artist: ["Artist", "Kite Array"], year: ["Year", "2024"], title: ["Title", "Glass Current"],
  genre: ["Genre", "Ambient"], artistInitial: ["Artist initial", "K"],
  fileName: ["Filename", "Kite Array - Glass Current"], extension: ["Extension", "flac"]
};
const tokenNames = Object.fromEntries(Object.keys(fields).map(key => [key.toLowerCase(), key]));
export const defaultPattern = "{artistInitial}/{artist}/{title}";
const preset = "{artist}/{year}/{title}";

const encoder = new TextEncoder();
function bounded(value, maxUnits, maxBytes) {
  let output = "", bytes = 0;
  for (const char of value) {
    bytes += encoder.encode(char).length;
    if (output.length + char.length > maxUnits || bytes > maxBytes) break;
    output += char;
  }
  return output;
}
function sanitize(value) {
  const name = bounded(value.normalize("NFKC").replace(/[<>:"/\\|?*\x00-\x1f\x7f-\x9f]/g, "_")
    .trim().replace(/[. ]+$/, ""), 200, 230).replace(/[. ]+$/, "");
  if (!name) return "Unknown";
  const stem = name.split(".")[0].trimEnd();
  return /^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9]|LPT[1-9])$/i.test(stem)
    ? "_" + bounded(name, 199, 229) : name;
}

function previewPath(pattern) {
  const split = pattern.trim().replace(/\\/g, "/").split("/");
  if (split.length > 32 || split.some(segment => /^\s*(\.|\.\.)?\s*$/.test(segment) || /[:\x00-\x1f\x7f-\x9f]/.test(segment))) return null;
  const expanded = split.map(segment => {
    const rendered = segment.replace(/\{([A-Za-z]+)\}/g, (_, key) => fields[tokenNames[key.toLowerCase()]]?.[1] ?? "{}");
    return /[{}]/.test(rendered) ? null : sanitize(rendered);
  });
  if (expanded.some(segment => segment == null)) return null;
  const stem = sanitize(expanded.pop().replace(/\.flac$/i, ""));
  expanded.push(bounded(stem, 195, 225).replace(/[. ]+$/, "") + ".flac");
  return expanded;
}

export function renderPattern(value, changed) {
  const editor = el("div", {className: "pattern-box", role: "group", "aria-label": "Folder pattern"});
  const palette = el("div", {className: "token-palette", "aria-label": "Pattern fields"});
  const example = el("aside", {className: "example", "aria-labelledby": "example-title"},
    el("h2", {id: "example-title"}, "Example output"));
  const tree = el("div", {"aria-label": "Example output path"});
  example.append(tree);

  let model = tokenize(value);
  let last = null;
  let inputs = [];
  function report() {
    const pattern = model.map(part => part.token ? (part.raw || "{" + part.token + "}") : part.text).join("");
    changed(pattern);
    const parts = previewPath(pattern);
    if (!parts) {
      tree.replaceChildren(el("p", {className: "field-note"}, "Complete the pattern to see an example."));
      return;
    }
    tree.replaceChildren(...parts.map((part, depth) => {
      const row = el("div", {className: "example-line"}, depth ? el("span", {className: "branch", "aria-hidden": "true"}, "└") : null,
        glyph(depth === parts.length - 1 ? "file" : "folder"), el("span", {className: "ellipsis", title: part}, part));
      row.style.setProperty("--depth", depth);
      return row;
    }));
  }

  function refresh(focusIndex, caret) {
    inputs = [];
    last = null;
    editor.replaceChildren(...model.map((part, i) => {
      if (part.token) {
        const name = fields[part.token][0];
        return el("span", {className: "chip"}, name,
          btn("×", () => {
            const offset = model[i - 1].text.length;
            model[i - 1].text += model[i + 1].text;
            model.splice(i, 2);
            refresh(i - 1, offset);
            report();
          }, {"aria-label": "Remove " + name}));
      }
      const input = el("input", {className: "literal", type: "text", maxlength: 240, value: part.text,
        "aria-label": "Literal text or folder separator " + (i / 2 + 1),
        placeholder: i === model.length - 1 ? "Text or /" : ""});
      function size() {
        input.style.setProperty("--edit-width", Math.min(180, Math.max(i === model.length - 1 ? 76 : 25, input.value.length * 8 + 15)) + "px");
      }
      size();
      input.addEventListener("focus", () => { last = i; });
      input.addEventListener("input", () => {
        part.text = input.value;
        size();
        report();
      });
      inputs[i] = input;
      return input;
    }));
    if (focusIndex != null && inputs[focusIndex]) {
      inputs[focusIndex].focus();
      inputs[focusIndex].setSelectionRange(caret, caret);
    }
  }

  function insert(token, literal) {
    const i = last ?? model.length - 1;
    const input = inputs[i];
    const text = model[i].text;
    const from = last == null ? text.length : input.selectionStart ?? text.length;
    const to = last == null ? text.length : input.selectionEnd ?? from;
    if (token) {
      model.splice(i, 1, {text: text.slice(0, from)}, {token}, {text: text.slice(to)});
      refresh(i + 2, 0);
    } else {
      model[i].text = text.slice(0, from) + literal + text.slice(to);
      refresh(i, from + literal.length);
    }
    report();
  }

  for (const [key, [label]] of Object.entries(fields)) {
    palette.append(btn(label, () => insert(key), {className: "small", "aria-label": "Insert " + label + " field"}));
  }
  palette.append(btn("Add /", () => insert(null, "/"), {className: "small", "aria-label": "Add folder separator (/)"}));
  const below = el("div", {className: "field-note"}, "Choose a field to insert it. Use / to separate folders.");
  const presetButton = btn("Use Artist / Year / Title", () => {
    model = tokenize(preset);
    refresh();
    report();
  }, {className: "small", title: "Replace the draft pattern. Nothing changes until you save."});
  refresh();
  report();
  return {editor, palette, below, presetButton, example};
}

function tokenize(pattern) {
  const model = [];
  let start = 0;
  for (const match of pattern.matchAll(/\{([A-Za-z]+)\}/g)) {
    const token = tokenNames[match[1].toLowerCase()];
    if (!token) continue;
    model.push({text: pattern.slice(start, match.index)}, {token, raw: match[0]});
    start = match.index + match[0].length;
  }
  model.push({text: pattern.slice(start)});
  return model;
}
