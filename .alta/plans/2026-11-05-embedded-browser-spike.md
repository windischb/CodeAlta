---
title: Versuch – die Seite in CodeAlta statt in einem eigenen Browser
status: done
created: 2026-11-05
summary: Ein kurzer Versuch auf macOS klärt, ob die CodeAlta-Leiste mit Anmerkungen auch in einer Browser-Ansicht von CodeAlta selbst läuft (NeoAstra, WKWebView), eingebettet neben der Session oder in einem eigenen Fenster von CodeAlta, und was dort fehlt. Er baut auf dem Windows-Branch `feature/embedded-browser` auf und entscheidet, ob sich der volle Ausbau lohnt.
---

# Versuch: die Seite in CodeAlta statt in einem eigenen Browser

Heute öffnet CodeAlta für eine Session einen echten Chromium-Browser (Chrome, Edge) und spricht ihn über CDP an: Leiste, Anmerkungen, Quellenhinweise und die Werkzeuge des Agenten. Als **zweite Option** soll die Seite auch in CodeAlta selbst laufen, ohne zweites Programm. Dieser Versuch klärt, ob das auf macOS geht, wie, und was dort nicht geht. Er baut nichts Fertiges; was er findet, steht am Ende in der Tabelle „Ergebnisse“ und entscheidet den nächsten Schritt.

## Was wir schon wissen

**Der Branch `feature/embedded-browser`** (Oktober 2026, nur Windows, 1934 Commits hinter `upstream/main`) hat das auf Windows schon gebaut, mit Plan `.alta/plans/2026-10-13-embedded-browser-annotations.md` in diesem Branch:

- ein Browser-Teil im Bereich der Session, mit Tabs der Session, Splitter, Wiederherstellen nach Neustart (`DesktopBrowsers`, `BrowserService`, `BrowserPane`, `pages.json`);
- eine eigene WebView-Umgebung mit eigenem Datenordner, ohne Bridge zur App, mit Regeln für Navigation, neue Fenster, Rechte, Downloads;
- das Verschieben und Ausblenden der nativen Ansicht über den geborgten `ICoreWebView2Controller` (COM), weil NeoAstra dafür keine Methode hatte;
- die **Überlagerung** („airspace“): eine native Ansicht liegt über allem, was die App darüberlegt. Gelöst mit einem Signal aus dem DOM (`nativeViewOcclusion.ts`), einem Standbild (~100 ms) an Stelle der Ansicht und Toasts, die dem Browser-Teil ausweichen;
- Werkzeuge des Agenten über `NeoAutomation` mit Filter pro Session (`browser_*`), ohne CDP: ein Skript in der Hauptwelt der Seite, Eingaben als DOM-Ereignisse (`isTrusted` false, kein `:hover`, keine nativen Popups);
- Anmerkungen mit eigenem Picker-Skript, Bild des Elements, Liste, `alta browser annotations`.

**NeoAstra 1.12** (heute in `src/Directory.Packages.props`):

| Was | Stand |
| --- | --- |
| Zweite Ansicht im Fenster, eigene Startgröße, eigenes Profil, ohne Bridge | ja (`CreateWebViewAsync`, `NeoAstraOptions.Bounds`, `Profile`, `BridgePolicy`) |
| Ansicht später verschieben, ausblenden, Tastatur geben | **weiterhin keine Methode**; auf macOS gibt es den geborgten `WKWebView*` (`GetNativeHandle(NeoNativeHandleKind.WkWebView)`), also `setFrame:`/`setHidden:` über die Objective-C-Laufzeit, wie Windows über COM |
| Ansicht, die ein eigenes Fenster füllt | ja (`NeoAstraHost.FillWindow`), so wie heute jedes Fenster von CodeAlta |
| Skript in jedes Dokument, auch in einer isolierten Welt | ja (`AddScriptAsync`, `NeoScriptOptions.IsolatedWorld`, `WorldName`; Fähigkeit `ScriptIsolatedWorld` vorher abfragen) |
| Bild des sichtbaren Bereichs, auch eines Ausschnitts | ja (`CaptureAsync`, `NeoCaptureOptions.Region`); ganze Seite nicht auf WKWebView |
| Automation für den Agenten | ja (`NeoAutomation`, `NeoAutomationPage`), Werkzeuge wie Chrome DevTools MCP |
| DevTools-Protokoll | **nein** auf macOS (WKWebView); auf Windows (WebView2) prinzipiell, nicht geprüft |

