import {invoke} from "./bridge.js";
import {btn, el, errorLine, fileName, heading, replaceScreen, showError, speak} from "./dom.js";
import {mountLibrary} from "./library.js";
import {defaultPattern, renderPattern} from "./pattern.js";

const navigation = ["home", "profiles-nav", "settings-nav"].map(id => document.getElementById(id));
let activeLibrary = null;
let profile = null;
let generation = 0;
let libraryClosed = Promise.resolve();
let profileOpening = Promise.resolve();
let sortJob = null;
let keyOperation = Promise.resolve();
let sortView = null;
let routeCleanup = null;
const jobEvents = new EventTarget();
const jobButton = btn("Sorting progress", () => { if (sortJob) showSortJob(sortJob); }, {className: "plain small job-button", hidden: true});
document.querySelector(".appbar nav").prepend(jobButton);

function leave() {
  const id = ++generation;
  sortView = null;
  routeCleanup?.();
  routeCleanup = null;
  const closing = activeLibrary;
  activeLibrary = null;
  if (closing) {
    closing.close();
    libraryClosed = Promise.allSettled([libraryClosed, closing.flush()]).then(() => {});
  }
  return id;
}

function publishJob(job) {
  jobButton.hidden = false;
  jobButton.textContent = job.result || job.failed
    ? "Last sort" : job.total > 0 ? `Sorting ${job.completed}/${job.total}` : "Sorting…";
  jobButton.setAttribute("aria-label", job.result || job.failed ? "View last sort" : "View sorting progress");
  if (sortView?.job === job && sortView.id === generation)
    renderSortJob(job, sortView);
  jobEvents.dispatchEvent(new Event("change"));
}

function sortInProgress() { return Boolean(sortJob?.running); }

function loading(title, message = "Loading…") {
  const line = el("p", {className: "muted", role: "status"}, message);
  replaceScreen(el("section", {className: "screen form-screen"}, heading(title), line));
}

function problem(title, error, back) {
  const line = errorLine();
  showError(line, error, "The desktop app could not complete that operation.");
  replaceScreen(el("section", {className: "screen form-screen"}, heading(title, null, back), line));
}

async function showProfiles() {
  const id = leave();
  if (id !== generation) return;
  profile = null;
  loading("Profiles", "Loading saved profiles…");
  await libraryClosed;
  if (id !== generation) return;
  try {
    const profiles = await invoke("list_profiles");
    if (id !== generation) return;
    const screen = el("section", {className: "screen form-screen"}, heading("Profiles",
      "Choose how you want to sort a music library.",
      btn("New profile", () => showFolders(newDraft()), {className: "primary"})));
    if (!profiles.length) {
      screen.append(el("p", {className: "empty"}, "No profiles yet. Choose source folders and a separate destination to get started."));
    } else {
      const list = el("ul", {className: "profile-list", "aria-label": "Saved profiles"});
      for (const item of profiles) {
        const sources = item.sources.length;
        const row = el("li", {className: "profile-row"},
          el("div", {className: "profile-meta"}, el("strong", {title: item.name}, item.name),
            el("p", {className: "small-text"}, sources === 1 ? "1 source folder" : sources + " source folders")),
          el("div", {className: "actions"},
            btn("Edit", () => showFolders(newDraft(item)), {className: "plain", "aria-label": "Edit " + item.name}),
            btn("Open", () => openLibrary(item), {"aria-label": "Open " + item.name})));
        list.append(row);
      }
      screen.append(list);
    }
    replaceScreen(screen);
  } catch (error) {
    if (id === generation) problem("Profiles", error, btn("Retry", showProfiles));
  }
}

function newDraft(item) {
  return item ? {...item, sources: item.sources.slice()} : {name: "", sources: [], destination: "", pattern: defaultPattern, lookupEnabled: false};
}

async function editCurrent() {
  if (!profile) return;
  const id = leave();
  loading("Edit profile", "Loading the latest settings…");
  await libraryClosed;
  if (id === generation && profile) showFolders(newDraft(profile), true);
}

