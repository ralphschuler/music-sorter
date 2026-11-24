import {t} from "./i18n.js";

export const view = document.getElementById("view");
const announcement = document.getElementById("announcement");
export const raw = value => document.createTextNode(String(value ?? ""));

export function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (value === undefined || value === null) continue;
    if (key === "className") node.className = value;
    else if (key === "checked" || key === "disabled" || key === "hidden") node[key] = Boolean(value);
    else node.setAttribute(key, value);
  }
  for (const child of children.flat()) {
    if (child != null) node.append(typeof child === "string" ? t(child) : child);
  }
  return node;
}

export function btn(label, action, attrs = {}) {
  const button = el("button", {type: "button", ...attrs}, t(label));
  button.addEventListener("click", action);
  return button;
}

export function heading(name, detail, actions, meta) {
  const left = el("div", {className: "heading-title"}, el("h1", {}, name));
  if (detail) left.append(typeof detail === "string" ? el("p", {className: "lede"}, detail) : detail);
  if (meta) left.append(meta);
  return el("div", {className: "heading"}, left, actions);
}

export function replaceScreen(screen) {
  view.replaceChildren(screen);
  view.focus({preventScroll: true});
}

export function speak(message) {
  announcement.textContent = message;
}

export function showError(node, error, fallback) {
  node.textContent = typeof error === "string" ? error : error?.message || fallback;
  node.hidden = false;
}

export function errorLine() {
  return el("p", {className: "error", role: "alert", hidden: true});
}

export function glyph(kind) {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("viewBox", "0 0 20 20");
  svg.setAttribute("aria-hidden", "true");
  const path = document.createElementNS(ns, "path");
  path.setAttribute("d", kind === "folder" ? "M2 16V5a1 1 0 0 1 1-1h5l2 2h7a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1Z" :
    "M5 2h7l4 4v12H5zM12 2v4h4");
  svg.append(path);
  return svg;
}

export function fileName(path = "") { return path.split(/[\\/]/).at(-1) || ""; }
export function dirName(path = "") {
  const pos = Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\"));
  return pos >= 0 ? path.slice(0, pos + (pos === 0 ? 1 : 0)) : "";
}
