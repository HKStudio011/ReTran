import '../css/style.css';

export {
  applyTheme,
  applyColor,
  setTheme,
  setColor,
  getThemeSetting,
  toggleTheme,
  initTheme,
} from './theme-manager'

/** Copies text to the clipboard. Sao chép văn bản vào clipboard. */
export async function copyText(text: string): Promise<void> {
  await navigator.clipboard.writeText(text);
}

/**
 * Reads the persisted UI language ("vi"/"en"), or null when unset.
 * Đọc ngôn ngữ UI đã lưu ("vi"/"en"), hoặc null nếu chưa đặt.
 */
export function getLang(): string | null {
  return localStorage.getItem('retran.lang');
}

/**
 * Persists the UI language choice to localStorage.
 * Lưu lựa chọn ngôn ngữ UI vào localStorage.
 */
export function setLang(lang: string): void {
  localStorage.setItem('retran.lang', lang);
}
