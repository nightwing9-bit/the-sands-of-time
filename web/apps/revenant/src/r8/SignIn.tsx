import type { FormEvent } from "react";
import { ErrorText, Hourglass, Icon } from "./shared.tsx";

const tabs = [["tower", "Control Tower"], ["held", "Held Queue"], ["liquidity", "Liquidity"], ["rewind", "Rewind"], ["audit", "Audit"]] as const;

/** Real staff sign-in: username, password and optional TOTP against /auth/staff/login. No SSO exists, so none is offered. */
export function SignIn(p: { login: string; password: string; totp: string; setLogin: (v: string) => void; setPassword: (v: string) => void;
  setTotp: (v: string) => void; onSubmit: (e: FormEvent) => void; busy: boolean; error: string }) {
  return <div className="r8-gate">
    {/* Faded, inert chrome: no links or financial actions exist before authentication. */}
    <div className="r8-mast is-inert" aria-hidden="true">
      <div className="r8-brand"><Hourglass className="r8-brand-mark" /><span>Revenant</span></div>
      <div className="r8-nav">{tabs.map(([icon, name]) => <span key={icon}><Icon name={icon} /><span>{name}</span></span>)}</div>
      <div className="r8-mast-right"><span className="r8-chip is-synthetic"><i />Synthetic data</span></div>
    </div>
    <div className="r8-statusline is-inert" aria-hidden="true"><span>Synthetic data — no real funds</span><i /><span>Staff access required</span>
      <span className="r8-status-right">SYNTHETIC ENVIRONMENT <em>/</em> 6 CONNECTED BANKS</span></div>
    <div className="r8-gate-stage">
      <section className="r8-signin" aria-labelledby="r8-signin-title">
        <div className="r8-signin-brand">
          <div className="r8-signin-logo"><Hourglass className="r8-brand-mark" /><span>Revenant</span></div>
          <div className="r8-eyebrow">THE LEDGER REMEMBERS</div>
          <svg className="r8-signin-motif" viewBox="0 0 420 60" aria-hidden="true">
            <line x1="0" y1="40" x2="420" y2="40" />
            {Array.from({ length: 13 }, (_, i) => <line key={i} x1={16 + i * 32} x2={16 + i * 32} y1="34" y2="46" />)}
            <path className="r8-motif-alt" d="M214 40C240 40 250 22 278 22H352" /><path className="r8-motif-alt" d="m346 17 6 5-6 5" />
            <rect x="208" y="34" width="12" height="12" transform="rotate(45 214 40)" />
          </svg>
          <h1 id="r8-signin-title">Decisions, with a record of <em>what might have been.</em></h1>
        </div>
        <div className="r8-signin-form">
          <p className="r8-signin-lede">Access the synthetic settlement workspace. Sign in with your staff credentials to operate a six-bank scenario,
            review held payments and compare verified financial histories.</p>
          <form onSubmit={p.onSubmit}>
            <label>Username<input autoComplete="username" required value={p.login} onChange={e => p.setLogin(e.target.value)} /></label>
            <label>Password<input autoComplete="current-password" type="password" required value={p.password} onChange={e => p.setPassword(e.target.value)} /></label>
            <label>MFA code<input inputMode="numeric" autoComplete="one-time-code" value={p.totp} onChange={e => p.setTotp(e.target.value)} aria-describedby="r8-mfa-help" /></label>
            <small id="r8-mfa-help">Optional at sign-in. Approvals, rejections and sandbox interventions need a fresh MFA code.</small>
            <button className="r8-btn is-primary is-block" disabled={p.busy}>{p.busy ? "Signing in…" : "Sign in"}<Icon name="arrow" /></button>
          </form>
          <ErrorText error={p.error} />
          <div className="r8-signin-foot"><span>Staff accounts only · synthetic environment</span><span className="r8-chip is-synthetic"><i />Synthetic data</span></div>
        </div>
      </section>
    </div>
  </div>;
}