**Was auf macOS sicher fehlt:** die Quellenhinweise aus dem DevTools-Protokoll (CSS-Regeln mit Datei und Zeile, Stack der Erzeugung eines Elements). Es bleibt die Textsuche im Projekt (Text, id, Klassen, die Datei einer Seite des Datei-Servers). Außerdem rendert die Ansicht wie Safari (WebKit), nicht wie Chrome.

## Was der Versuch beantwortet

1. **Eingebettet:** Lässt sich eine zweite `WKWebView` im Hauptfenster über einem Platzhalter der Seite halten, mitwandern beim Verschieben des Splitters und beim Ändern der Fenstergröße, und ausblenden, wenn eine andere Session vorn ist? Mit welchen Koordinaten (NSView rechnet von unten links), bei Retina und bei Zoom der App?
2. **Eigenes Fenster:** Reicht als einfachere Variante ein eigenes Fenster von CodeAlta, das nur die Seite zeigt (`FillWindow`)? Es hat kein Überlagerungsproblem und braucht keine neue Methode in NeoAstra.
3. **Isolierung:** Läuft auf macOS eine zweite Umgebung oder ein eigenes Profil mit getrennten Cookies und Speicher, ohne Bridge und ohne das `app:`-Schema der App?
4. **Die Leiste:** Läuft `codealta-toolbar.js` unverändert in der isolierten Welt der Ansicht? Kommen ihre Nachrichten bei CodeAlta an, **und kann die Seite selbst keine schicken** (heute die Sicherheitsgarantie über das CDP-Binding)? Kann CodeAlta die Leiste in ihrer Welt aufrufen (`enable`, `marked`, `list`, `clear`), oder nur die Hauptwelt?
5. **Bild:** Gibt `CaptureAsync` mit `Region` das Bild eines Bereichs, mit welcher Verzögerung, auch für eine verdeckte Ansicht?
6. **Agent:** Laufen `NeoAutomation`-Werkzeuge (`take_snapshot`, `click`, `evaluate_script`, `take_screenshot`) auf der eingebetteten `WKWebView`, mit Filter, sodass `alta ui` und `codealta-dev` sie nicht sehen?
7. **Überlagerung auf macOS:** Wie viele der Fälle aus dem Windows-Branch treten hier auf (Menüs, Dialoge, Toasts, Popover)? Funktioniert das Standbild dort auch, und wie lange dauert es?
8. **Tastatur:** Bekommt die Ansicht die Tastatur beim Klick hinein, und die App sie zurück?

## Schritte

Wegwerf-Code ist erlaubt. Der Versuch läuft in einem eigenen Worktree auf `upstream/main`, nicht in diesem Feature-Branch; nichts davon wird gemergt. Zum Vergleich wird der Windows-Branch nur gelesen.

