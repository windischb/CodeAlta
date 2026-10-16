import { useEffect, useRef, useState } from "react";
import { Button, Callout, HTMLSelect, InputGroup, Switch } from "@blueprintjs/core";
import { remoteHost } from "#neoastra";
import { AppIcon } from "../AppIcon";
import type { MessageKey } from "../localization";
import { RemoteLinkQr } from "../RemoteLinkQr";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import { bindChoices, bindLabel, bindWarning, type RemoteHostState } from "./remoteHost";

type Copy = (text: string) => Promise<void>;

/**
 * Settings page of the remote server of the application: the switch that lets other computers use this CodeAlta
 * in a browser, the address it listens on, the address to open and how many remote windows are connected.
 */
export function RemoteHostSettings({ epoch, api = remoteHost, copy = text => navigator.clipboard.writeText(text) }: {
  epoch: string | null;
  api?: Pick<typeof remoteHost, "status" | "setEnabled" | "setBind">;
  copy?: Copy;
}) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload, mutate } = useSettingsEditor(options => api.status({ expectedEpoch: epoch }, options), `${epoch}`);
  return <SettingsPage className="remote-host-settings" label={t("Remote access")} group="Advanced" title="Remote access"
    description="The page of CodeAlta in a browser on another computer, with everything this window does."
    notice={notice} loading={loading} busy={busy} onReload={reload}>
    {!listing ? <SettingsUnavailable loading={loading} icon="remote" title="Remote access unavailable" />
      : <RemoteHostView state={listing} busy={busy} copy={copy}
        onEnable={enabled => void mutate(async () => ({ status: (await api.setEnabled({ expectedEpoch: epoch, enabled }, { timeoutMilliseconds: 30_000 })).status }))}
        onBind={host => void mutate(async () => ({ status: (await api.setBind({ expectedEpoch: epoch, host }, { timeoutMilliseconds: 30_000 })).status }))} />}
  </SettingsPage>;
}

/** What the page shows of the server: its switch, where it listens, and what a browser needs while it runs. */
export function RemoteHostView({ state, busy, onEnable, onBind, copy }: {
  state: RemoteHostState; busy: boolean; onEnable: (enabled: boolean) => void; onBind: (host: string) => void; copy: Copy;
}) {
  const { t } = useShellLanguage();
  const running = state.state === "running" && !!state.url;
  return <>
    <Switch className="remote-host-switch" checked={state.enabled} disabled={busy} label={t("Allow other computers to use this CodeAlta")}
      onChange={event => onEnable(event.currentTarget.checked)} />
    {/* There is no login yet: the address is the whole protection, and the page says so while the server is on. */}
    {state.enabled && <Callout intent="warning" compact className="remote-host-warning">
      {t("There is no login yet: whoever reaches this address has your control of CodeAlta. Use a private network such as Tailscale, and turn it off when not needed.")}</Callout>}
    <label className="remote-host-field"><span>{t("Listen on")}</span>
      <HTMLSelect value={state.bind} disabled={busy} aria-label={t("Listen on")} onChange={event => onBind(event.currentTarget.value)}>
        {bindChoices(state.addresses, state.bind).map(address => <option key={address.host} value={address.host}>{bindLabel(address, t)}</option>)}
      </HTMLSelect>
      {bindWarning(state.bind) && <span className="remote-host-reach">{t("Other computers on that network can reach this CodeAlta.")}</span>}
    </label>
    {state.state === "failed" && <Callout intent="warning" compact role="alert">
      {t("The remote server is not listening.")}{state.error && <div className="config-editor-diagnostic">{state.error}</div>}</Callout>}
    {running && <>
      <CopyField label="Address" copyLabel="Copy address" value={state.url!} copy={copy} />
      <div className="remote-host-open">
        <RemoteLinkQr url={state.url!} label={t("Open on another device")} />
        <span className="remote-host-clients">{t("Connected windows: {count}", { count: state.clients })}</span>
      </div>
    </>}
  </>;
}

function CopyField({ label, copyLabel, value, copy }: { label: MessageKey; copyLabel: MessageKey; value: string; copy: Copy }) {
  const { t } = useShellLanguage();
  return <label className="remote-host-field"><span>{t(label)}</span>
    <InputGroup readOnly value={value} aria-label={t(label)} onFocus={event => event.currentTarget.select()}
      rightElement={<CopyButton label={copyLabel} text={value} copy={copy} />} />
  </label>;
}

function CopyButton({ label, text, copy }: { label: MessageKey; text: string; copy: Copy }) {
  const { t } = useShellLanguage();
  const [copied, setCopied] = useState(false);
  const timer = useRef<number | undefined>(undefined);
  useEffect(() => () => window.clearTimeout(timer.current), []);
  return <Button size="small" variant="minimal" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />}
    aria-label={t(label)} title={t(copied ? "Copied" : label)}
    onClick={() => void copy(text).then(() => {
      setCopied(true);
      window.clearTimeout(timer.current);
      timer.current = window.setTimeout(() => setCopied(false), 1400);
    }, () => { /* The clipboard is not available: nothing is copied. */ })} />;
}
