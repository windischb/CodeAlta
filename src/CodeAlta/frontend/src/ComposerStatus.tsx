import { useContext, useEffect, useState } from "react";
import { createPortal } from "react-dom";
import { Button, Menu, MenuItem, Popover } from "@blueprintjs/core";
import type { ComposerStatusRequest } from "#neoastra";
import { AppIcon, type IconName } from "./AppIcon";
import { PaneObservingContext } from "./hostReadsContext";
import { composerStatusItems, sameComposerStatus, type ComposerStatusView } from "./pluginStatus";
import { PluginUiContext, pluginsChangedEvent } from "./pluginUi";
import { shortStatusText } from "./promptSurface/promptSurface";
import { useSurfaceDensity } from "./promptSurface/PromptSurfaceParts";
import { McpSessionDialog, type ReadMcp } from "./mcpSession/McpSessionDialog";
import { settingsNavigation } from "./settingsNavigation";
import { useShellLanguage } from "./shellLanguage";

const refreshMilliseconds = 10_000;
type Read = (request: ComposerStatusRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<unknown>;
const icons: Readonly<Record<string, IconName>> = { mcp: "server" };

/**
 * The plugin status items at the end of the composer's status line (the MCP servers and the tools the
 * session activated, for example). They are read when the composer appears, every ten seconds while it
 * stays, when the window gets the focus back and when the plugins change, so a change made in Settings or by
 * a run shows up without a reload. The composer of a hidden tab reads nothing until it is shown again. The MCP item
 * opens the window of the servers and tools of the session when `readMcp` reads them; another item that names a
 * Settings page opens it, and one that names a plugin command runs it.
 */
export function ComposerStatus({ epoch, projectId, sessionId, read, readMcp }: {
  epoch: string | null; projectId: string | null; sessionId: string | null; read: Read;
  /** Reads the MCP servers and tools of the session for the window of the MCP item; without it, the item opens Settings. */
  readMcp?: ReadMcp;
}) {
  const { t } = useShellLanguage();
  const [mcpShown, setMcpShown] = useState(false);
  const plugins = useContext(PluginUiContext);
  const observing = useContext(PaneObservingContext);
  const [shown, setShown] = useState<{ projectId: string | null; sessionId: string | null; items: readonly ComposerStatusView[] }>();
  useEffect(() => {
    if (!epoch || !observing) return;
    const abort = new AbortController();
    let reading = false;
    const refresh = () => {
      if (reading || document.visibilityState === "hidden") return;
      reading = true;
      void read({ expectedEpoch: epoch, projectId, sessionId }, { signal: abort.signal, timeoutMilliseconds: 8000 })
        .then(reply => {
          const items = abort.signal.aborted ? null : composerStatusItems(reply, projectId);
          if (items) setShown(previous => previous && previous.projectId === projectId && previous.sessionId === sessionId
            && sameComposerStatus(previous.items, items) ? previous : { projectId, sessionId, items });
        }, () => { /* The items simply stay as last read. */ })
        .finally(() => { reading = false; });
    };
    refresh();
    const timer = window.setInterval(refresh, refreshMilliseconds);
    window.addEventListener("focus", refresh);
    window.addEventListener(pluginsChangedEvent, refresh);
    return () => { abort.abort(); window.clearInterval(timer); window.removeEventListener("focus", refresh); window.removeEventListener(pluginsChangedEvent, refresh); };
  }, [epoch, projectId, sessionId, read, observing]);
  const density = useSurfaceDensity();
  const items = epoch && shown?.projectId === projectId && shown.sessionId === sessionId ? shown.items : [];
  // On the body: the status line's text styles are not the window's.
  const mcpWindow = mcpShown && epoch && readMcp ? createPortal(<McpSessionDialog epoch={epoch} projectId={projectId} sessionId={sessionId} read={readMcp}
    onClose={() => setMcpShown(false)} onOpenSettings={() => { setMcpShown(false); settingsNavigation.open("mcp"); }} />, document.body) : null;
  if (items.length === 0) return mcpWindow;
  return <span className="composer-plugin-status">{items.map(item => {
    // The whole text where there is room; its first part ("1/1") otherwise, the whole in the tooltip.
    const text = item.text && density !== "wide" ? shortStatusText(item.text) : item.text;
    const about = [item.label, item.text].filter(Boolean).join(" ");
    const content = <>{icons[item.pluginId] && <AppIcon name={icons[item.pluginId]} size={14} />}
      {item.label && <strong>{item.label}</strong>}{text && <span className="composer-plugin-status-text">{text}</span>}
      <span className="composer-plugin-status-dot" aria-hidden="true" /></>;
    return item.pluginId === "mcp" && readMcp
      ? <Button key={item.key} variant="minimal" size="small" className="composer-plugin-status-item" data-tone={item.tone} title={`${about}\n${t("Show MCP servers and tools")}`}
        aria-haspopup="dialog" onClick={() => setMcpShown(true)}>{content}</Button>
      : item.settingsPage
      ? <Button key={item.key} variant="minimal" size="small" className="composer-plugin-status-item" data-tone={item.tone} title={`${about}\n${t("Open settings")}`}
        onClick={() => settingsNavigation.open(item.settingsPage!)}>{content}</Button>
      : item.commandId && item.alternatives.length > 0
      // A split button: the item runs its command, the caret offers the other ways to run it.
      ? <span key={item.key} className="composer-plugin-status-split" data-tone={item.tone}>
        <Button variant="minimal" size="small" className="composer-plugin-status-item" data-tone={item.tone}
          title={[about, plugins.contributions.commands.find(command => command.id === item.commandId)?.description].filter(Boolean).join("\n")}
          onClick={() => plugins.run(item.commandId!, { projectId, sessionId })}>{content}</Button>
        <Popover placement="top-end" minimal content={<Menu>{item.alternatives.map(alternative =>
          <MenuItem key={`${alternative.commandId} ${alternative.argument ?? ""}`} text={alternative.label}
            onClick={() => plugins.run(alternative.commandId, { projectId, sessionId, argument: alternative.argument })} />)}</Menu>}>
          <Button variant="minimal" size="small" className="composer-plugin-status-item composer-plugin-status-caret" data-tone={item.tone}
            aria-label={t("More ways to open {name}", { name: item.label })} title={t("More ways to open {name}", { name: item.label })}
            icon={<AppIcon name="chevronDown" size={12} />} />
        </Popover>
      </span>
      : item.commandId
      ? <Button key={item.key} variant="minimal" size="small" className="composer-plugin-status-item" data-tone={item.tone}
        title={[about, plugins.contributions.commands.find(command => command.id === item.commandId)?.description].filter(Boolean).join("\n")}
        onClick={() => plugins.run(item.commandId!, { projectId, sessionId })}>{content}</Button>
      : <span key={item.key} className="composer-plugin-status-item" data-tone={item.tone} title={about}>{content}</span>;
  })}{mcpWindow}</span>;
}