- [x] 0.1 Den Windows-Branch lesen: `DesktopBrowsers`, `browserPlacement.ts`, `nativeViewOcclusion.ts`, `src/prototypes/CodeAlta.Browser.Spike/Program.cs`, die „Spike results“ und „Facts found on the way“ seines Plans. Notieren, was plattformneutral ist und übernommen werden kann.
- [x] 0.2 Kleines Versuchsprogramm auf NeoAstra 1.12 (wie `CodeAlta.Browser.Spike`, ohne die App): ein Fenster mit Hauptansicht und zweiter `WKWebView`; Verschieben, Größe, Ausblenden über `setFrame:`/`setHidden:` am geborgten `WKWebView*`; Fenster verschieben, vergrößern, Vollbild, Retina. → Frage 1.
- [x] 0.3 Dasselbe als eigenes Fenster (`FillWindow`): Öffnen, Schließen, Wiederöffnen ohne Neuladen. → Frage 2.
- [x] 0.4 Zweite Umgebung bzw. eigenes Profil: Cookies und `localStorage` getrennt von der Hauptansicht; Navigation auf `app:` und `file:` abgelehnt. → Frage 3.
- [x] 0.5 `codealta-toolbar.js` per `AddScriptAsync` in eine isolierte Welt; Nachricht an CodeAlta; Gegenprobe: die Seite versucht, eine Anmerkung zu schicken. Aufruf der Leiste aus CodeAlta. → Frage 4.
- [x] 0.6 `CaptureAsync` mit `Region` (sichtbar, verdeckt, ausgeblendet); Dauer messen. → Fragen 5 und 7.
- [x] 0.7 Eine `NeoAutomation` mit Filter auf die zweite Ansicht: Snapshot, Klick, Skript, Bild; die Automation des Fensters sieht sie nicht. → Frage 6.
- [x] 0.8 In der App, minimal: ein Platzhalter im Bereich der Session, die Ansicht darüber, ein Menü, ein Dialog und ein Toast darüber; Standbild an Stelle der Ansicht. Tastatur hinein und zurück. → Fragen 7 und 8. **Erledigt am 2026-11-05** mit einem Wegwerf-Haken in der Developer-Instanz (Patch `.spike/CodeAlta.Browser.MacSpike/app-hook.patch`, danach aus dem App-Code entfernt; Log `app-hook.log` daneben).
- [x] 0.9 Ergebnisse unten eintragen, mit Empfehlung: eingebettet, eigenes Fenster, oder beides; und welche Teile des Windows-Branchs auf `main` übernommen werden können.

## Ergebnisse

Gemessen am 2026-11-05 mit `.spike/CodeAlta.Browser.MacSpike` (Worktree `glad-spica-mn9d`, nicht committet), NeoAstra 1.12.0, macOS 27.0.1 arm64, Retina (Faktor 2), fünf Läufe, dazu Schritt 0.8 in der Developer-Instanz. Logs und Bilder: `/tmp/macspike-run1` … `run5` (Lauf 2–4 bei gesperrtem Bildschirm, ohne Fensterbilder; Lauf 5 mit Bildern), App: `/tmp/dev-*.png`.

