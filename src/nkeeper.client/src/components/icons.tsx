/** Small inline icon set (24px stroke icons) so modules don't need an icon library. */
import type { SVGProps } from 'react';

export type IconComponent = (props: SVGProps<SVGSVGElement>) => React.JSX.Element;

function icon(path: React.ReactNode): IconComponent {
  return function Icon(props) {
    return (
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth={1.75}
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
        {...props}
      >
        {path}
      </svg>
    );
  };
}

export const HomeIcon = icon(<path d="M3 11.5 12 4l9 7.5M5 10v10h14V10" />);

export const ServerIcon = icon(
  <>
    <rect x="3" y="4" width="18" height="7" rx="1.5" />
    <rect x="3" y="13" width="18" height="7" rx="1.5" />
    <path d="M7 7.5h.01M7 16.5h.01" />
  </>,
);

export const UserIcon = icon(
  <>
    <circle cx="12" cy="8" r="4" />
    <path d="M4 20c1.5-3.5 4.5-5 8-5s6.5 1.5 8 5" />
  </>,
);

export const LogoutIcon = icon(<path d="M15 4h4v16h-4M10 8l-4 4 4 4M6 12h10" />);

export const FolderIcon = icon(<path d="M3 6.5A1.5 1.5 0 0 1 4.5 5H9l2 2h8.5A1.5 1.5 0 0 1 21 8.5v9a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 17.5z" />);

export const PlugIcon = icon(<path d="M9 3v5M15 3v5M7 8h10v3a5 5 0 0 1-10 0zM12 16v5" />);
