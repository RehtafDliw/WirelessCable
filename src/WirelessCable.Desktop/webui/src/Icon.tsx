import { ICONS } from 'oink-ui';

/** oink-ui 的图标是 20×20 视框、stroke 描边路径。这里补几个 oink 没有的图标。 */
const EXTRA: Record<string, string> = {
  cable: '<path d="M3 8.5h5.5M11.5 8.5H17M8.5 8.5v3.5a2 2 0 0 0 2 2h0M3 8.5v3a2 2 0 0 0 2 2h1M17 8.5v3a2 2 0 0 1-2 2h-1"/>',
  folder: '<path d="M2.5 5.5A1.5 1.5 0 0 1 4 4h3.4l1.6 2H16a1.5 1.5 0 0 1 1.5 1.5v7A1.5 1.5 0 0 1 16 16H4a1.5 1.5 0 0 1-1.5-1.5z"/>',
  refresh: '<path d="M16.5 10a6.5 6.5 0 1 1-1.9-4.6M16.5 3v3.5H13"/>',
  shield: '<path d="M10 2.8 4 5.2v4.3c0 3.6 2.5 6.9 6 7.7 3.5-.8 6-4.1 6-7.7V5.2z"/>',
  phone: '<rect x="6" y="2.5" width="8" height="15" rx="2"/><path d="M9 14.6h2"/>',
  laptop: '<rect x="3" y="4.5" width="14" height="9" rx="1.6"/><path d="M1.5 16h17"/>',
  file: '<path d="M5 2.8h6l4 4V17a.8.8 0 0 1-.8.8H5a.8.8 0 0 1-.8-.8V3.6A.8.8 0 0 1 5 2.8z"/><path d="M11 2.8V7h4"/>',
  image: '<rect x="2.8" y="3.8" width="14.4" height="12.4" rx="2"/><circle cx="7.2" cy="8" r="1.4"/><path d="M3 14l4.5-4 3.5 3 2.5-2 3.5 3.4"/>',
  send: '<path d="M2.8 10 17 3.5 12.5 17l-3-5z"/><path d="M9.5 12 17 3.5"/>',
  receive: '<path d="M10 3.5v9M6.5 9.5 10 13l3.5-3.5M4.5 16.5h11"/>',
  pause: '<path d="M7.5 4.5v11M12.5 4.5v11"/>',
  play: '<path d="M7 4.5 15 10l-8 5.5z"/>',
  close: '<path d="M5.5 5.5 14.5 14.5M14.5 5.5 5.5 14.5"/>',
  link: '<path d="M8.5 11.5a3 3 0 0 0 4.4.3l2.3-2.3a3 3 0 0 0-4.2-4.2l-1.3 1.3"/><path d="M11.5 8.5a3 3 0 0 0-4.4-.3l-2.3 2.3a3 3 0 0 0 4.2 4.2l1.3-1.3"/>',
  router: '<rect x="2.5" y="12" width="15" height="4.5" rx="1.5"/><path d="M6.5 12V9.5M10 12V9.5M13.5 12V9.5M5 9.5a5.5 5.5 0 0 1 10 0"/>',
  wifi: '<path d="M2.2 6.6a11 11 0 0 1 15.6 0M5 9.8a7 7 0 0 1 10 0M7.8 13a3 3 0 0 1 4.4 0"/><circle cx="10" cy="16" r="1" fill="currentColor" stroke="none"/>',
  trash: '<path d="M4.5 6h11M8 6V4.5h4V6M6 6l.7 8.8a1.6 1.6 0 0 0 1.6 1.5h3.4a1.6 1.6 0 0 0 1.6-1.5L14 6"/>',
  check: '<path d="M4.5 10.5 8 14l7.5-8"/>',
  plus: '<path d="M10 4.5v11M4.5 10h11"/>',
  folderOpen: '<path d="M2.5 6.5A1.5 1.5 0 0 1 4 5h3.4l1.6 2H16a1.5 1.5 0 0 1 1.5 1.5v1.5H4.6L2.6 15.4A1.5 1.5 0 0 1 4 13.7z"/><path d="M4.6 10h13.2l-1.4 6.4a1 1 0 0 1-1 .6H4.4a1 1 0 0 1-1-1.2z"/>',
  clock: '<circle cx="10" cy="10" r="7"/><path d="M10 6v4l2.5 1.5"/>',
  arrowRight: '<path d="M4 10h12M11.5 5.5 16 10l-4.5 4.5"/>',
  arrowLeft: '<path d="M16 10H4M8.5 5.5 4 10l4.5 4.5"/>',
  more: '<path d="M5 10v.1M10 10v.1M15 10v.1" stroke-width="2.6"/>',
  chevronRight: '<path d="M7.5 4.5 13 10l-5.5 5.5"/>',
  chevronDown: '<path d="M4.5 7.5 10 13l5.5-5.5"/>',
  search: '<circle cx="9" cy="9" r="5.5"/><path d="M13.2 13.2 17 17"/>',
  user: '<circle cx="10" cy="7" r="3.2"/><path d="M4 16.5c.7-2.8 3-4.3 6-4.3s5.3 1.5 6 4.3"/>',
  home: '<path d="M3.5 9.2 10 3.5l6.5 5.7V15a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 15V9.2z"/><path d="M8 16.5V12h4v4.5"/>',
  users: '<circle cx="7.5" cy="7.2" r="2.8"/><path d="M2.5 16c.6-2.5 2.5-3.8 5-3.8s4.4 1.3 5 3.8M12.8 4.6a2.8 2.8 0 0 1 0 5.2M14.8 12.5c1.4.5 2.3 1.7 2.7 3.5"/>',
  settings: '<path d="M8.6 3h2.8l.5 2 1.8 1 2-.6 1.4 2.4-1.5 1.5v1.4l1.5 1.5-1.4 2.4-2-.6-1.8 1-.5 2H8.6l-.5-2-1.8-1-2 .6-1.4-2.4L4.4 9.3V7.9L2.9 6.4l1.4-2.4 2 .6 1.8-1z"/><circle cx="10" cy="10" r="2.5"/>',
  monitor: '<rect x="2.5" y="3.5" width="15" height="10" rx="1.6"/><path d="M6.5 16.5h7M10 13.5v3" stroke-width="1.6" stroke-linecap="round" fill="none"/>',
  palette: '<path d="M10 2.5a7.5 7.5 0 000 15c1 0 1.5-.7 1.5-1.4 0-.4-.2-.7-.4-1-.3-.3-.4-.6-.4-1 0-.8.6-1.3 1.4-1.3H14a3.8 3.8 0 003.8-3.8C17.8 5.2 14.4 2.5 10 2.5z"/><circle cx="6.2" cy="9.4" r="1.1"/><circle cx="9" cy="6.2" r="1.1"/><circle cx="13" cy="6.6" r="1.1"/>',
  download: '<path d="M10 3.5v9M6.5 9.5 10 13l3.5-3.5M4.5 16.5h11"/>',
  upload: '<path d="M10 13V4M6.5 7.5 10 4l3.5 3.5M4.5 16.5h11"/>',
  sun: '<circle cx="10" cy="10" r="3.3"/><path d="M10 2.5v1.8M10 15.7v1.8M2.5 10h1.8M15.7 10h1.8M4.7 4.7 6 6M14 14l1.3 1.3M4.7 15.3 6 14M14 6l1.3-1.3"/>',
  moon: '<path d="M16.5 11.8A7 7 0 1 1 8.2 3.5a5.5 5.5 0 0 0 8.3 8.3z"/>',
  info: '<circle cx="10" cy="10" r="7"/><path d="M10 9.3v4.2M10 6.5v.1"/>',
  alert: '<path d="M10 3.5 17 16H3zM10 8.5v3.2M10 14v.1"/>',
  qr: '<rect x="3" y="3" width="5.5" height="5.5" rx="1.2"/><rect x="11.5" y="3" width="5.5" height="5.5" rx="1.2"/><rect x="3" y="11.5" width="5.5" height="5.5" rx="1.2"/><path d="M11.5 11.5h2v2h-2zM15.5 15.5h1.5v1.5h-1.5zM11.5 15.5h1.5v1.5h-1.5z"/>',
  edit: '<path d="M4 16l.8-3.3L13.4 4a1.7 1.7 0 0 1 2.4 0l.2.2a1.7 1.7 0 0 1 0 2.4L7.3 15.2 4 16z"/>',
  copy: '<rect x="7" y="7" width="9.5" height="9.5" rx="2.5"/><path d="M13 7V5.5A2 2 0 0 0 11 3.5H5.5a2 2 0 0 0-2 2V11a2 2 0 0 0 2 2H7"/>',
  stop: '<rect x="5.5" y="5.5" width="9" height="9" rx="1.6"/>',
};

export type IconName = keyof typeof EXTRA | string;

export function Icon({ name, size = 20 }: { name: IconName; size?: number }) {
  const d = EXTRA[name] ?? (ICONS as Record<string, string>)[name] ?? '';
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 20 20"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.7}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      dangerouslySetInnerHTML={{ __html: d }}
    />
  );
}
