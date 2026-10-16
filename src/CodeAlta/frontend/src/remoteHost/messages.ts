// English source keys; translations in es, fr, de, ja, zh-CN order.
export const remoteHostMessages = {
  // The page and its place in Settings.
  "Remote access": ["Acceso remoto", "Accès à distance", "Fernzugriff", "リモートアクセス", "远程访问"],
  "The page of CodeAlta in a browser on another computer, with everything this window does.": [
    "La página de CodeAlta en un navegador de otro equipo, con todo lo que hace esta ventana.",
    "La page de CodeAlta dans un navigateur sur un autre ordinateur, avec tout ce que fait cette fenêtre.",
    "Die Seite von CodeAlta in einem Browser auf einem anderen Rechner, mit allem, was dieses Fenster kann.",
    "別のコンピューターのブラウザーで開く CodeAlta のページ。このウィンドウでできることがすべて使えます。",
    "在另一台电脑的浏览器中打开 CodeAlta 页面，拥有此窗口的全部功能。"],
  "Remote access unavailable": ["Acceso remoto no disponible", "Accès à distance indisponible", "Fernzugriff nicht verfügbar", "リモートアクセスは利用できません", "远程访问不可用"],
  // The switch, and what it means while there is no login.
  "Allow other computers to use this CodeAlta": ["Permitir que otros equipos usen este CodeAlta", "Autoriser d’autres ordinateurs à utiliser ce CodeAlta",
    "Anderen Rechnern erlauben, dieses CodeAlta zu benutzen", "他のコンピューターからこの CodeAlta を使えるようにする", "允许其他电脑使用此 CodeAlta"],
  "There is no login yet: whoever reaches this address has your control of CodeAlta. Use a private network such as Tailscale, and turn it off when not needed.": [
    "Todavía no hay inicio de sesión: quien alcance esta dirección tiene su control de CodeAlta. Use una red privada como Tailscale y desactívelo cuando no lo necesite.",
    "Il n’y a pas encore de connexion : quiconque atteint cette adresse a votre contrôle de CodeAlta. Utilisez un réseau privé tel que Tailscale, et désactivez-le quand il n’est pas nécessaire.",
    "Es gibt noch keine Anmeldung: Wer diese Adresse erreicht, hat deine Kontrolle über CodeAlta. Nutze ein privates Netz wie Tailscale und schalte es aus, wenn du es nicht brauchst.",
    "まだログインはありません。このアドレスに到達できる人は、あなたと同じように CodeAlta を操作できます。Tailscale のようなプライベートネットワークを使い、不要なときはオフにしてください。",
    "目前还没有登录机制：能访问此地址的人就拥有你对 CodeAlta 的控制权。请使用 Tailscale 等私有网络，不需要时请关闭。"],
  // Where the server listens.
  "Listen on": ["Escuchar en", "Écouter sur", "Lauschen auf", "待ち受けアドレス", "监听地址"],
  "This computer only": ["Solo este equipo", "Cet ordinateur seulement", "Nur dieser Rechner", "このコンピューターのみ", "仅此电脑"],
  "All networks": ["Todas las redes", "Tous les réseaux", "Alle Netze", "すべてのネットワーク", "所有网络"],
  "Other computers on that network can reach this CodeAlta.": ["Otros equipos de esa red pueden alcanzar este CodeAlta.", "D’autres ordinateurs de ce réseau peuvent atteindre ce CodeAlta.",
    "Andere Rechner in diesem Netz können dieses CodeAlta erreichen.", "そのネットワーク上の他のコンピューターからこの CodeAlta に到達できます。", "该网络上的其他电脑可以访问此 CodeAlta。"],
  "The remote server is not listening.": ["El servidor remoto no está escuchando.", "Le serveur distant n’écoute pas.", "Der Remote-Server lauscht nicht.", "リモートサーバーは待ち受けていません。", "远程服务器未在监听。"],
  // What a browser needs.
  "Open on another device": ["Abrir en otro dispositivo", "Ouvrir sur un autre appareil", "Auf einem anderen Gerät öffnen", "別のデバイスで開く", "在其他设备上打开"],
  "Connected windows: {count}": ["Ventanas conectadas: {count}", "Fenêtres connectées : {count}", "Verbundene Fenster: {count}", "接続中のウィンドウ: {count}", "已连接的窗口：{count}"],
} as const;
