let currentThemeSetting = 'system';

const THEME_KEY = "retran-theme";
const COLOR_KEY = "retran-color";
const ACCENT_KEY = "retran-accent";

function applyTheme(theme: string) {
    currentThemeSetting = theme;
    const html = document.documentElement;
    const resolvedTheme = theme === 'system'
        ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
        : theme;
    html.setAttribute('data-theme', resolvedTheme);
}

/**
 * Applies the legacy colour pair. Giữ lại cho localStorage cũ; theme mới dùng accent.
 * @deprecated Use {@link setAccent} instead.
 */
function applyColor(colorId: string) {
    const html = document.documentElement;
    if (!colorId || colorId === 'default') {
        html.removeAttribute('data-color');
    } else {
        html.setAttribute('data-color', colorId);
    }
}

function applyAccent(accentId: string) {
    const html = document.documentElement;
    if (!accentId || accentId === 'amber') {
        html.setAttribute('data-accent', 'amber');
    } else {
        html.setAttribute('data-accent', accentId);
    }
}

window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', (e) => {
    if (currentThemeSetting === 'system') {
        document.documentElement.setAttribute('data-theme', e.matches ? 'dark' : 'light');
    }
});

export { applyTheme, applyColor, applyAccent };

/** Applies a theme and persists the choice. Áp dụng theme và lưu lựa chọn. */
export function setTheme(theme: string): void {
  localStorage.setItem(THEME_KEY, theme);
  applyTheme(theme);
}

/** Applies a legacy colour pair and persists the choice. Áp dụng cặp màu cũ và lưu lựa chọn. */
export function setColor(colorId: string): void {
  localStorage.setItem(COLOR_KEY, colorId);
  applyColor(colorId);
}

/** Applies an accent and persists the choice. Áp dụng màu nhấn và lưu lựa chọn. */
export function setAccent(accentId: string): void {
  localStorage.setItem(ACCENT_KEY, accentId);
  applyAccent(accentId);
}

/** Returns the persisted theme setting ("light" | "dark" | "system"). Trả về theme đã lưu. */
export function getThemeSetting(): string {
  return localStorage.getItem(THEME_KEY) ?? "system";
}

/** Returns the persisted accent id. Trả về mã màu nhấn đã lưu. */
export function getAccent(): string {
  return localStorage.getItem(ACCENT_KEY) ?? "amber";
}

/** Toggles the resolved theme and persists the explicit result. Đảo theme đang hiển thị và lưu kết quả. */
export function toggleTheme(): string {
  const current = document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';
  const next = current === 'dark' ? 'light' : 'dark';
  setTheme(next);
  return next;
}

/** Restores saved theme/colour/accent on startup. Khôi phục theme/màu đã lưu khi khởi động. */
export function initTheme(): void {
  applyTheme(localStorage.getItem(THEME_KEY) ?? "system");
  applyColor(localStorage.getItem(COLOR_KEY) ?? "default");
  applyAccent(localStorage.getItem(ACCENT_KEY) ?? "amber");
}

initTheme();
