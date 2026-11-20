// Shared Ghost Lap UI primitives (styles in styles/primitives.css, values in styles/ghost-lap-tokens.css).
import type { ButtonHTMLAttributes, HTMLAttributes, ReactNode } from "react";

export function FrostPanel({ tone = "frost", flush = false, className = "", ...rest }: HTMLAttributes<HTMLElement> & { tone?: "frost" | "slate"; flush?: boolean }) {
  return <section {...rest} className={`gl-panel${tone === "slate" ? " gl-panel--slate" : ""}${flush ? " gl-panel--flush" : ""} ${className}`.trim()} />;
}

interface GameButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, "disabled"> {
  variant?: "primary" | "quiet";
  /** Set when the action has no supported outcome yet. The button stays focusable and legible, announces the reason, and does nothing. */
  unavailable?: string;
  /** Called instead of onClick when unavailable, so the shell can say why in one live region. */
  onUnavailable?: (reason: string) => void;
}

export function GameButton({ variant = "quiet", unavailable, onUnavailable, onClick, children, ...rest }: GameButtonProps) {
  return (
    <button
      type="button"
      {...rest}
      className={`gl-button gl-button--${variant}`}
      aria-disabled={unavailable ? true : undefined}
      title={unavailable}
      onClick={unavailable ? () => onUnavailable?.(unavailable) : onClick}
    >
      {children}
    </button>
  );
}

export function StatusPill({ tone = "neutral", children, ...rest }: HTMLAttributes<HTMLElement> & { tone?: "neutral" | "ok" | "warn"; children: ReactNode }) {
  return <span {...rest} className="gl-status" data-tone={tone}>{children}</span>;
}

export function BackLink({ onClick, children = "Back" }: { onClick: () => void; children?: ReactNode }) {
  return <button type="button" className="gl-back" onClick={onClick}><span aria-hidden="true">←</span> {children}</button>;
}
