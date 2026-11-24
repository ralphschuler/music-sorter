const labels = {
  windows: "Download for Windows x64",
  macosArm: "Download for macOS (Apple Silicon)",
  macosIntel: "Download for macOS (Intel)",
  linux: "Download for Linux x64"
};

export function platformFromClient(client = navigator) {
  const platform = String(client.userAgentData?.platform || client.platform || client.userAgent || "").toLowerCase();
  if (platform.includes("win")) return "windows";
  if (platform.includes("linux")) return "linux";
  return platform.includes("mac") ? "macos" : null;
}

export function macArchitecture(hints) {
  const architecture = String(hints?.architecture || "").toLowerCase();
  if (architecture.includes("arm")) return "macosArm";
  if (architecture.includes("x86") || architecture.includes("intel")) return "macosIntel";
  return null;
}

function showMenu(menu, trigger, open) {
  menu.hidden = !open;
  trigger.setAttribute("aria-expanded", String(open));
}

async function resolveChoice() {
  const platform = platformFromClient();
  if (platform !== "macos") return platform;
  // Safari and privacy-restricted browsers often report "Intel Mac" for Apple
  // Silicon. Only an explicit Client Hint is trusted to choose a Mac binary.
  try {
    const hints = await navigator.userAgentData?.getHighEntropyValues?.(["architecture"]);
    return macArchitecture(hints);
  } catch { return null; }
}

async function mount() {
  const primary = document.getElementById("primary-download");
  const trigger = document.getElementById("download-options");
  const menu = document.getElementById("download-menu");
  const status = document.getElementById("download-status");
  if (!primary || !trigger || !menu || !status) return;
  try {
    const response = await fetch("./downloads.json", {cache: "no-store"});
    if (!response.ok) throw new Error("download manifest unavailable");
    const manifest = await response.json();
    const assets = manifest.assets || {};
    const links = Object.entries(labels).flatMap(([key, label]) => {
      const asset = assets[key];
      if (!asset?.url) return [];
      const link = document.createElement("a");
      link.href = asset.url;
      link.textContent = label;
      link.dataset.platform = key;
      return [link];
    });
    if (links.length !== 4) throw new Error("incomplete stable release");
    menu.replaceChildren(...links);
    const choice = await resolveChoice();
    const selected = choice && assets[choice];
    if (selected) {
      primary.href = selected.url;
      primary.textContent = labels[choice];
      status.textContent = `Latest stable release: ${manifest.name || manifest.tag}.`;
    } else {
      primary.removeAttribute("href");
      primary.setAttribute("role", "button");
      primary.textContent = "Choose your download";
      status.textContent = "Choose a platform to see all supported downloads.";
      primary.addEventListener("click", event => { event.preventDefault(); showMenu(menu, trigger, true); });
    }
    trigger.addEventListener("click", () => showMenu(menu, trigger, menu.hidden));
    document.addEventListener("click", event => {
      if (!event.target.closest(".download-action")) showMenu(menu, trigger, false);
    });
  } catch {
    primary.textContent = "View downloads on GitHub";
    status.textContent = "The latest download list is temporarily unavailable.";
  }
}

if (typeof document !== "undefined") mount();
