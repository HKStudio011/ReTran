/*
 * ReTran OBS overlay page script.
 * Subscribes to the SSE feed at /events and renders one absolutely-positioned
 * element per translated box, using the 0-1 coordinates sent by the server.
 * No external network resources: everything is served by the same loopback server.
 */
(function () {
  "use strict";

  var MIN_FONT_PX = 10;
  var MAX_FONT_PX = 40;
  var FONT_HEIGHT_RATIO = 0.5;

  var stage = document.getElementById("stage");

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
  function renderBox(box, viewHeight) {
    var el = document.createElement("div");
    el.className = "box";
    el.style.left = (box.x * 100) + "%";
    el.style.top = (box.y * 100) + "%";
    el.style.width = (box.w * 100) + "%";
    el.style.height = (box.h * 100) + "%";

    /* Font size derives from the box's pixel height (~0.5x, clamped 10-40 px). */
    var pxHeight = box.h * viewHeight;
    var fontPx = Math.round(
      Math.max(MIN_FONT_PX, Math.min(MAX_FONT_PX, pxHeight * FONT_HEIGHT_RATIO))
    );
    el.style.fontSize = fontPx + "px";

    var pill = document.createElement("div");
    pill.className = "pill";
    pill.textContent = box.translated ? box.translated : box.text;
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
    var viewHeight = window.innerHeight;
    var fragment = document.createDocumentFragment();
    for (var i = 0; i < boxes.length; i++) {
      fragment.appendChild(renderBox(boxes[i], viewHeight));
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
