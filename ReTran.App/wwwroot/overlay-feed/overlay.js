/*
 * ReTran OBS overlay page script.
 * Subscribes to the SSE feed at /events and renders one absolutely-positioned
 * element per translated box, using the 0-1 coordinates sent by the server.
 * Font size follows the same rule as the native overlays: the largest size
 * whose wrapped block fits the box interior (no line cap; the pill grows
 * downward when even the floor size does not fit) — spec 2026-11-13.
 * No external network resources: everything is served by the same loopback server.
 */
(function () {
  "use strict";

  // Mirror of BoxAnnotationStyle constants (Core/App): change all three together.
  // Bản sao hằng số BoxAnnotationStyle (Core/App): sửa thì sửa cả ba nơi.
  var MIN_FONT_PX = 9;
  var MAX_FONT_PX = 40;
  var FONT_HEIGHT_RATIO = 0.5;
  var FONT_FAMILY = "'Segoe UI', system-ui, sans-serif";

  function padX(px) { return Math.max(4, px * 0.35); }
  function padY(px) { return Math.max(2, px * 0.12); }

  var stage = document.getElementById("stage");

  /* One hidden element re-used for every measurement (mirrors MeasureBlock + PadY). */
  var measureEl = document.createElement("div");
  measureEl.style.cssText =
    "position:absolute;left:-10000px;top:0;visibility:hidden;white-space:pre-wrap;" +
    "word-wrap:break-word;font-family:" + FONT_FAMILY + ";line-height:1.15;";
  document.body.appendChild(measureEl);

  function measureHeight(text, px, innerWidth) {
    measureEl.style.width = Math.max(1, innerWidth - 2 * padX(px)) + "px";
    measureEl.style.fontSize = px + "px";
    measureEl.textContent = text;
    return measureEl.offsetHeight;
  }

  function fits(text, px, innerWidth, innerHeight) {
    return measureHeight(text, px, innerWidth) + 2 * padY(px) <= innerHeight + 0.5;
  }

  /*
   * Largest size (floor..cap) whose wrapped block fits the inner width/height — no line cap.
   * The cap scales with the canvas width like the native viewport scale (40 × max(0.5, w/1280)).
   */
  function fitFont(text, innerWidth, innerHeight, boxHeightPx) {
    var cap = Math.max(20, MAX_FONT_PX * Math.max(0.5, window.innerWidth / 1280));
    var high = Math.round(Math.max(12, Math.min(cap, FONT_HEIGHT_RATIO * boxHeightPx)));
    if (fits(text, high, innerWidth, innerHeight)) return high;

    var low = MIN_FONT_PX, best = low;
    var from = low + 1, to = high - 1;
    while (from <= to) {
      var mid = (from + to) >> 1;
      if (fits(text, mid, innerWidth, innerHeight)) { best = mid; from = mid + 1; }
      else { to = mid - 1; }
    }
    return best;
  }

  function clearStage() {
    while (stage.firstChild) {
      stage.removeChild(stage.firstChild);
    }
  }

  function tick(name) {
    var el = document.createElement("span");
    el.className = "tick " + name;
    return el;
  }

  /* One box: percentage geometry from 0-1 coordinates, dark pill + four teal corner ticks. */
  function renderBox(box, viewWidth, viewHeight) {
    var el = document.createElement("div");
    el.className = "box";
    el.style.left = (box.x * 100) + "%";
    el.style.top = (box.y * 100) + "%";
    el.style.width = (box.w * 100) + "%";
    el.style.height = (box.h * 100) + "%";

    var text = box.translated ? box.translated : box.text;
    var pxWidth = box.w * viewWidth;
    var pxHeight = box.h * viewHeight;
    var fontPx = fitFont(text, pxWidth, pxHeight, pxHeight);

    var pill = document.createElement("div");
    pill.className = "pill";
    pill.style.fontSize = fontPx + "px";
    pill.style.padding = padY(fontPx) + "px " + padX(fontPx) + "px";
    pill.textContent = text;
    el.appendChild(pill);

    el.appendChild(tick("t1"));
    el.appendChild(tick("t2"));
    el.appendChild(tick("t3"));
    el.appendChild(tick("t4"));
    return el;
  }

  /* Each event replaces the whole scene: clear, then render every box fresh. */
  function onMessage(event) {
    var payload;
    try {
      payload = JSON.parse(event.data);
    } catch (error) {
      return; // malformed frame — keep showing the last rendered one
    }

    clearStage();
    var boxes = payload.boxes || [];
    var viewWidth = window.innerWidth;
    var viewHeight = window.innerHeight;
    var fragment = document.createDocumentFragment();
    for (var i = 0; i < boxes.length; i++) {
      fragment.appendChild(renderBox(boxes[i], viewWidth, viewHeight));
    }
    stage.appendChild(fragment);
  }

  var source = new EventSource("/events");
  source.onmessage = onMessage;

  /* Debug aid for the OBS smoke test: SSE errors are otherwise invisible in a browser source. */
  source.onerror = function (event) {
    console.warn("ReTran overlay: SSE /events error — EventSource reconnects automatically.", event);
  };
})();
