import {btn, dirName, el, fileName, glyph, heading, raw, replaceScreen, speak} from "./dom.js";
import {t} from "./i18n.js";

const columns = [
  ["title", "Title", 220, track => track.title || fileName(track.source)],
  ["artist", "Artist", 170, track => track.artist],
  ["genre", "Genre", 128, track => track.genre],
  ["year", "Year", 70, track => track.year || null],
  ["filename", "Filename", 225, track => fileName(track.source)],
  ["format", "Format", 94, track => String(track.format || fileName(track.source).split(".").at(-1) || "").toUpperCase()],
  ["source", "Source folder", 290, track => dirName(track.source)],
  ["sha256", "SHA-256", 320, track => track.sha256]
];
const minWidth = 56;
const maxWidth = 840;
const defaultIds = ["title", "artist", "genre", "year"];
const rowHeight = 40; // Matches the CSS --row invariant.
const buffer = 8;
const clampWidth = value => Math.min(maxWidth, Math.max(minWidth, Math.round(value)));

function readChoices(source) {
  const choices = new Map();
  for (const item of Array.isArray(source) ? source : []) {
    const def = columns.find(spec => spec[0] === (typeof item === "string" ? item : item?.id));
    if (!def || choices.has(def[0])) continue;
    choices.set(def[0], Number.isFinite(item?.width) ? clampWidth(item.width) : def[2]);
  }
  return choices.size ? choices : new Map(columns.filter(c => defaultIds.includes(c[0])).map(c => [c[0], c[2]]));
}

