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
