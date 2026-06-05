const sourceNames = {
  QQMusic: "QQ音乐",
  Netease: "网易云音乐",
  Kugou: "酷狗音乐",
  Spotify: "Spotify",
};

let settings = null;
let draggedSource = null;
let applyTimer = 0;
let inputsBound = false;

const post = (message) => {
  window.chrome?.webview?.postMessage(message);
};

const showToast = (text) => {
  const toast = document.querySelector("#toast");
  toast.textContent = text;
  toast.classList.add("show");
  window.clearTimeout(showToast.timer);
  showToast.timer = window.setTimeout(() => toast.classList.remove("show"), 1600);
};

const normalizeOrder = (order) => {
  const defaults = ["QQMusic", "Netease", "Kugou", "Spotify"];
  const result = [];
  for (const item of [...(order || []), ...defaults]) {
    if (defaults.includes(item) && !result.includes(item)) {
      result.push(item);
    }
  }
  return result;
};

const scheduleApply = () => {
  if (!settings) {
    return;
  }

  window.clearTimeout(applyTimer);
  applyTimer = window.setTimeout(() => {
    post({ type: "settingsChanged", settings });
  }, 220);
};

const setValue = (key, value) => {
  settings[key] = value;
  scheduleApply();
};

const readNumber = (input) => {
  if (input.value.trim() === "") {
    return null;
  }

  const value = Number(input.value);
  return Number.isFinite(value) ? value : null;
};

const syncInputs = () => {
  document.querySelectorAll("[data-setting]").forEach((input) => {
    const key = input.dataset.setting;
    if (!(key in settings)) {
      return;
    }

    if (input.type === "checkbox") {
      input.checked = Boolean(settings[key]);
      return;
    }

    input.value = settings[key] ?? "";
  });
};

const bindInputs = () => {
  if (inputsBound) {
    return;
  }

  document.querySelectorAll("[data-setting]").forEach((input) => {
    const key = input.dataset.setting;
    if (!(key in settings)) {
      return;
    }

    if (input.type === "checkbox") {
      input.addEventListener("change", () => setValue(key, input.checked));
      return;
    }

    input.addEventListener("input", () => {
      if (input.type === "number") {
        const value = readNumber(input);
        if (value === null) {
          return;
        }

        setValue(key, value);
        return;
      }

      setValue(key, input.value);
    });
    input.addEventListener("change", () => {
      if (input.type === "number") {
        const value = readNumber(input);
        if (value !== null) {
          setValue(key, value);
        }
        return;
      }

      setValue(key, input.value);
    });
  });

  inputsBound = true;
};

const renderOrder = () => {
  const list = document.querySelector("#recognitionOrder");
  list.innerHTML = "";
  settings.sourceRecognitionOrder = normalizeOrder(settings.sourceRecognitionOrder);

  for (const source of settings.sourceRecognitionOrder) {
    const row = document.createElement("div");
    row.className = "order-row";
    row.draggable = true;
    row.dataset.source = source;
    row.innerHTML = `<span class="drag-handle">☰</span><strong>${sourceNames[source] || source}</strong>`;

    row.addEventListener("dragstart", () => {
      draggedSource = source;
      row.classList.add("dragging");
    });
    row.addEventListener("dragend", () => {
      draggedSource = null;
      row.classList.remove("dragging");
    });
    row.addEventListener("dragover", (event) => {
      event.preventDefault();
    });
    row.addEventListener("drop", (event) => {
      event.preventDefault();
      if (!draggedSource || draggedSource === source) {
        return;
      }

      const order = settings.sourceRecognitionOrder.filter((item) => item !== draggedSource);
      const targetIndex = order.indexOf(source);
      order.splice(targetIndex, 0, draggedSource);
      settings.sourceRecognitionOrder = order;
      renderOrder();
      scheduleApply();
    });

    list.appendChild(row);
  }
};

const bindNavigation = () => {
  const content = document.querySelector(".content");
  document.querySelectorAll(".nav-item").forEach((button) => {
    button.addEventListener("click", () => {
      document.querySelectorAll(".nav-item").forEach((item) => item.classList.remove("active"));
      button.classList.add("active");
      const target = document.querySelector(`#${button.dataset.target}`);
      if (!target || !content) {
        return;
      }

      content.scrollTo({
        top: target.offsetTop - 12,
        behavior: "smooth",
      });
    });
  });
};

const renderSettings = (nextSettings, accent) => {
  settings = {
    ...nextSettings,
    sourceRecognitionOrder: normalizeOrder(nextSettings.sourceRecognitionOrder),
  };

  if (accent) {
    document.documentElement.style.setProperty("--accent", accent);
  }

  syncInputs();
  bindInputs();
  renderOrder();
};

window.chrome?.webview?.addEventListener("message", (event) => {
  const message = event.data;
  if (message?.type === "settingsLoaded") {
    renderSettings(message.settings, message.accent);
  }

  if (message?.type === "cacheCleared") {
    showToast("歌词缓存已清除");
  }
});

document.querySelector("#clearCacheButton").addEventListener("click", () => {
  post({ type: "clearCache" });
});

bindNavigation();
post({ type: "ready" });