async function showFolders(draft, alreadyLeft = false) {
  if (!alreadyLeft) {
    const id = leave();
    if (id !== generation) return;
  }
  const id = generation;
  const screen = el("section", {className: "screen form-screen"});
  screen.append(heading("Choose folders", "Pick the music to read and where to put sorted copies.",
    el("p", {className: "step"}, "Step 1 of 2")));
  const error = errorLine();
  const paths = el("ul", {className: "paths", "aria-label": "Source folders"});
  const output = el("input", {type: "text", readonly: "", placeholder: "Choose a destination folder", id: "destination"});
  const continueButton = btn("Continue", () => showPattern(draft), {className: "primary"});
  const cancel = btn("Cancel", showProfiles, {className: "plain"});
  let choosing = false;

  function refresh() {
    paths.replaceChildren();
    if (!draft.sources.length) paths.append(el("li", {className: "empty"}, "No source folders selected."));
    for (const [i, path] of draft.sources.entries()) {
      paths.append(el("li", {className: "path-row"},
        el("span", {className: "path-text", title: path}, path),
        btn("Remove", () => { draft.sources.splice(i, 1); refresh(); },
          {className: "plain small", disabled: choosing, "aria-label": "Remove source folder " + path})));
    }
    output.value = draft.destination;
    continueButton.disabled = choosing || draft.sources.length === 0 || !draft.destination;
    add.disabled = browse.disabled = choosing;
  }

  async function pick(multiple) {
    if (choosing) return;
    choosing = true;
    error.hidden = true;
    refresh();
    try {
      const result = await invoke("pick_folders", {multiple});
      if (id !== generation) return;
      if (multiple) draft.sources = [...new Set([...draft.sources, ...result])];
      else if (result.length) draft.destination = result[0];
    } catch (failure) {
      if (id === generation) showError(error, failure, "Could not open the folder chooser.");
    } finally {
      choosing = false;
      if (id === generation) refresh();
    }
  }

  const add = btn("Add source folders…", () => pick(true), {className: "small"});
  const browse = btn("Choose destination…", () => pick(false));
  refresh();
  screen.append(el("div", {className: "fields"},
    el("section", {className: "field", "aria-labelledby": "sources-label"},
      el("div", {className: "label-row"}, el("h2", {id: "sources-label"}, "Source folders"), add), paths),
    el("section", {className: "field"}, el("label", {for: "destination"}, "Destination folder"),
      el("div", {className: "destination-row"}, output, browse),
      el("p", {className: "field-note"}, "The app makes sorted copies. Originals stay in their source folders.")),
    error),
    el("footer", {className: "footer"}, cancel, continueButton));
  replaceScreen(screen);
}

function showPattern(draft) {
  const id = ++generation;
  const name = el("input", {type: "text", maxlength: 80, id: "profile-name", value: draft.name});
  const pattern = renderPattern(draft.pattern, value => { draft.pattern = value; });
  const error = errorLine();
  const back = btn("Back", () => showFolders(draft, true), {className: "plain"});
  const save = btn("Save and open", async () => {
    if (save.disabled || id !== generation) return;
    error.hidden = true;
    draft.name = name.value.trim();
    draft.pattern = draft.pattern.trim();
    if (!draft.name || !draft.pattern) {
      showError(error, "Enter a profile name and a folder pattern.");
      (!draft.name ? name : pattern.editor.querySelector("input")).focus();
      return;
    }
    back.disabled = save.disabled = true;
    let justSaved = null;
    try { justSaved = await invoke("save_profile", {profile: draft}); }
    catch (failure) { if (id === generation) showError(error, failure, "Could not save the profile."); }
    finally { back.disabled = save.disabled = false; }
    if (justSaved && id === generation) await openLibrary(justSaved);
  }, {className: "primary"});
  name.addEventListener("input", () => { draft.name = name.value; });
  const lookup = el("input", {id: "lookup-pref", type: "checkbox", checked: draft.lookupEnabled,
    "aria-describedby": "lookup-help"});
  lookup.addEventListener("change", () => { draft.lookupEnabled = lookup.checked; });
  const online = el("section", {className: "online-choice"},
    el("label", {for: "lookup-pref"}, lookup, "Look up missing artist and title with AcoustID"),
    el("p", {className: "field-note", id: "lookup-help"},
      "For this profile only. With a saved key, the app sends an audio fingerprint and duration for tracks missing an artist or title. No audio or file paths are sent. Only strong, unambiguous matches are used."));
  replaceScreen(el("section", {className: "screen form-screen"}, heading("Name your profile",
    "Give this sorting setup a name and choose how files are organized.", el("p", {className: "step"}, "Step 2 of 2")),
    el("div", {className: "setup-grid"}, el("div", {className: "fields"},
      el("div", {className: "field"}, el("label", {for: "profile-name"}, "Profile name"), name),
      el("div", {className: "field"}, el("span", {className: "field-label"}, "Folder pattern"),
        pattern.editor, pattern.palette, pattern.below, el("div", {}, pattern.presetButton)),
      error),
    pattern.example),
    online,
    el("footer", {className: "footer"}, back, save)));
  name.focus({preventScroll: true});
}

