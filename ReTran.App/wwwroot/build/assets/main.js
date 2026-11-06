//#region src/ts/theme-manager.ts
var e = "system", t = "retran-theme", n = "retran-color", r = "retran-accent";
function i(t) {
	e = t;
	let n = document.documentElement, r = t === "system" ? window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light" : t;
	n.setAttribute("data-theme", r);
}
function a(e) {
	let t = document.documentElement;
	!e || e === "default" ? t.removeAttribute("data-color") : t.setAttribute("data-color", e);
}
function o(e) {
	let t = document.documentElement;
	!e || e === "amber" ? t.setAttribute("data-accent", "amber") : t.setAttribute("data-accent", e);
}
window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", (t) => {
	e === "system" && document.documentElement.setAttribute("data-theme", t.matches ? "dark" : "light");
});
function s(e) {
	localStorage.setItem(t, e), i(e);
}
function c(e) {
	localStorage.setItem(n, e), a(e);
}
function l(e) {
	localStorage.setItem(r, e), o(e);
}
function u() {
	return localStorage.getItem(t) ?? "system";
}
function d() {
	return localStorage.getItem(r) ?? "amber";
}
function f() {
	let e = (document.documentElement.getAttribute("data-theme") === "dark" ? "dark" : "light") == "dark" ? "light" : "dark";
	return s(e), e;
}
function p() {
	i(localStorage.getItem(t) ?? "system"), a(localStorage.getItem(n) ?? "default"), o(localStorage.getItem(r) ?? "amber");
}
p();
//#endregion
//#region src/ts/main.ts
async function m(e) {
	await navigator.clipboard.writeText(e);
}
function h() {
	return localStorage.getItem("retran.lang");
}
function g(e) {
	localStorage.setItem("retran.lang", e);
}
//#endregion
export { o as applyAccent, a as applyColor, i as applyTheme, m as copyText, d as getAccent, h as getLang, u as getThemeSetting, p as initTheme, l as setAccent, c as setColor, g as setLang, s as setTheme, f as toggleTheme };
