(() => {
  "use strict";

  const stocks = new Map();
  let marketOpen = false;
  let connected = false;
  let searchTimer = null;
  let searchVisible = false;

  const webview = window.chrome && window.chrome.webview;
  const $ = (id) => document.getElementById(id);
  const searchInput = $("searchInput");
  const searchResults = $("searchResults");
  const stockList = $("stockList");
  const emptyTip = $("emptyTip");
  const marketBar = $("marketBar");
  const connDot = $("connDot");

  function post(obj) {
    if (webview) {
      webview.postMessage(JSON.stringify(obj));
    }
  }

  // ---------- Render helpers ----------
  function trendClass(value) {
    const num = parseFloat(value);
    if (Number.isNaN(num) || num === 0) return "flat";
    return num > 0 ? "up" : "down";
  }

  function formatPercent(value) {
    const num = parseFloat(value);
    if (Number.isNaN(num)) return "--";
    const sign = num > 0 ? "+" : "";
    return `${sign}${num.toFixed(2)}%`;
  }

  function formatPrice(value) {
    const num = parseFloat(value);
    if (Number.isNaN(num) || !value) return "--";
    return num.toFixed(2);
  }

  function formatChange(value) {
    const num = parseFloat(value);
    if (Number.isNaN(num) || !value) return "--";
    const sign = num > 0 ? "+" : "";
    return `${sign}${num.toFixed(2)}`;
  }

  function formatVolume(value) {
    const num = parseFloat(value);
    if (Number.isNaN(num) || !value) return "--";
    if (num >= 100000000) return (num / 100000000).toFixed(2) + "亿";
    if (num >= 10000) return (num / 10000).toFixed(2) + "万";
    return num.toFixed(0);
  }

  function marketName(market) {
    if (market === "SH") return "沪";
    if (market === "SZ") return "深";
    if (market === "BJ") return "北";
    return market || "";
  }

  function renderMarketBar() {
    if (!window.apiKeyConfigured) {
      marketBar.innerHTML = '<span class="market-dot"></span><span id="notice">请先在 config.json 中填写 ApiKey</span>';
      return;
    }
    marketBar.className = "market-bar " + (marketOpen ? "open" : "closed");
    marketBar.innerHTML =
      '<span class="market-dot"></span><span>' + (marketOpen ? "开盘" : "休市") + "</span>";
  }

  function renderStockRow(code) {
    const item = stocks.get(code);
    if (!item) return null;

    const priceClass = item.hasQuote ? trendClass(item.changePercent || item.change) : "flat";
    const price = item.hasQuote ? formatPrice(item.price) : "--";
    const changeAmount = item.hasQuote ? formatChange(item.change) : "--";
    const changePercent = item.hasQuote ? formatPercent(item.changePercent) : "--";
    const changeDisplay = item.hasQuote ? `${changeAmount} ${changePercent}` : "--";
    const high = item.hasQuote ? formatPrice(item.high) : "--";
    const low = item.hasQuote ? formatPrice(item.low) : "--";
    const volume = item.hasQuote ? formatVolume(item.volume) : "--";
    const mkt = marketName(item.market);

    const row = document.createElement("div");
    row.className = "stock-row";
    row.dataset.code = code;
    row.innerHTML = `
      <div class="stock-left">
        <div class="stock-name">${escapeHtml(item.name)}</div>
        <div class="stock-code">${escapeHtml(item.code)}${mkt ? " · " + escapeHtml(mkt) : ""}</div>
      </div>
      <div class="stock-right">
        <div class="stock-price ${priceClass}">${price}</div>
        <div class="stock-change ${priceClass}">${changeDisplay}</div>
      </div>
      <div class="stock-info">
        <span>高 ${high}</span>
        <span>低 ${low}</span>
        <span>量 ${volume}</span>
      </div>
      <button class="stock-remove" title="删除">×</button>
    `;
    return row;
  }

  function renderAll() {
    stockList.innerHTML = "";
    if (stocks.size === 0) {
      stockList.appendChild(emptyTip);
    } else {
      for (const code of stocks.keys()) {
        const row = renderStockRow(code);
        if (row) stockList.appendChild(row);
      }
    }
    document.body.classList.toggle("closed", !marketOpen);
    renderMarketBar();
  }

  function updateQuote(quote) {
    const item = stocks.get(quote.symbol);
    if (!item) return;

    item.price = quote.price;
    item.change = quote.change;
    item.changePercent = quote.changePercent;
    item.high = quote.high || "";
    item.low = quote.low || "";
    item.volume = quote.volume || "";
    item.hasQuote = true;

    const row = stockList.querySelector(`.stock-row[data-code="${quote.symbol}"]`);
    if (!row) return;

    const trend = trendClass(quote.changePercent || quote.change);
    const cls = `stock-price ${trend}`;
    const cls2 = `stock-change ${trend}`;

    row.querySelector(".stock-price").className = cls;
    row.querySelector(".stock-price").textContent = formatPrice(quote.price);
    row.querySelector(".stock-change").className = cls2;
    row.querySelector(".stock-change").textContent = `${formatChange(quote.change)} ${formatPercent(quote.changePercent)}`;

    const infoSpans = row.querySelectorAll(".stock-info span");
    if (infoSpans.length >= 3) {
      infoSpans[0].textContent = `高 ${formatPrice(quote.high)}`;
      infoSpans[1].textContent = `低 ${formatPrice(quote.low)}`;
      infoSpans[2].textContent = `量 ${formatVolume(quote.volume)}`;
    }
  }

  function escapeHtml(value) {
    return String(value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  // ---------- Search ----------
  function updateSearchMaxHeight() {
    if (!searchVisible || searchResults.hidden) return;
    const rect = searchResults.getBoundingClientRect();
    const available = window.innerHeight - rect.top - 10;
    const maxHeight = Math.min(240, Math.max(80, Math.floor(available)));
    if (searchResults.style.maxHeight !== maxHeight + "px") {
      searchResults.style.maxHeight = maxHeight + "px";
    }
  }

  function showSearchResults(items) {
    if (!searchInput.value.trim()) {
      hideSearchResults();
      return;
    }

    searchResults.innerHTML = "";
    searchResults.hidden = false;
    searchVisible = true;
    searchResults.style.maxHeight = "";

    if (!items || items.length === 0) {
      const div = document.createElement("div");
      div.className = "search-result";
      div.textContent = "未找到匹配股票";
      searchResults.appendChild(div);
    } else {
      for (const item of items) {
        const div = document.createElement("div");
        div.className = "search-result";
        div.innerHTML = `
          <span class="result-name">${escapeHtml(item.name)}</span>
          <span class="result-code">${escapeHtml(item.code)}</span>
          <span class="market-tag">${escapeHtml(item.market)}</span>
        `;
        div.addEventListener("click", () => {
          post({ type: "add", code: item.code });
          searchInput.value = "";
          hideSearchResults();
        });
        searchResults.appendChild(div);
      }
    }

    // Ask C# to temporarily enlarge the window so the dropdown is not clipped.
    const extraHeight = Math.min(searchResults.scrollHeight, 240);
    post({ type: "searchExpand", extraHeight });
    requestAnimationFrame(updateSearchMaxHeight);
  }

  function hideSearchResults() {
    if (!searchVisible) return;
    searchVisible = false;
    searchResults.hidden = true;
    post({ type: "searchCollapse" });
  }

  searchInput.addEventListener("input", () => {
    const query = searchInput.value.trim();
    if (!query) {
      hideSearchResults();
      return;
    }
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => post({ type: "search", query }), 150);
  });

  searchInput.addEventListener("keydown", (e) => {
    if (e.key === "Escape") {
      hideSearchResults();
      searchInput.blur();
    }
  });

  window.addEventListener("resize", updateSearchMaxHeight);

  document.addEventListener("click", (e) => {
    if (!e.target.closest(".search-wrap")) {
      hideSearchResults();
    }
  });

  // ---------- Stock list events ----------
  stockList.addEventListener("click", (e) => {
    const btn = e.target.closest(".stock-remove");
    if (!btn) return;
    const row = btn.closest(".stock-row");
    if (row && row.dataset.code) {
      post({ type: "remove", code: row.dataset.code });
    }
  });

  // ---------- Titlebar drag & actions ----------
  document.querySelector(".titlebar").addEventListener("mousedown", (e) => {
    if (e.target.closest(".action-btn")) return;
    post({ type: "move" });
    e.preventDefault();
  });

  $("minBtn").addEventListener("click", () => post({ type: "minimize" }));
  $("closeBtn").addEventListener("click", () => post({ type: "close" }));

  // ---------- WebView2 messages ----------
  function handleMessage(msg) {
    switch (msg.type) {
      case "init":
        stocks.clear();
        for (const s of msg.stocks || []) {
          stocks.set(s.code, { ...s });
        }
        marketOpen = !!msg.marketOpen;
        connected = !!msg.connected;
        window.apiKeyConfigured = !!msg.apiKeyConfigured;
        renderAll();
        break;

      case "quote":
        if (msg.quote) updateQuote(msg.quote);
        break;

      case "searchResult":
        showSearchResults(msg.items);
        break;

      case "marketState":
        marketOpen = !!msg.marketOpen;
        document.body.classList.toggle("closed", !marketOpen);
        renderMarketBar();
        break;

      case "connection":
        connected = !!msg.connected;
        connDot.className = "conn-dot " + (connected ? "online" : "offline");
        break;

      case "notice":
        marketBar.innerHTML =
          '<span class="market-dot"></span><span id="notice">' + escapeHtml(msg.message || "") + "</span>";
        break;
    }
  }

  if (webview) {
    webview.addEventListener("message", (e) => {
      try {
        const msg = typeof e.data === "string" ? JSON.parse(e.data) : e.data;
        handleMessage(msg);
      } catch {
        // ignore
      }
    });
  }

  document.addEventListener("DOMContentLoaded", () => {
    connDot.className = "conn-dot offline";
    post({ type: "ready" });
  });

  // If the script runs after DOMContentLoaded already fired.
  if (document.readyState !== "loading") {
    connDot.className = "conn-dot offline";
    post({ type: "ready" });
  }
})();