async function openLibrary(item) {
  const id = leave();
  if (id !== generation) return;
  profile = item;
  loading(profile.name, "Opening local index…");
  let initial;
  const opening = profileOpening.then(async () => {
    await libraryClosed;
    if (id === generation) return invoke("open_profile", {profileId: item.id});
  });
  profileOpening = opening.catch(() => {});
  try { initial = await opening; }
  catch (failure) {
    if (id === generation) problem(item.name, failure, btn("Back to profiles", showProfiles));
    return;
  }
  if (id !== generation) return;
  const controller = mountLibrary(item, {
    edit: editCurrent, review: showReview, sort: startSorting,
    sortBusy: sortInProgress, jobEvents,
    saveColumns: async columns => {
      const saved = await invoke("save_profile", {profile: {...item, columns}});
      if (profile?.id === item.id) profile = saved;
    }
  });
  activeLibrary = controller;
  controller.apply(initial);
  async function poll() {
    if (activeLibrary !== controller || id !== generation) return;
    try {
      const snapshot = await invoke("index_snapshot", {profileId: item.id, revision: controller.revision()});
      if (activeLibrary === controller) controller.apply(snapshot);
    } catch {
      if (activeLibrary === controller) controller.failed();
    }
    if (activeLibrary === controller) setTimeout(poll, 1050);
  }
  setTimeout(poll, 750);
}

function table(headers, items, rowFor, chunk = 100) {
  const tbody = el("tbody");
  const component = el("div", {className: "result-list"});
  const rendered = el("table", {className: "review-table"},
    el("thead", {}, el("tr", {}, headers.map(name => el("th", {scope: "col"}, name)))), tbody);
  let position = 0;
  const more = btn("Show more rows", () => append());
  function append() {
    const end = Math.min(items.length, position + chunk);
    const fragment = document.createDocumentFragment();
    for (let i = position; i < end; i++) fragment.append(rowFor(items[i]));
    tbody.append(fragment);
    position = end;
    more.hidden = end >= items.length;
  }
  component.append(rendered, more);
  append();
  return component;
}

function rowLabel(row) {
  return row.track?.title || fileName(row.track?.source || row.source) || "–";
}
function outcomeLabel(outcome) {
  const original = String(outcome || "");
  return ({
    ready: "Ready",
    variant: "Keep both versions",
    duplicate: "Skip exact duplicate",
    skippedduplicate: "Skipped exact duplicate",
    alreadypresent: "Already present",
    copied: "Copied",
    conflict: "Conflict",
    failed: "Failed"
  })[original.toLowerCase().replace(/[_ -]/g, "")] || original || "–";
}

