/**
 * The label of the view of every remote document session (`DesktopRemoteServer.ViewLabel`); the window's own view has
 * another one. The one place that answers "is this window remote".
 */
export const remoteViewLabel = "remote";

/** True when the page runs in a browser connected to the remote server, not in the window of the desktop app. */
export function isRemoteWindow(info: { viewLabel: string } | undefined): boolean {
  return info?.viewLabel === remoteViewLabel;
}

/** The host the page was loaded from, `<name or address>:<port>`, for a "Remote: <host>" tag. */
export function remoteHostName(location: { host: string }): string {
  return location.host;
}

/** The host of this window when it is remote, else null; the connection must exist (`connect()` resolved) to say so. */
export function remoteWindowHost(info: { viewLabel: string } | undefined, location: { host: string }): string | null {
  return isRemoteWindow(info) ? remoteHostName(location) : null;
}

/** The attribute of the root element that `remote.ts` sets before the page runs: the host of a remote window. */
export const remoteDocumentAttribute = "codealtaRemote";

/**
 * The host of this window when it is remote, read from the mark `remote.ts` leaves on the document: for what
 * cannot wait for the connection (a component that renders before it, a module without the client). Null in the
 * window of the desktop app.
 */
export function remoteDocumentHost(root: { dataset: DOMStringMap } | null = typeof document === "undefined" ? null : document.documentElement): string | null {
  return root?.dataset[remoteDocumentAttribute] || null;
}

/** The zoom steps of the window, in percent: the host's (`DesktopPreferences.ZoomSteps`), which a remote window applies itself. */
export const zoomSteps: readonly number[] = Object.freeze([25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500]);

/** The zoom after a command: the next step up (1), the next step down (-1), or 100 (0); a zoom between steps moves to the step beyond it. */
export function nextZoom(zoom: number, direction: 1 | -1 | 0): number {
  if (direction === 0) return 100;
  if (direction > 0) return zoomSteps.find(step => step > zoom) ?? zoomSteps[zoomSteps.length - 1];
  return [...zoomSteps].reverse().find(step => step < zoom) ?? zoomSteps[0];
}