| Frage | Ergebnis |
| --- | --- |
| 1 Eingebettet: verschieben, ausblenden, Koordinaten | **Geht.** Zweite `WKWebView` auf derselben `NSWindow` (`FillWindow` + `FillParent=false`), über den geborgten `WKWebView*`: `setFrame:` ~0,01 ms, `setHidden:` < 1 ms (Seite meldet `visibilityState` `hidden`/`visible`). Sie liegt **über** der Hauptansicht (später hinzugefügt, kein Anheben nötig); der Treffertest gibt im Platzhalter die Browser-Ansicht. Koordinaten: Platzhalter-CSS × `pageZoom` (nicht × `devicePixelRatio`: AppKit rechnet in Punkten), dann `convertRect:toView:contentView` (Hauptansicht ist geflippt, Content-View nicht). Stimmt bei Zoom 1 und 1,25, Retina pixelgenau (Bild `run1/02-placed.png`). **Aufpassen:** NeoAstra ignoriert bei `FillWindow` `FillParent=false` und `Bounds` (Ansicht füllt das Fenster, Maske 18 = wächst mit); `Bounds` würde ohnehin von unten links zählen. Mit `autoresizingMask = NSViewMinYMargin` bleibt die Ansicht beim Vergrößern/Verkleinern oben links stehen, neu platzieren nur, wenn sich der Platzhalter ändert. NeoAstra zeigt eine verborgene Ansicht nach Fenster-Verbergen/Zeigen **nicht** wieder (anders als Windows mit FillWindow). Vollbild: im Versuch nicht wirksam geworden (Client-Größe unverändert) – offen. |
| 2 Eigenes Fenster | **Geht, ohne Neuladen.** Zweites `NeoWindow` mit `FillWindow`: Verbergen/Zeigen behält Seite und Eingaben. Zusätzlich: der `WKWebView` lässt sich zwischen Fenstern umhängen (`removeFromSuperview`/`addSubview:`), eingebettet ↔ eigenes Fenster, ohne Neuladen (Variable und getippter Text bleiben, CSS-Größe folgt). Schließt man das Fenster, während die Ansicht im Hauptfenster hängt, lebt sie weiter; `DisposeAsync` nimmt sie heraus. Kein Überlagerungsproblem, keine neue NeoAstra-Methode nötig. |
| 3 Isolierung auf macOS | **Zweite Umgebung ja, Profil nein.** `CreateEnvironmentAsync` mit eigenem `UserDataRoot` gibt einen eigenen `WKWebsiteDataStore` (ab macOS 14): Cookie und `localStorage` derselben Herkunft getrennt. Ein benanntes Profil auf der App-Umgebung (`ProfileNamed: None`, Aufruf gelingt trotzdem) **teilt** Cookies und Speicher mit der App **und** kann `app://codealta/…` öffnen (Schema-Handler der Umgebung) – also nicht verwenden. In der zweiten Umgebung laufen `app:` und `file:` ins Leere; die Regel `NavigationRequested` (nur http/https/about:blank) lehnt `app:` ab, `file:` kommt gar nicht erst als Anfrage. `fetch('app://…')` scheitert in beiden Varianten. Mit `BridgePolicy.Disabled` ist `window.webkit` in der Seite `undefined`. |
| 4 Leiste in isolierter Welt, nur sie kann melden, Aufrufe der Leiste | **Geht, aber an NeoAstra vorbei.** NeoAstra 1.12 kann auf macOS keine isolierte Welt (`ScriptIsolatedWorld: None`, `AddScriptAsync(IsolatedWorld)` → `NotSupportedException`; im Quellcode `cocoa_backend.mm` ausdrücklich abgelehnt). Über die Objective-C-Laufzeit (~120 Zeilen): `WKContentWorld worldWithName:"codealta-toolbar"`, `addScriptMessageHandler:contentWorld:name:` **nur in dieser Welt**, `WKUserScript …inContentWorld:` (Dokumentstart, nur Hauptframe) mit einer Zeile Vorspann `__codealtaToolbarMessage = p => webkit.messageHandlers.codealtaToolbar.postMessage(p)`; `codealta-toolbar.js` selbst **unverändert**. Ergebnis: `ready` bei jedem Dokument; echte Anmerkung per nativem Klick auf ein Element, Tippen und Cmd+Enter kommt als `annotation` an (`world=codealta-toolbar`). Die Seite sieht weder `__codealtaToolbar` noch das Binding noch `webkit.messageHandlers` (`webkit` ist `undefined`); ihr Versuch, eine Anmerkung zu schicken, wirft und kommt nie an. Aufrufe in der Welt der Leiste: `evaluateJavaScript:inFrame:inContentWorld:completionHandler:` – `enable`, `state`, `list`, `mode`, `marked`, `node`, `clear` antworten (im Versuch über den Kanal der Welt statt eines Completion-Blocks). Kein `ContextId` wie bei CDP: das Dokument wird über einen Zähler je Navigation erkannt. Ohne DevTools: Regeln nur per Skript (`#box @ inline`, Selektor + Datei, ohne Zeile; fremde Stylesheets nicht lesbar), keine Stacks der Erzeugung. |
| 5 Bild eines Bereichs, Dauer | **Geht, schnell, auch verborgen.** `CaptureAsync` mit `Region` (CSS-Pixel, NeoAstra rechnet Zoom um): 1–8 ms für einen Bereich, 7–19 ms für die ganze Ansicht (1000×800 Pixel). Funktioniert und ist **frisch** bei `setHidden`, verdeckt von der Hauptansicht, außerhalb des Fensters und bei minimiertem Fenster (Änderung der Seite im verborgenen Zustand ist im Bild). Bild der Anmerkung (Element + 16 px Rand) 6 ms. Ganze Seite nicht (`CaptureFullPage: None`). |
| 6 Werkzeuge des Agenten über `NeoAutomation` | **Geht.** Zwei Automationen mit `PageFilter` (`main` / `browser:`): jede sieht nur ihre Seite; `take_snapshot`, `click`, `evaluate_script`, `take_screenshot` auf der Browser-Ansicht; die Automation des Fensters sieht sie nicht. Auch bei **verborgener** Ansicht: Snapshot, Klick und Bildschirmfoto gehen (unter Windows braucht das Foto eine sichtbare Ansicht). Die Leiste (geschlossener Shadow Root, eigene Welt) erscheint nicht im Snapshot. Klicks der Automation sind `isTrusted=false`, native Klicks `true`. **In der App:** die Automation des Fensters hat heute keinen Filter – `codealta-dev` `list_pages` zeigte die Browser-Ansicht als zweite Seite („Spike page“). Der Filter `PageFilter` aus dem Windows-Branch ist Pflicht im selben Schritt. |
| 7 Überlagerung, Standbild | **Gelöst, zwei Wege.** Ohne Maßnahme liegt eine HTML-Überlagerung der App unter der nativen Ansicht (Versuch und App: Menü „Open sessions“ unsichtbar). Die Selektoren aus `nativeViewOcclusion.ts` erkennen in der App Menü (`.session-tab-popup`), Einstellungsdialog (`dialog[open]`) und Toast (`.bp6-toast`) sofort. **(a) Standbild:** Aufnahme + Bild in den Platzhalter + Ansicht verbergen in **16 ms** in der App (8 ms im Versuch; Windows ~100 ms) – im Moment des Öffnens, ohne Vorrat; Menü und Notizen-Knopf liegen sichtbar darüber. **(b) „Unterlage“ (nur macOS):** Browser-Ansicht **unter** der Hauptansicht, Hauptansicht ohne Hintergrund (`drawsBackground = NO`), Loch im Platzhalter, `hitTest:` der Hauptansicht gibt im Loch `nil`. Versuch: eine Überlagerung der Seite der App wird sichtbar **über** der Browser-Seite gezeichnet (Bild `run5/18`). App: Seite durch das Loch sichtbar, Klicks gehen durch die App in die Seite. Im Haken war das Loch ein `clip-path` auf `#root`; da Menüs und Dialoge der App in `#root` liegen (nur Toasts sind Portale außerhalb), schloss der Haken das Loch bei einer Überlagerung (Seite dann vom App-Hintergrund verdeckt, Dialog voll sichtbar). Im Produkt: Platzhalter und seine Vorfahren ohne Hintergrund statt `clip-path` – dann zeichnet WebKit **alle** Überlagerungen über der Seite, und das Signal steuert nur noch das Durchklicken. Eingriff: eine Methode `hitTest:` an NeoAstras Klasse `NeoAstraWebView` (Laufzeit) – im Produkt als NeoAstra-Funktion. |
| 8 Tastatur | **Geht, mit zwei Aufgaben für den Host.** Versuch und App (Modus plain und Unterlage): Klick in die Ansicht → First Responder ist die Browser-Ansicht, Tasten landen in der Seite; Klick zurück in die App → App hat die Tastatur. `makeFirstResponder:` gibt sie gezielt; bei aktiver App folgt `document.hasFocus()` (Lauf 5: Seite true / App false und umgekehrt). CodeAlta greift auf macOS nicht ein (`DesktopWindowFocus.Follow` wirkt nur unter Windows). (1) **Kürzel der App** (Cmd+Shift+P) nimmt die Seite (WebKit `performKeyEquivalent:`), die App sieht sie nicht – wie unter Windows; Weiterleiten ausgewählter Kürzel ist Arbeit des Hosts. (2) Wird die Ansicht mit der Tastatur verborgen, ist der First Responder danach das Fenster: der Host gibt sie der Hauptansicht zurück. Synthetische Ereignisse über `NSWindow sendEvent:` kommen nur an, wenn die App aktiv ist (für Tests: direkt an die getroffene Ansicht). |