async function showReview() {
  if (!profile) return;
  const id = leave();
  if (id !== generation) return;
  const item = profile;
  loading("Review", "Preparing the copy plan…");
  const back = btn("Back to library", () => openLibrary(item), {className: "plain"});
  try {
    const result = await invoke("review_profile", {profileId: item.id});
    if (id !== generation) return;
    const rows = result.rows;
    const attention = [];
    const other = [];
    for (const row of rows) {
      (/variant|duplicate|conflict/i.test(row.status || "") ? attention : other).push(row);
    }
    const start = btn("Start sorting", startSorting, {className: "primary", disabled: rows.length === 0 || sortInProgress()});
    const syncSortButton = () => {
      if (id !== generation) jobEvents.removeEventListener("change", syncSortButton);
      else start.disabled = rows.length === 0 || sortInProgress();
    };
    jobEvents.addEventListener("change", syncSortButton);
    routeCleanup = () => jobEvents.removeEventListener("change", syncSortButton);
    const screen = el("section", {className: "screen"}, heading("Review",
      "Exact duplicates are skipped. Possible variants are kept as separate copies.",
      el("div", {className: "actions"}, back, start)));
    screen.append(attention.length
      ? el("p", {className: "lede review-summary"}, attention.length.toLocaleString() + (attention.length === 1 ? " row to review" : " rows to review"))
      : el("p", {className: "lede review-summary"}, "No possible variants or exact duplicates in this plan."));
    screen.append(table(["Track", "Destination", "Plan"], [...attention, ...other], row =>
      el("tr", {}, el("td", {}, rowLabel(row)), el("td", {}, row.destination ?? "–"),
        el("td", {}, outcomeLabel(row.status), row.message ? el("p", {className: "small-text"}, row.message) : null))));
    replaceScreen(screen);
  } catch (failure) { if (id === generation) problem("Review", failure, back); }
}

function showSortJob(job) {
  const id = leave();
  sortView = {id, job, mounted: null};
  renderSortJob(job, sortView);
}

function renderSortJob(job, state) {
  if (job.running) {
    if (!state.mounted) {
      const line = el("p", {className: "muted", role: "status"});
      const progress = el("progress", {"aria-label": "Sorting progress"});
      const back = btn("Back to library", () => openLibrary(job.profile));
      replaceScreen(el("section", {className: "screen form-screen"}, heading("Copying tracks",
        "Sorted copies are being made. You can keep using Music Sorter.", back),
      el("div", {className: "sort-progress"}, progress, line)));
      state.mounted = {line, progress};
    }
    const {line, progress} = state.mounted;
    if (job.total > 0) {
      progress.max = job.total;
      progress.value = job.completed;
      line.textContent = `${job.completed.toLocaleString()} of ${job.total.toLocaleString()} checked`;
    } else {
      progress.removeAttribute("value");
      line.textContent = "Preparing sorted copies…";
    }
    return;
  }
  if (state.finished) return;
  state.finished = true;
  if (job.failed) {
    problem("Sorting stopped", job.error, btn("Back to library", () => openLibrary(job.profile)));
    return;
  }
  const rows = job.result.rows;
  const copied = rows.filter(row => String(row.outcome).toLowerCase() === "copied").length;
  const issues = rows.filter(row => /conflict|failed/i.test(row.outcome)).length;
  const detail = copied + (copied === 1 ? " copy made." : " copies made.") +
    (issues ? " " + issues + " need attention." : "") + " Originals were not changed.";
  const screen = el("section", {className: "screen"}, heading("Sorting results", detail,
    btn("Back to library", () => openLibrary(job.profile), {className: "primary"})));
  screen.append(table(["Track", "Destination", "Outcome"], rows, row =>
    el("tr", {}, el("td", {}, rowLabel(row)), el("td", {}, row.destination ?? "–"),
      el("td", {}, outcomeLabel(row.outcome), row.message ? el("p", {className: "small-text"}, row.message) : null))));
  replaceScreen(screen);
}

async function startSorting() {
  if (!profile || sortInProgress()) return;
  const job = {profile, running: true, completed: 0, total: 0, result: null, failed: false};
  sortJob = job; // Guard duplicate starts synchronously, before any IPC.
  showSortJob(job);
  publishJob(job);
  async function poll() {
    if (sortJob !== job || !job.running) return;
    try {
      const update = await invoke("export_progress", {profileId: job.profile.id});
      if (sortJob !== job || !job.running) return;
      if (update?.running && Number.isFinite(update.total) && update.total > 0) {
        const total = Math.max(0, Math.floor(update.total));
        const done = Math.max(0, Math.min(total, Math.floor(update.completed) || 0));
        if (total !== job.total || done !== job.completed) {
          job.total = total;
          job.completed = done;
          publishJob(job);
        }
      }
    } catch {
      // Progress is supplementary. A polling failure doesn't imply that the export failed.
    }
    if (sortJob === job && job.running) job.timer = setTimeout(poll, 500);
  }
  await libraryClosed;
  try {
    const resultPending = invoke("export_profile", {profileId: job.profile.id});
    job.timer = setTimeout(poll, 220);
    job.result = await resultPending;
  } catch (error) {
    job.failed = true;
    job.error = error;
  } finally {
    job.running = false;
    clearTimeout(job.timer);
    publishJob(job);
    speak(job.failed ? "Sorting stopped. See the last sort for details." : "Sorting finished. Results are available.");
  }
}

