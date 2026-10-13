// The picker of annotations, in a page of the browser of a session. The host adds it to every document of a
// browser view, at its start. It does nothing until the host turns pick mode on: then it outlines the element
// under the pointer, and a click, which the page does not get, keeps a description of that element for the
// host to read. It also draws the numbered marks of the annotations the host names, and scrolls to one.
//
// The view has no bridge: the host calls the functions below with a script and reads what they return, a JSON
// text each. Everything a function returns is text of the page and is bounded here; the host bounds it again,
// since a page can replace this object.
(() => {
  "use strict";
  // The frames of a page get this script too: an element is picked in the top document only.
  if (window.__codealtaAnnotations || window.top !== window) return;

  const limits = { selector: 1000, ancestors: 12, ancestor: 160, name: 300, text: 500, html: 1000, short: 64, address: 2048, title: 512 };
  // The attributes that name an element for good, in the order they are tried.
  const stable = ["data-testid", "data-test-id", "data-test", "data-cy", "data-qa", "name", "aria-label"];
  // How long the events of the click that picked are still kept from the page, in milliseconds.
  const afterPick = 400;

  let state = "off";
  let picked = null, pickedElement = null;
  let swallowUntil = 0;
  let host = null, root = null, outline = null, label = null, cursor = null;
  let marks = [];
  let frame = 0;

  const shorten = (value, maximum) => {
    const text = String(value ?? "").replace(/\s+/g, " ").trim();
    return text.length <= maximum ? text : text.slice(0, maximum - 1) + "…";
  };
  const escape = value => window.CSS && CSS.escape ? CSS.escape(value) : String(value).replace(/[^a-zA-Z0-9_-]/g, character => "\\" + character);
  const quote = value => '"' + String(value).replace(/\\/g, "\\\\").replace(/"/g, '\\"').replace(/\n/g, "\\a ") + '"';
  const unique = selector => { try { return document.querySelectorAll(selector).length === 1; } catch { return false; } };
  const tagOf = element => element.localName || String(element.tagName || "").toLowerCase();

  // What names an element by itself, when exactly one element of the document has it: its id, or a stable attribute.
  function ownSelector(element) {
    const id = element.getAttribute("id");
    if (id && id.length <= 200 && unique("#" + escape(id))) return "#" + escape(id);
    for (const attribute of stable) {
      const value = element.getAttribute(attribute);
      if (!value || value.length > 200) continue;
      const selector = `${tagOf(element)}[${attribute}=${quote(value)}]`;
      if (unique(selector)) return selector;
    }
    return null;
  }

  // A CSS selector that finds the element: what names it by itself, or the path from the nearest ancestor that
  // has such a name, by the place of each element among the elements of its kind.
  function selectorOf(element) {
    const parts = [];
    for (let current = element; current && current.nodeType === 1; current = current.parentElement) {
      const own = ownSelector(current);
      if (own) { parts.unshift(own); break; }
      const tag = tagOf(current);
      if (current === document.documentElement || !current.parentElement) { parts.unshift(tag); break; }
      let place = 1, alone = true;
      for (let sibling = current.previousElementSibling; sibling; sibling = sibling.previousElementSibling) if (tagOf(sibling) === tag) { place++; alone = false; }
      for (let sibling = current.nextElementSibling; alone && sibling; sibling = sibling.nextElementSibling) if (tagOf(sibling) === tag) alone = false;
      parts.unshift(alone ? escape(tag) : `${escape(tag)}:nth-of-type(${place})`);
    }
    return parts.join(" > ").slice(0, limits.selector);
  }

  // An element in a few words: its tag, its id and its classes.
  function brief(element) {
    let text = tagOf(element);
    const id = element.getAttribute("id");
    if (id) text += "#" + id;
    const classes = typeof element.className === "string" ? element.className.trim().split(/\s+/).filter(Boolean) : [];
    for (const name of classes.slice(0, 4)) text += "." + name;
    return shorten(text, limits.ancestor);
  }

  // The ancestors of an element, from the outermost that is kept to its parent.
  function ancestorsOf(element) {
    const chain = [];
    for (let current = element.parentElement; current && chain.length < limits.ancestors; current = current.parentElement) chain.unshift(brief(current));
    return chain;
  }

  function roleOf(element) {
    const role = element.getAttribute("role");
    if (role) return shorten(role, limits.short);
    const tag = tagOf(element), type = (element.getAttribute("type") || "").toLowerCase();
    if (tag === "a" && element.hasAttribute("href")) return "link";
    if (tag === "button" || tag === "summary") return "button";
    if (tag === "select") return "combobox";
    if (tag === "textarea") return "textbox";
    if (tag === "img") return "img";
    if (/^h[1-6]$/.test(tag)) return "heading";
    if (tag === "input") return type === "checkbox" ? "checkbox" : type === "radio" ? "radio" : type === "button" || type === "submit" || type === "reset" ? "button" : type === "range" ? "slider" : "textbox";
    return "";
  }

  // The name a reader of the page hears for the element, as far as a few rules tell it.
  function nameOf(element) {
    const labelled = element.getAttribute("aria-labelledby");
    if (labelled) {
      const text = labelled.split(/\s+/).map(id => document.getElementById(id)?.textContent ?? "").join(" ");
      if (text.trim()) return shorten(text, limits.name);
    }
    for (const attribute of ["aria-label", "alt", "title", "placeholder"]) {
      const value = element.getAttribute(attribute);
      if (value && value.trim()) return shorten(value, limits.name);
    }
    if (element.labels && element.labels.length > 0) return shorten(element.labels[0].textContent, limits.name);
    const tag = tagOf(element);
    if (tag === "input" && /^(button|submit|reset)$/i.test(element.getAttribute("type") || "")) return shorten(element.value, limits.name);
    if (tag === "button" || tag === "a" || tag === "summary" || /^h[1-6]$/.test(tag) || element.getAttribute("role")) return shorten(element.textContent, limits.name);
    return "";
  }

  // The opening tag of an element, with its attributes shortened.
  function openingTag(element) {
    let text = "<" + tagOf(element);
    for (const attribute of Array.from(element.attributes || []).slice(0, 24)) {
      text += ` ${attribute.name}="${shorten(attribute.value, 120).replace(/"/g, "&quot;")}"`;
      if (text.length > limits.html) break;
    }
    return (text + ">").slice(0, limits.html);
  }

  function rectOf(element) {
    const box = element.getBoundingClientRect();
    return { x: Math.round(box.left), y: Math.round(box.top), width: Math.round(box.width), height: Math.round(box.height) };
  }

  /** What the host keeps of an element. */
  function describe(element) {
    return {
      selector: selectorOf(element),
      ancestors: ancestorsOf(element),
      tag: shorten(tagOf(element), limits.short),
      role: roleOf(element),
      name: nameOf(element),
      text: shorten(element.innerText ?? element.textContent, limits.text),
      html: openingTag(element),
      rect: rectOf(element),
      viewport: { width: window.innerWidth, height: window.innerHeight },
      address: String(location.href).slice(0, limits.address),
      title: shorten(document.title, limits.title),
    };
  }

  // What this script draws is in the shadow tree of one element: the styles of the page do not reach it, and
  // nothing in it takes the pointer.
  function layer() {
    if (host && host.isConnected) return root;
    host = document.createElement("codealta-annotations");
    host.style.cssText = "all: initial; position: fixed; inset: 0; z-index: 2147483647; pointer-events: none;";
    root = host.attachShadow({ mode: "open" });
    const style = document.createElement("style");
    style.textContent = `
      .outline { position: fixed; box-sizing: border-box; border: 2px solid #2d72d2; background: rgba(45, 114, 210, 0.12); border-radius: 2px; }
      .label { position: fixed; max-width: 60vw; padding: 1px 6px; border-radius: 3px; color: #fff; background: #2d72d2; font: 11px/16px system-ui, sans-serif; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .mark { position: fixed; min-width: 18px; height: 18px; box-sizing: border-box; padding: 0 5px; border: 1.5px solid #fff; border-radius: 9px; color: #fff; background: #c87619; font: 600 11px/15px system-ui, sans-serif; text-align: center; box-shadow: 0 1px 3px rgba(0, 0, 0, 0.4); }
      .ring { position: fixed; box-sizing: border-box; border: 2px solid #c87619; border-radius: 3px; transition: opacity 0.6s; }`;
    root.append(style);
    (document.documentElement || document).append(host);
    return root;
  }

  function showOutline(element) {
    const tree = layer();
    if (!outline) { outline = document.createElement("div"); outline.className = "outline"; label = document.createElement("div"); label.className = "label"; }
    if (!outline.isConnected) tree.append(outline, label);
    const box = element.getBoundingClientRect();
    outline.style.cssText = `left:${box.left}px;top:${box.top}px;width:${box.width}px;height:${box.height}px`;
    label.textContent = brief(element);
    label.style.cssText = `left:${Math.max(0, box.left)}px;top:${box.top >= 20 ? box.top - 20 : Math.min(window.innerHeight - 18, box.bottom + 2)}px`;
  }

  function hideOutline() { outline?.remove(); label?.remove(); }

  function setCursor(on) {
    if (on && !cursor) {
      cursor = document.createElement("style");
      cursor.textContent = "* { cursor: crosshair !important; }";
      (document.head || document.documentElement).append(cursor);
    } else if (!on && cursor) { cursor.remove(); cursor = null; }
  }

  const own = element => !!host && (element === host || host.contains(element));
  const target = event => event.target && event.target.nodeType === 1 && !own(event.target) ? event.target : null;

  function finish(next) {
    state = next;
    hideOutline();
    setCursor(false);
    swallowUntil = performance.now() + afterPick;
  }

  // Every event of the pointer goes through here first: this script is there before the page. While an element
  // is picked, and for the rest of the click that picked one, the page gets none of them.
  function pointer(event) {
    const picking = state === "picking";
    if (!picking && performance.now() >= swallowUntil) return;
    if (event.type === "pointermove" || event.type === "mousemove" || event.type === "pointerover" || event.type === "mouseover") {
      if (picking) { const element = target(event); if (element) showOutline(element); }
      return;
    }
    event.preventDefault();
    event.stopImmediatePropagation();
    // The click picks; so does the release of the pointer, for an element that gets no click (a disabled button).
    if (!picking || (event.type !== "click" && event.type !== "pointerup") || event.button > 0) return;
    const element = target(event);
    if (!element) return;
    pickedElement = element;
    picked = describe(element);
    finish("picked");
  }

  function keyboard(event) {
    if (state !== "picking") return;
    event.preventDefault();
    event.stopImmediatePropagation();
    if (event.type === "keydown" && event.key === "Escape") finish("cancelled");
  }

  for (const type of ["pointermove", "mousemove", "pointerdown", "pointerup", "mousedown", "mouseup", "click", "dblclick", "auxclick", "contextmenu", "touchstart", "touchend", "dragstart"])
    window.addEventListener(type, pointer, { capture: true, passive: false });
  for (const type of ["keydown", "keyup", "keypress"]) window.addEventListener(type, keyboard, true);

  function find(selector) {
    try { return typeof selector === "string" && selector ? document.querySelector(selector) : null; } catch { return null; }
  }

  function placeMarks() {
    frame = 0;
    for (const mark of marks) {
      const element = mark.element.isConnected ? mark.element : null;
      const box = element?.getBoundingClientRect();
      const seen = !!box && (box.width > 0 || box.height > 0);
      mark.badge.style.display = seen ? "" : "none";
      if (seen) mark.badge.style.cssText = `left:${Math.max(0, box.left - 9)}px;top:${Math.max(0, box.top - 9)}px`;
    }
  }
  const moved = () => { if (marks.length > 0 && !frame) frame = requestAnimationFrame(placeMarks); };
  window.addEventListener("scroll", moved, { capture: true, passive: true });
  window.addEventListener("resize", moved, { passive: true });

  const api = {
    /** Turns pick mode on or off. On forgets what was picked before. */
    pick(on) {
      if (on) { picked = null; pickedElement = null; state = "picking"; setCursor(true); }
      else if (state === "picking") finish("off");
      else state = "off";
      return JSON.stringify({ state });
    },
    /**
     * Where pick mode is: "off", "picking", "picked" with the element that was picked, or "cancelled" when
     * Escape ended it. The rectangle of a picked element is where it is now.
     */
    poll() {
      if (state !== "picked" || !picked) return JSON.stringify({ state });
      if (pickedElement && pickedElement.isConnected) { picked.rect = rectOf(pickedElement); picked.viewport = { width: window.innerWidth, height: window.innerHeight }; }
      return JSON.stringify({ state, element: picked });
    },
    /** Describes the element a selector finds, or returns "null". For the tests of this script. */
    describe(selector) {
      const element = find(selector);
      return JSON.stringify(element ? describe(element) : null);
    },
    /**
     * Draws a numbered mark at each element the selectors find, in the place of the marks drawn before, and
     * says which numbers were found. `list` holds `{ number, selector }`.
     */
    mark(list) {
      for (const mark of marks) mark.badge.remove();
      marks = [];
      const found = [];
      for (const entry of Array.isArray(list) ? list.slice(0, 200) : []) {
        const element = find(entry?.selector);
        if (!element || own(element)) continue;
        const badge = document.createElement("div");
        badge.className = "mark";
        badge.textContent = String(entry.number);
        layer().append(badge);
        marks.push({ element, badge });
        found.push(entry.number);
      }
      placeMarks();
      return JSON.stringify({ found, address: String(location.href).slice(0, limits.address) });
    },
    /** Scrolls to the element a selector finds and shows a ring around it for a moment. Says whether it was found. */
    reveal(selector) {
      const element = find(selector);
      if (!element) return JSON.stringify({ found: false });
      element.scrollIntoView({ block: "center", inline: "nearest" });
      const ring = document.createElement("div");
      ring.className = "ring";
      const box = element.getBoundingClientRect();
      ring.style.cssText = `left:${box.left - 4}px;top:${box.top - 4}px;width:${box.width + 8}px;height:${box.height + 8}px`;
      layer().append(ring);
      setTimeout(() => { ring.style.opacity = "0"; }, 1200);
      setTimeout(() => ring.remove(), 1900);
      moved();
      return JSON.stringify({ found: true });
    },
  };
  Object.defineProperty(window, "__codealtaAnnotations", { value: Object.freeze(api), enumerable: false });
})();