### Empfehlung

**Weiter, beides – in dieser Reihenfolge: eigenes Fenster zuerst, eingebettet danach.**

- Das eigene Fenster ist klein, braucht keine neue NeoAstra-Methode und hat kein Überlagerungsproblem; auf macOS lässt sich dieselbe Ansicht später ohne Neuladen in den Bereich der Session umhängen. Damit ist der Wechsel zwischen beiden Varianten billig.
- Nach 0.8 bestätigt: eingebettet geht auch in der App – Platzierung über der Timeline, Erkennung von Menü, Dialog und Toast, Standbild in 16 ms, Tastatur hinein und zurück.
- Eingebettet ist auf macOS einfacher als befürchtet: Platzierung, Verbergen und Bilder sind schnell und zuverlässig, Bilder und Automation gehen auch bei verborgener Ansicht. Die „Unterlage“ könnte die Überlagerung auf macOS grundsätzlich lösen (nach Sichtprüfung).
- **Voraussetzung für die Leiste:** eine isolierte Welt mit eigenem Nachrichtenkanal und Auswerten in dieser Welt. Heute nur an NeoAstra vorbei über die Objective-C-Laufzeit; besser als NeoAstra-Funktion (WKWebView: `AddScriptAsync(IsolatedWorld)`, ein Nachrichten-Ereignis mit Welt, `EvaluateScriptAsync` in einer Welt). Zusammen mit `SetBounds`/`IsVisible`/`Focus` ein Vorschlag an NeoAstra – braucht die Zustimmung des Nutzers.
- Eine zweite Umsetzung von `IAnnotatablePage` für NeoAstra ist machbar: `StartToolbarAsync` (Welt + Kanal + Skript), `CallToolbarAsync` (Auswerten in der Welt, Dokumentzähler statt `ContextId`), `PictureAsync` (`CaptureAsync` mit Region), `InspectAsync` nur mit Skript-Hinweisen (Selektor + Datei von Stylesheets derselben Herkunft), `RecordNodeCreationAsync` leer.

