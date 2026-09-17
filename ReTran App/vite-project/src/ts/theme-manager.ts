let currentThemeSetting = 'system';

function applyTheme(theme: string) {
    currentThemeSetting = theme;
    const html = document.documentElement;
    const resolvedTheme = theme === 'system'
        ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
        : theme;
    html.setAttribute('data-theme', resolvedTheme);
}

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

const THEME_KEY = "retran-theme";
const COLOR_KEY = "retran-color";

/** Applies a theme and persists the choice. Áp dụng theme và lưu lựa chọn. */
export function setTheme(theme: string): void {
  localStorage.setItem(THEME_KEY, theme);
  applyTheme(theme);
}

/** Applies a color pair and persists the choice. Áp dụng cặp màu và lưu lựa chọn. */
export function setColor(colorId: string): void {
  localStorage.setItem(COLOR_KEY, colorId);
  applyColor(colorId);
}

/** Restores saved theme/color on startup. Khôi phục theme/màu đã lưu khi khởi động. */
export function initTheme(): void {
  applyTheme(localStorage.getItem(THEME_KEY) ?? "system");
  applyColor(localStorage.getItem(COLOR_KEY) ?? "default");
}

initTheme();
