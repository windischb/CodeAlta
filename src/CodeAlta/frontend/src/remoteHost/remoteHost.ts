/**
 * The remote server of the application, as its settings page shows it: the address a browser on another computer
 * opens, the address it listens on, and how many remote windows are connected.
 */
import type { RemoteHostAddress, RemoteHostResponse } from "#neoastra";
import type { MessageKey } from "../localization";

/** What the host says about its remote server: `state` is `running`, `stopped` (turned off) or `failed` (turned on, and not listening). */
export type RemoteHostState = Readonly<RemoteHostResponse>;

/** The address of this computer alone, and the one that is every address. */
export const loopbackHost = "127.0.0.1", everyHost = "0.0.0.0";

/** The text of a choice of address: this computer, one network by the name of its interface, or every network. */
export function bindLabel(address: Pick<RemoteHostAddress, "host" | "name">, t: (key: MessageKey) => string): string {
  if (address.host === everyHost) return t("All networks");
  if (address.host === loopbackHost || address.host === "localhost" || address.host === "::1") return t("This computer only");
  return address.name ? `${address.name} (${address.host})` : address.host;
}

/** Whether an address lets other computers reach the server, which the page warns about. */
export function bindWarning(bind: string): boolean {
  return !(bind === loopbackHost || bind === "localhost" || bind === "::1");
}

/** The choices to show: the host's, with the one the server listens on added when the list does not have it (an address of the command line). */
export function bindChoices(addresses: readonly RemoteHostAddress[], bind: string): readonly RemoteHostAddress[] {
  return addresses.some(address => address.host === bind) ? addresses : [...addresses, { host: bind, name: "" }];
}
