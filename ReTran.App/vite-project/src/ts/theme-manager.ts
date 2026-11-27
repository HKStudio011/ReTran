let currentThemeSetting = 'system';

const THEME_KEY = "retran-theme";
const COLOR_KEY = "retran-color";

function applyTheme(theme: string) {
    currentThemeSetting = theme;
    const html = document.documentElement;
    const resolvedTheme = theme === 'system'
        ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
        : theme;
    html.setAttribute('data-theme', resolvedTheme);
}

/**
 * Applies the legacy colour pair kept for old localStorage entries.
 * Áp dụng cặp màu legacy còn giữ cho localStorage cũ.
 * @deprecated The signal-deck palette uses a single amber brand accent.
 */
function applyColor(colorId: string) {
    const html = document.documentElement;
    if (!colorId || colorId === 'default') {
        html.removeAttribute('data-color');
    } else {
        html.setAttribute('data-color', colorId);
    }
}

window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', (e) => {
    if (currentThemeSetting === 'system') {
        document.documentElement.setAttribute('data-theme', e.matches ? 'dark' : 'light');
    }
});

export { applyTheme, applyColor };

/** Applies a theme and persists the choice. Áp dụng theme và lưu lựa chọn. */
export function setTheme(theme: string): void {
  localStorage.setItem(THEME_KEY, theme);
  applyTheme(theme);
}

/** Applies a legacy colour pair and persists the choice. Áp dụng cặp màu legacy và lưu lựa chọn. */
export function setColor(colorId: string): void {
  localStorage.setItem(COLOR_KEY, colorId);
  applyColor(colorId);
}

/** Returns the persisted theme setting ("light" | "dark" | "system"). Trả về theme đã lưu. */
export function getThemeSetting(): string {
  return localStorage.getItem(THEME_KEY) ?? "system";
}

/** Toggles the resolved theme and persists the explicit result. Đảo theme đang hiển thị và lưu kết quả. */
export function toggleTheme(): string {
  const current = document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';
  const next = current === 'dark' ? 'light' : 'dark';
  setTheme(next);
  return next;
}

/** Restores the saved theme and legacy colour on startup. Khôi phục theme và màu legacy khi khởi động. */
export function initTheme(): void {
  applyTheme(localStorage.getItem(THEME_KEY) ?? "system");
  applyColor(localStorage.getItem(COLOR_KEY) ?? "default");
}

initTheme();
