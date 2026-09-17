import $ from "jquery";
import '../css/style.css';

export { applyTheme, applyColor } from './theme-manager'

export { $ };

/** Copies text to the clipboard. Sao chép văn bản vào clipboard. */
export async function copyText(text: string): Promise<void> {
  await navigator.clipboard.writeText(text);
}