async function showSettings() {
  const id = leave();
  if (id !== generation) return;
  const error = errorLine();
  const state = el("p", {role: "status", className: "muted"}, "Checking whether a key is saved…");
  const feedback = el("p", {role: "status", className: "small-text"});
  const input = el("input", {id: "key", type: "password", autocomplete: "new-password", maxlength: 128,
    spellcheck: "false", placeholder: "Enter an AcoustID application key", disabled: true});
  let source = null;
  const readStatus = status => {
    source = status.source ?? (status.configured ? "keychain" : null);
    known = true;
  };
  let known = false;
  let busy = true;

  function refresh() {
    input.disabled = busy;
    save.disabled = busy || !input.value.trim();
    test.disabled = busy || !source;
    remove.disabled = busy || source !== "keychain";
    state.textContent = !known ? (busy ? "Checking key configuration…" : "Key configuration could not be checked.")
      : source === "keychain" ? "A key is saved in the system credential store."
      : source === "environment" ? "Using the key from ACOUSTID_API_KEY."
      : "No AcoustID key is configured.";
  }

  async function editKey(mode) {
    if (busy || id !== generation) return;
    busy = true;
    error.hidden = true;
    feedback.textContent = "";
    const key = mode === "save" ? input.value.trim() : null;
    if (mode === "save") input.value = "";
    refresh();
    const pending = keyOperation.then(() => mode === "save" ? invoke("acoustid_save", {key})
      : mode === "test" ? invoke("acoustid_test") : invoke("acoustid_delete"));
    keyOperation = pending.catch(() => {});
    try {
      const result = await pending;
      if (mode === "save") {
        readStatus(result);
        feedback.textContent = "Key saved.";
      } else if (mode === "test") {
        feedback.textContent = result.ok ? "The configured key passed the check." : "The configured key could not be verified.";
      } else {
        readStatus(result);
        feedback.textContent = source === "environment" ? "Saved key removed. The environment key is still available." : "Saved key removed.";
      }
    } catch {
      // Never render errors returned by credential commands; they might contain credential material.
      showError(error, mode === "test" ? "The configured key could not be verified." : "The saved key could not be changed.");
    } finally {
      busy = false;
      if (id === generation) refresh();
    }
  }

  const save = btn("Save key", () => editKey("save"), {className: "primary", disabled: true});
  const test = btn("Test configured key", () => editKey("test"), {disabled: true, title: "Send a validation request to AcoustID."});
  const remove = btn("Remove saved key", () => editKey("delete"), {className: "destructive plain", disabled: true});
  input.addEventListener("input", refresh);
  input.addEventListener("keydown", event => {
    if (event.key === "Enter" && !save.disabled) { event.preventDefault(); editKey("save"); }
  });

  replaceScreen(el("section", {className: "screen form-screen"}, heading("Settings"),
    el("section", {className: "settings-form", "aria-labelledby": "acoustid-heading"},
      el("div", {}, el("h2", {id: "acoustid-heading"}, "AcoustID"),
        el("p", {className: "lede"}, "The app uses a key saved in the system credential store first, then ACOUSTID_API_KEY. Neither is displayed. Testing sends a built-in fingerprint and duration to AcoustID, not anything from your library. Local indexing and sorting do not require a key.")),
      state,
      el("div", {className: "field"}, el("label", {for: "key"}, "AcoustID application key"), input),
      el("div", {className: "actions"}, save, test, remove), feedback, error)));
  try {
    await keyOperation;
    if (id !== generation) return;
    readStatus(await invoke("acoustid_status"));
  }
  catch { showError(error, "Could not read the AcoustID key configuration."); }
  finally { busy = false; if (id === generation) refresh(); }
}

navigation[0].addEventListener("click", showProfiles);
navigation[1].addEventListener("click", showProfiles);
navigation[2].addEventListener("click", showSettings);
showProfiles();