### Was vom Windows-Branch übernommen werden kann

| Teil | Übernehmen? |
| --- | --- |
| `DesktopBrowsers`, `BrowserStore`/`pages.json`, `BrowserRpc`, `BrowserPane`, `browserHub`, Splitter, Wiederherstellen | ja, plattformneutral |
| `nativeViewOcclusion.ts` (Signal, Toasts ausweichen) | ja, unverändert; auch für die „Unterlage“ (als Schalter fürs Durchklicken) |
| `browserPlacement.ts` | ja, aber `deviceBounds` (× `devicePixelRatio`) ist Windows-Einheit: auf macOS CSS × Zoom in Punkten, umgerechnet im Host |
| `IBrowserViewPlacement` mit Windows-Umsetzung über COM | Schnittstelle ja; macOS-Umsetzung über `setFrame:`/`setHidden:`/`makeFirstResponder:` dazu |
| Isolierung (zweite Umgebung, keine Bridge, Navigationsregeln, Rechte, Downloads) | ja; auf macOS **nie** die Profil-Ausweichlösung |
| `DesktopBrowserTools`/`DesktopBrowserPlugin` (`browser_*`, Filter pro Session), `BrowserDialog` | ja; die Sonderbehandlung „Bildschirmfoto braucht sichtbare Ansicht“ entfällt auf macOS |
| `annotation-picker.js`, `BrowserAnnotations`, `AnnotationPopup` | nein: ersetzt durch `codealta-toolbar.js` und `IAnnotatablePage` des Features `glowing-larch` |
| `DesktopWindowFocus` (COM `MoveFocus`) | Idee ja (Tastatur der zuletzt fokussierten Ansicht), macOS über `makeFirstResponder:` |

