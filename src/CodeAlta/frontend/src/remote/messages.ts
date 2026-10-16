// English source keys; translations in es, fr, de, ja, zh-CN order.
export const remoteMessages = {
  // The tag of a remote window beside the mark of the application, and what it tells on hover.
  "Remote window of {host}": ["Ventana remota de {host}", "Fenêtre distante de {host}", "Remote-Fenster von {host}", "{host} のリモートウィンドウ", "{host} 的远程窗口"],
  // What a remote window cannot do: it acts on the computer that runs CodeAlta, not on this one.
  "This window is remote: exit CodeAlta from its own window on {host}.": [
    "Esta ventana es remota: salga de CodeAlta desde su propia ventana en {host}.",
    "Cette fenêtre est distante : quittez CodeAlta depuis sa propre fenêtre sur {host}.",
    "Dieses Fenster ist remote: Beende CodeAlta in seinem eigenen Fenster auf {host}.",
    "このウィンドウはリモートです。{host} の CodeAlta のウィンドウから終了してください。",
    "此窗口是远程窗口：请在 {host} 上 CodeAlta 自己的窗口中退出。"],
  "This window is remote: the editor and the file manager would open on the computer that runs CodeAlta, not here.": [
    "Esta ventana es remota: el editor y el administrador de archivos se abrirían en el equipo que ejecuta CodeAlta, no aquí.",
    "Cette fenêtre est distante : l’éditeur et le gestionnaire de fichiers s’ouvriraient sur l’ordinateur qui exécute CodeAlta, pas ici.",
    "Dieses Fenster ist remote: Editor und Dateimanager würden auf dem Rechner öffnen, auf dem CodeAlta läuft, nicht hier.",
    "このウィンドウはリモートです。エディターとファイルマネージャーは、ここではなく CodeAlta を実行しているコンピューターで開きます。",
    "此窗口是远程窗口：编辑器和文件管理器会在运行 CodeAlta 的电脑上打开，而不是这里。"],
} as const;
