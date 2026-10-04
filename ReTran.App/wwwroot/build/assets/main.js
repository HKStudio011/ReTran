//#region src/ts/theme-manager.ts
var e = "system";
function t(t) {
	e = t;
	let n = document.documentElement, r = t === "system" ? window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light" : t;
	n.setAttribute("data-theme", r);
}
function n(e) {
	let t = document.documentElement;
	!e || e === "default" ? t.removeAttribute("data-color") : t.setAttribute("data-color", e);
}
window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", (t) => {
	e === "system" && document.documentElement.setAttribute("data-theme", t.matches ? "dark" : "light");
});
var r = "retran-theme", i = "retran-color";
function a(e) {
	localStorage.setItem(r, e), t(e);
}
function o(e) {
	localStorage.setItem(i, e), n(e);
}
function s() {
	t(localStorage.getItem(r) ?? "system"), n(localStorage.getItem(i) ?? "default");
}
s();
//#endregion
//#region src/ts/main.ts
async function c(e) {
	await navigator.clipboard.writeText(e);
}
function l() {
	return localStorage.getItem("retran.lang");
}
function u(e) {
	localStorage.setItem("retran.lang", e);
}
//#endregion
export { n as applyColor, t as applyTheme, c as copyText, l as getLang, s as initTheme, o as setColor, u as setLang, a as setTheme };