### Offene Punkte

- Vollbild: `NeoWindow.EnterFullscreen` wurde im Versuch nicht wirksam (Client-Größe unverändert); `visibilityState` blieb nach Wiederherstellen aus dem Dock `hidden` (Lauf 1 und 5) – beides mit NeoAstra zu klären.
- Unterlage im Produkt: Layout so, dass Platzhalter und Vorfahren keinen Hintergrund haben; Sichtprüfung eines Toasts darüber in der App (im Versuch geprüft, in der App nur erkannt).
- Speicher pro Ansicht und Grenze lebender Ansichten auf macOS nicht gemessen.
- JavaScript-Dialoge der Seite (`alert`/`confirm`) mit aktiver Automation: wie unter Windows im Browser-Teil zeigen – nicht geprüft.
- Vorschlag an NeoAstra (isolierte Welt auf WKWebView mit Kanal und Auswerten pro Welt, `SetBounds`/`IsVisible`/`Focus`, `FillParent=false` bei `FillWindow` respektieren, Durchklicken für die Unterlage): Gespräch mit dem Nutzer steht aus.

## Entscheidung danach (nicht Teil des Versuchs)

**Wunsch des Nutzers (2026-11-05, nach dem Versuch):** Erst ausprobieren, wenn es vernünftig nutzbar ist. Der Browser soll sich wie die anderen Teile von CodeAlta verhalten: die Timeline teilen, rechts daneben der Browser, die Breite mit einem Splitter änderbar. Das ist die eingebettete Variante des Windows-Branchs (`BrowserPane` im Bereich der Session, Splitter, Breite pro Session), auf macOS mit der Platzierung aus diesem Versuch.

- **Weiter, eingebettet:** Windows-Branch auf `main` bringen (neu aufsetzen, nicht rebasen), macOS-Platzierung dazu, die Leiste dieses Features statt des alten Pickers, eine zweite Umsetzung von `IAnnotatablePage` für die NeoAstra-Ansicht (ohne CSS-Regeln und Stacks).
- **Weiter, eigenes Fenster:** dasselbe ohne Platzierung und Überlagerung, deutlich kleiner.
- **Nicht weiter:** die Seite bleibt im Chromium-Browser; der Versuch dokumentiert warum.
- Ein Vorschlag an NeoAstra für `SetBounds`, `IsVisible` und `Focus` einer Ansicht (wie im Windows-Branch notiert) ist ein Schritt nach außen und braucht die Zustimmung des Nutzers.

## Nicht dabei

- Linux (WebKitGTK kann eine nicht füllende Ansicht noch nicht platzieren), Windows (dort gibt es den Branch).
- Die fertige Funktion, Doku, Tests über den Versuch hinaus.
- Quellenhinweise aus einem DevTools-Protokoll auf macOS: WKWebView hat keins.

## Risiken

| Risiko | Umgang |
| --- | --- |
| `setFrame:`/`setHidden:` am geborgten `WKWebView*` an NeoAstra vorbei bricht dessen Annahmen (Layout, Fokus, Sichtbarkeit beim Zeigen des Fensters) | im Versuch gezielt Fenster zeigen/verbergen, minimieren, Vollbild; bei Problemen ist das eigene Fenster die Ausweichlösung |
| Die isolierte Welt kann keine Nachrichten an CodeAlta schicken, oder die Seite kann es auch | dann keine Leiste in der eingebetteten Ansicht, bis NeoAstra einen Kanal pro Welt hat; das wäre ein Ergebnis, kein Fehlschlag |
| Überlagerung ist auf macOS aufwendiger als auf Windows | Messen in 0.8; das eigene Fenster hat das Problem nicht |