export function mountLibrary(profile, handlers) {
  let tracks = [];
  let selectedSource = null;
  let trackPositions = new Map();
  let responsive = !Array.isArray(profile.columns) || !profile.columns.some(column => Number.isFinite(column?.width));
  let visible = readChoices(profile.columns);
  const knownWidths = new Map(visible);
  let committed = new Map(visible);
  let prefsVersion = 0;
  let savedPrefs = Promise.resolve();
  let closed = false;
  let frame = 0;
  let renderedRange = "";
  let rev = undefined;
  let indexing = true;

  const screen = el("section", {className: "screen library"});
  const counts = el("p", {className: "lede"}, "Opening local index…");
  const watcherText = profile.sources.length === 1 ? profile.sources[0] : profile.sources.length + " " + t("source folders");
  const watcher = el("p", {className: "watching"}, glyph("folder"),
    el("span", {className: "ellipsis", title: profile.sources.join("\n")}, t("Watching: ") + watcherText));
  const start = btn("Start sorting", handlers.sort, {className: "primary", disabled: true});
  const review = btn("Review", handlers.review, {disabled: true});
  function syncActions() {
    review.disabled = indexing || tracks.length === 0;
    start.disabled = review.disabled || Boolean(handlers.sortBusy?.());
    start.title = handlers.sortBusy?.() ? t("A sort is already running. Open sorting progress from the top bar.") : "";
  }
  handlers.jobEvents?.addEventListener("change", syncActions);
  screen.append(heading(raw(profile.name), counts, el("div", {className: "actions"}, review, start), watcher));
  const strip = el("section", {className: "column-strip", id: "column-strip", hidden: true, "aria-label": "Visible table columns"});
  const hint = el("p", {className: "small-text"}, "Choose columns. Drag a header divider or use its arrow keys to resize.");
  const preferenceStatus = el("p", {className: "small-text", role: "status", hidden: true, "aria-live": "polite"});
  const choices = el("div", {className: "column-choices"});
  strip.append(hint, choices, preferenceStatus);
  const columnButton = btn("Columns", () => {
    strip.hidden = !strip.hidden;
    columnButton.setAttribute("aria-expanded", String(!strip.hidden));
    scheduleRows();
  }, {className: "small", "aria-controls": "column-strip", "aria-expanded": "false"});
  screen.append(el("div", {className: "table-toolbar"}, columnButton), strip);

  const scroll = el("div", {className: "grid-scroll", role: "grid", tabindex: "0",
    "aria-label": "Indexed tracks", "aria-rowcount": "1", "aria-colcount": String(visible.size)});
  const header = el("div", {className: "grid-header", role: "row", "aria-rowindex": "1"});
  const rows = el("div", {className: "grid-body", role: "rowgroup"});
  const empty = el("p", {className: "empty"}, "Waiting for the index…");
  scroll.append(header, rows, empty);
  scroll.addEventListener("scroll", scheduleRows, {passive: true});
  scroll.addEventListener("keydown", onGridKey);
  screen.append(scroll);
  const status = el("p", {className: "muted", role: "status"}, "Opening index…");
  const edit = btn("Edit profile", handlers.edit, {className: "plain"});
  screen.append(el("footer", {className: "footer"}, status, edit));

  function shown() { return columns.filter(([id]) => visible.has(id)); }
  function applyWidths() {
    const specs = shown().map(([id, , base]) => responsive
      ? "minmax(" + visible.get(id) + "px, " + base + "fr)" : visible.get(id) + "px");
    scroll.style.setProperty("--columns", specs.join(" "));
    scroll.setAttribute("aria-colcount", String(specs.length));
  }

  function lockWidths() {
    if (!responsive) return;
    for (const cell of header.children) {
      const id = cell.dataset.column;
      const width = clampWidth(cell.getBoundingClientRect().width);
      visible.set(id, width);
      knownWidths.set(id, width);
      cell.querySelector(".resize").setAttribute("aria-valuenow", String(width));
    }
    responsive = false;
    applyWidths();
  }

  function paintHeaders() {
    header.replaceChildren(...shown().map(([id, label]) => {
      const handle = el("span", {className: "resize", role: "separator", tabindex: "0",
        "aria-orientation": "vertical", "aria-label": t("Resize {label}", {label: t(label)}),
        "aria-valuemin": minWidth, "aria-valuemax": maxWidth, "aria-valuenow": visible.get(id)});
      function move(value) {
        const newWidth = clampWidth(value);
        if (visible.get(id) === newWidth) return;
        visible.set(id, newWidth);
        knownWidths.set(id, newWidth);
        handle.setAttribute("aria-valuenow", String(newWidth));
        applyWidths();
      }
      handle.addEventListener("pointerdown", event => {
        if (event.button !== 0) return;
        event.preventDefault();
        handle.focus({preventScroll: true});
        handle.setPointerCapture(event.pointerId);
        lockWidths();
        const startX = event.clientX;
        const original = visible.get(id);
        const update = event => move(original + event.clientX - startX);
        const finish = event => {
          handle.removeEventListener("pointermove", update);
          handle.removeEventListener("lostpointercapture", finish);
          if (event.pointerId != null && handle.hasPointerCapture(event.pointerId))
            handle.releasePointerCapture(event.pointerId);
          if (visible.get(id) !== original) persist();
        };
        handle.addEventListener("pointermove", update);
        handle.addEventListener("lostpointercapture", finish);
      });
      handle.addEventListener("keydown", event => {
        const left = event.key === "ArrowLeft";
        if (!left && event.key !== "ArrowRight") return;
        event.stopPropagation();
        event.preventDefault();
        lockWidths();
        move(visible.get(id) + (left ? -1 : 1) * (event.shiftKey ? 25 : 8));
        persist();
      });
      return el("div", {className: "grid-heading", role: "columnheader", "data-column": id},
        el("span", {title: label}, label), handle);
    }));
  }

  function paintChoices() {
    choices.replaceChildren(...columns.map(([id, label, width]) => {
      const check = el("input", {type: "checkbox", checked: visible.has(id),
        disabled: visible.has(id) && visible.size === 1, "aria-label": t("Show {label} column", {label: t(label)})});
      check.addEventListener("change", () => {
        lockWidths();
        if (check.checked) visible.set(id, knownWidths.get(id) ?? width);
        else if (visible.size > 1) visible.delete(id);
        else { check.checked = true; return; }
        paintColumns(id);
        persist();
      });
      check.dataset.choice = id;
      return el("label", {}, check, label);
    }));
  }

  function preferenceMessage(message, error = false) {
    preferenceStatus.hidden = !message;
    preferenceStatus.textContent = message ? t(message) : "";
    preferenceStatus.classList.toggle("error", error);
  }

  function persist() {
    const version = ++prefsVersion;
    const snapshot = new Map(visible);
    preferenceMessage("Saving columns…");
    savedPrefs = savedPrefs.then(async () => {
      if (version !== prefsVersion) return;
      try {
        await handlers.saveColumns(shownPayload(snapshot));
        committed = snapshot;
        if (!closed && version === prefsVersion) preferenceMessage("");
      } catch {
        if (!closed && version === prefsVersion) {
          visible = new Map(committed);
          for (const [id, width] of committed) knownWidths.set(id, width);
          paintColumns();
          preferenceMessage("Could not save the columns. Your last saved layout is restored.", true);
        }
      }
    });
  }

  function shownPayload(choices) {
    return columns.filter(([id]) => choices.has(id)).map(([id]) => ({id, width: choices.get(id)}));
  }

  function paintColumns(focusId) {
    applyWidths();
    paintHeaders();
    paintChoices();
    if (focusId) {
      const target = choices.querySelector('[data-choice="' + focusId + '"]');
      (target && !target.disabled ? target : columnButton).focus({preventScroll: true});
    }
    paintRows(true);
  }

  function scheduleRows() {
    if (closed || frame) return;
    frame = requestAnimationFrame(() => { frame = 0; paintRows(); });
  }

  function paintRows(force = false) {
    const start = Math.max(0, Math.floor(scroll.scrollTop / rowHeight) - buffer);
    const end = Math.min(tracks.length, start + Math.ceil((scroll.clientHeight || 400) / rowHeight) + 2 * buffer);
    const range = start + ":" + end;
    if (!force && renderedRange === range) return;
    renderedRange = range;
    const schema = shown();
    const fragment = document.createDocumentFragment();
    for (let i = start; i < end; i++) {
      const track = tracks[i];
      const row = el("div", {className: "track-row", id: "indexed-track-" + i, role: "row",
        "aria-rowindex": String(i + 2), "aria-selected": String(track.source === selectedSource)});
      row.style.top = i * rowHeight + "px";
      row.addEventListener("click", () => { scroll.focus({preventScroll: true}); select(i); });
      for (const [id, , , read] of schema) {
        const value = String(read(track) ?? "") || "–";
        row.append(el("div", {className: "grid-cell", role: "gridcell", "data-column": id},
          el("span", {title: value === "–" ? "" : value}, raw(value))));
      }
      fragment.append(row);
    }
    rows.replaceChildren(fragment);
    const selected = trackPositions.get(selectedSource) ?? -1;
    if (selected >= start && selected < end) scroll.setAttribute("aria-activedescendant", "indexed-track-" + selected);
    else scroll.removeAttribute("aria-activedescendant");
  }

  function select(index, intoView = false) {
    if (!tracks[index]) return;
    selectedSource = tracks[index].source;
    if (intoView) {
      const top = index * rowHeight;
      if (top < scroll.scrollTop) scroll.scrollTop = top;
      else if (top + rowHeight + header.offsetHeight > scroll.scrollTop + scroll.clientHeight)
        scroll.scrollTop = top + rowHeight + header.offsetHeight - scroll.clientHeight;
    }
    paintRows(true);
  }

  function onGridKey(event) {
    if (event.target !== scroll) return;
    let index = trackPositions.get(selectedSource) ?? -1;
    if (event.key === "ArrowDown") index++;
    else if (event.key === "ArrowUp") index = Math.max(0, index - 1);
    else if (event.key === "Home") index = 0;
    else if (event.key === "End") index = tracks.length - 1;
    else if (event.key === "PageDown") index += Math.max(1, Math.floor(scroll.clientHeight / rowHeight) - 1);
    else if (event.key === "PageUp") index -= Math.max(1, Math.floor(scroll.clientHeight / rowHeight) - 1);
    else return;
    event.preventDefault();
    select(Math.min(tracks.length - 1, Math.max(0, index)), true);
  }

  const resize = new ResizeObserver(scheduleRows);
  replaceScreen(screen);
  resize.observe(scroll);
  paintColumns();

  return {
    apply(snapshot) {
      if (closed || !snapshot) return;
      const trackChange = Array.isArray(snapshot.tracks)
        && (snapshot.revision == null || snapshot.revision !== rev || !tracks.length);
      if (snapshot.revision != null) rev = snapshot.revision;
      if (trackChange) {
        tracks = snapshot.tracks;
        trackPositions = new Map(tracks.map((track, index) => [track.source, index]));
        scroll.setAttribute("aria-rowcount", String(tracks.length + 1));
        rows.style.height = tracks.length * rowHeight + "px";
        paintRows(true);
      }
      counts.textContent = tracks.length.toLocaleString() + (tracks.length === 1 ? " " + t("track indexed") : " " + t("tracks indexed"));
      empty.hidden = tracks.length > 0;
      empty.textContent = t(snapshot.indexing ? "Waiting for the index…" : "No tracks found in these folders.");
      const showProgress = snapshot.indexing && snapshot.total > 0 && Number.isInteger(snapshot.completed);
      status.textContent = snapshot.message ? t(snapshot.message) : (showProgress
        ? t("Indexing ") + snapshot.completed.toLocaleString() + t(" of ") + snapshot.total.toLocaleString() + t(" files…")
        : snapshot.indexing ? t("Updating index…") : t("Index up to date"));
      indexing = Boolean(snapshot.indexing);
      syncActions();
    },
    revision() { return rev; },
    failed() {
      if (closed) return;
      status.textContent = t("The index could not be refreshed. Checking again…");
      speak(status.textContent);
    },
    flush() { return savedPrefs; },
    close() {
      closed = true;
      resize.disconnect();
      handlers.jobEvents?.removeEventListener("change", syncActions);
      if (frame) cancelAnimationFrame(frame);
    }
  };
}
