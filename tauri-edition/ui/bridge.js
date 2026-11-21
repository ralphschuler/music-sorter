export function invoke(command, args = {}) {
  const tauri = window.__TAURI__?.core;
  if (typeof tauri?.invoke !== "function") {
    return Promise.reject(new Error("Open Music Sorter as a desktop app to use local files."));
  }
  return tauri.invoke(command, args);
}
