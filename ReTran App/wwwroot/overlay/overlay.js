const root = document.getElementById("overlay-root");
const es = new EventSource("./events");
es.onmessage = (e) => {
  const frame = JSON.parse(e.data);
  root.replaceChildren();
  for (const b of frame.boxes) {
    const d = document.createElement("div");
    d.className = "box";
    d.style.left = (b.x * 100) + "%"; d.style.top = (b.y * 100) + "%";
    d.style.width = (b.w * 100) + "%";
    d.textContent = b.t;
    root.appendChild(d);
  }
};
