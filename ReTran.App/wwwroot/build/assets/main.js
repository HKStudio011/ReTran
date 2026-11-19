//#region src/ts/theme-manager.ts
var e = "system", t = "retran-theme", n = "retran-color";
function r(t) {
	e = t;
	let n = document.documentElement, r = t === "system" ? window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light" : t;
	n.setAttribute("data-theme", r);
}
function i(e) {
	let t = document.documentElement;
	!e || e === "default" ? t.removeAttribute("data-color") : t.setAttribute("data-color", e);
}
window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", (t) => {
	e === "system" && document.documentElement.setAttribute("data-theme", t.matches ? "dark" : "light");
});
function a(e) {
	localStorage.setItem(t, e), r(e);
}
function o(e) {
	localStorage.setItem(n, e), i(e);
}
function s() {
	return localStorage.getItem(t) ?? "system";
}
function c() {
	let e = (document.documentElement.getAttribute("data-theme") === "dark" ? "dark" : "light") == "dark" ? "light" : "dark";
	return a(e), e;
}
function l() {
	r(localStorage.getItem(t) ?? "system"), i(localStorage.getItem(n) ?? "default");
}
l();
//#endregion
//#region src/ts/main.ts
async function u(e) {
	await navigator.clipboard.writeText(e);
}
function d() {
	return localStorage.getItem("retran.lang");
}
function f(e) {
	localStorage.setItem("retran.lang", e);
}
//#endregion
export { i as applyColor, r as applyTheme, u as copyText, d as getLang, s as getThemeSetting, l as initTheme, o as setColor, f as setLang, a as setTheme, c as toggleTheme };
