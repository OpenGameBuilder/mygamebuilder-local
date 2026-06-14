using System.Text.Encodings.Web;

namespace MyGameBuilder.Local.Api.Updates;

public static class UpdatePageRenderer
{
    public static string BuildUpdatePage(string csrfToken) =>
        UpdatePageTemplate
            .Replace("__MGB_COMMON_CSS__", CommonCss, StringComparison.Ordinal)
            .Replace("__CSRF_TOKEN__", JavaScriptEncoder.Default.Encode(csrfToken), StringComparison.Ordinal);

    public static string BuildMissingFrontendArchivePage(string archivePath) =>
        MissingFrontendArchiveTemplate
            .Replace("__MGB_COMMON_CSS__", CommonCss, StringComparison.Ordinal)
            .Replace("__FRONTEND_ARCHIVE_PATH__", HtmlEncoder.Default.Encode(archivePath), StringComparison.Ordinal);

    private const string CommonCss =
        """
            @font-face {
              font-family: "mgb-abscissa";
              src: url("/_updates/flash-assets/abscissa.ttf") format("truetype");
              font-style: normal;
              font-weight: 400;
            }
            @font-face {
              font-family: "mgb-abscissa";
              src: url("/_updates/flash-assets/abscissa-bold.ttf") format("truetype");
              font-style: normal;
              font-weight: 700;
            }
            @font-face {
              font-family: "mgb-title";
              src: url("/_updates/flash-assets/titlefont.ttf") format("truetype");
              font-style: normal;
              font-weight: 700;
            }
            :root {
              color-scheme: light;
              --stage: #8fa6ad;
              --stage-dark: #6f868e;
              --ink: #101010;
              --muted: #4b5559;
              --panel: #f7fbfd;
              --panel-mid: #d8e4e9;
              --chrome: #c0c0c0;
              --line: #5f6d72;
              --red: #ff0000;
              --blue: #0000a0;
              --link: #0000ff;
              --green: #009000;
              --home-cyan: #00bbbb;
              --warning: #ffff00;
              --purple: #8000ff;
            }
            * { box-sizing: border-box; }
            html, body { min-height: 100%; }
            body {
              margin: 0;
              background:
                linear-gradient(180deg, rgba(255,255,255,.24), rgba(0,0,0,.04) 145px, rgba(0,0,0,.12)),
                var(--stage);
              color: var(--ink);
              font: 13px/1.35 "mgb-abscissa", "Trebuchet MS", Verdana, Arial, sans-serif;
              font-synthesis: none;
              letter-spacing: 0;
            }
            body[data-stage="cyan"] { --stage: #00bbbb; --stage-dark: #008d91; }
            button, a.button {
              min-height: 24px;
              border: 1px solid #555;
              border-radius: 4px;
              background:
                linear-gradient(180deg, rgba(255,255,255,.96) 0 48%, rgba(226,226,226,.95) 49% 100%);
              box-shadow: inset 0 1px 0 rgba(255,255,255,.9), 1px 1px 0 rgba(0,0,0,.18);
              color: #111;
              cursor: pointer;
              align-items: center;
              display: inline-flex;
              gap: 5px;
              justify-content: center;
              font: 700 12px/1 "mgb-abscissa", "Trebuchet MS", Verdana, Arial, sans-serif;
              padding: 4px 12px;
              text-align: center;
              text-decoration: none;
            }
            button:hover, a.button:hover {
              background:
                linear-gradient(180deg, #ffffff 0 48%, #eeeeee 49% 100%);
              border-color: var(--blue);
            }
            button:active, a.button:active {
              transform: translate(1px, 1px);
              box-shadow: inset 0 1px 2px rgba(0,0,0,.25);
            }
            button.primary {
              background:
                linear-gradient(180deg, #38d238 0 48%, var(--green) 49% 100%);
              border-color: #004600;
              color: #fff;
              text-shadow: 0 1px 0 #004600;
            }
            button:disabled {
              background: linear-gradient(180deg, #eeeeee, #cfcfcf);
              color: #777;
              cursor: default;
              opacity: .72;
              text-shadow: none;
              transform: none;
            }
            button:disabled img {
              opacity: .58;
            }
            p { margin: 0; }
            .stage-shell {
              width: min(980px, calc(100vw - 24px));
              min-height: min(680px, calc(100vh - 24px));
              margin: 12px auto;
              position: relative;
            }
            .mgb-header {
              min-height: 52px;
              padding: 6px 5px 12px;
              position: relative;
            }
            .brand-row {
              align-items: center;
              display: flex;
              gap: 9px;
              min-height: 30px;
              overflow: hidden;
              white-space: nowrap;
            }
            .mgb-wordmark {
              color: #111;
              font: 700 21px/1 "mgb-title", "Trebuchet MS", Verdana, Arial, sans-serif;
              text-shadow: 0 1px 0 rgba(255,255,255,.65);
            }
            .mgb-logo {
              flex: 0 0 auto;
              height: 14px;
              image-rendering: pixelated;
              width: 35px;
            }
            .mgb-tagline {
              font-size: 12px;
              padding-top: 4px;
            }
            .red-rule {
              align-self: stretch;
              background: var(--red);
              border-left: 1px solid rgba(255,255,255,.45);
              border-right: 1px solid rgba(0,0,0,.35);
              display: inline-block;
              min-height: 24px;
              width: 3px;
            }
            .client-area {
              background:
                linear-gradient(180deg, rgba(255,255,255,.16), rgba(255,255,255,.04)),
                var(--stage);
              border: 1px solid rgba(0,0,0,.45);
              box-shadow: inset 0 1px 0 rgba(255,255,255,.42), 4px 4px 12px rgba(0,0,0,.25);
              min-height: 560px;
              padding: 15px;
            }
            .window {
              background: var(--panel);
              border: 1px solid #4f5b60;
              border-radius: 16px;
              box-shadow: 3px 3px 7px rgba(0,0,0,.36);
              max-width: 100%;
              min-width: 0;
              overflow: hidden;
            }
            .window-title {
              align-items: center;
              background:
                linear-gradient(180deg, rgba(255,255,255,.96), rgba(192,192,192,.98));
              border-bottom: 1px solid #67767c;
              display: flex;
              gap: 12px;
              justify-content: space-between;
              min-height: 33px;
              padding: 4px 10px 4px 14px;
            }
            h1 {
              color: #111;
              font: 700 20px/1 "mgb-title", "Trebuchet MS", Verdana, Arial, sans-serif;
              margin: 0;
              max-width: 100%;
              min-width: 0;
              overflow-wrap: anywhere;
            }
            .button-icon,
            .panel-icon {
              image-rendering: pixelated;
              object-fit: contain;
            }
            .button-icon {
              height: 16px;
              width: 16px;
            }
            .panel-icon {
              flex: 0 0 auto;
              height: 15px;
              width: 15px;
            }
            .build-link {
              color: #000080;
              font-size: 12px;
              text-decoration: underline;
              white-space: nowrap;
            }
            .window-body {
              background: rgba(255,255,255,.72);
              min-width: 0;
              padding: 13px;
            }
            .tutorial-clue {
              background: var(--warning);
              border: 1px solid var(--blue);
              border-radius: 8px;
              box-shadow: 0 0 13px rgba(255,255,255,.7);
              color: #000;
              min-height: 38px;
              padding: 8px 11px;
            }
            .update-layout {
              display: grid;
              gap: 12px;
              grid-template-columns: minmax(0, 1fr) 262px;
              margin-top: 12px;
              min-width: 0;
            }
            .target-stack {
              display: grid;
              gap: 10px;
              min-width: 0;
            }
            .target-panel {
              background: #fff;
              border: 1px solid #6c7a80;
              border-radius: 8px;
              box-shadow: 2px 2px 4px rgba(0,0,0,.18);
              min-width: 0;
              overflow: hidden;
            }
            .target-panel.setup {
              border-color: #004600;
              box-shadow: 0 0 0 2px rgba(0,144,0,.25), 2px 2px 4px rgba(0,0,0,.18);
            }
            .panel-heading {
              align-items: center;
              background:
                linear-gradient(180deg, #fbfbfb 0%, #d3dce1 52%, #c0c0c0 100%);
              border-bottom: 1px solid #78878e;
              display: flex;
              gap: 8px;
              min-height: 30px;
              padding: 4px 9px;
            }
            .panel-heading h2 {
              font-size: 14px;
              margin: 0;
            }
            .status-lamp {
              background: radial-gradient(circle at 35% 35%, #ffffff, #00a000 35%, #005000 100%);
              border: 1px solid #004600;
              border-radius: 50%;
              box-shadow: inset 0 1px 1px rgba(255,255,255,.8);
              flex: 0 0 13px;
              height: 13px;
              width: 13px;
            }
            .target-panel[data-state="working"] .status-lamp {
              background: radial-gradient(circle at 35% 35%, #ffffff, #ffff00 35%, #b4a000 100%);
              border-color: #837700;
            }
            .target-panel[data-state="error"] .status-lamp {
              background: radial-gradient(circle at 35% 35%, #ffffff, #ff5555 35%, #a00000 100%);
              border-color: #830000;
            }
            .target-panel[data-missing="true"] .status-lamp {
              background: radial-gradient(circle at 35% 35%, #ffffff, #8000ff 35%, #3d0078 100%);
              border-color: #3d0078;
            }
            .panel-body {
              display: grid;
              gap: 9px;
              padding: 10px;
            }
            .message {
              color: #2a3337;
              min-height: 19px;
            }
            .message.warning { color: #7a5800; }
            .message.current {
              color: var(--green);
              font-weight: bold;
            }
            .message.error {
              color: #b00000;
              font-weight: bold;
            }
            .meta {
              border: 1px solid #97a5ab;
              display: grid;
              font-size: 12px;
            }
            .meta div {
              display: grid;
              grid-template-columns: 112px minmax(0, 1fr);
              min-height: 24px;
            }
            .meta div + div { border-top: 1px solid #d0d8dc; }
            .meta span {
              min-width: 0;
              overflow-wrap: anywhere;
              padding: 4px 6px;
            }
            .meta span:first-child {
              background:
                linear-gradient(180deg, #f7f7f7, #d4dde2);
              border-right: 1px solid #a9b4ba;
              color: #333;
              font-weight: bold;
            }
            .meta span:last-child {
              background: #fff;
              color: #000;
            }
            .bar {
              background: linear-gradient(180deg, #aeb8bd, #d9e0e4);
              border: 1px solid #5b686e;
              border-radius: 4px;
              box-shadow: inset 0 1px 2px rgba(0,0,0,.25);
              height: 15px;
              overflow: hidden;
            }
            .bar > div {
              background:
                repeating-linear-gradient(45deg, rgba(255,255,255,.28) 0 6px, rgba(255,255,255,0) 6px 12px),
                linear-gradient(180deg, #2be52b, var(--green));
              height: 100%;
              transition: width .2s ease;
              width: 0;
            }
            .actions {
              display: flex;
              flex-wrap: wrap;
              gap: 7px;
              justify-content: flex-end;
              min-width: 0;
            }
            .side-panel {
              align-self: start;
              background: #fff;
              border: 1px solid #65737a;
              border-radius: 8px;
              box-shadow: 2px 2px 4px rgba(0,0,0,.18);
              overflow: hidden;
            }
            .side-heading {
              background:
                linear-gradient(180deg, #fbfbfb 0%, #d3dce1 52%, #c0c0c0 100%);
              border-bottom: 1px solid #78878e;
              font-weight: bold;
              min-height: 29px;
              padding: 6px 9px;
            }
            .event-grid {
              border-bottom: 1px solid #d0d8dc;
              min-height: 120px;
            }
            .event-row {
              display: grid;
              font-size: 12px;
              grid-template-columns: 74px minmax(0, 1fr);
            }
            .event-row + .event-row { border-top: 1px solid #d0d8dc; }
            .event-row span {
              min-width: 0;
              overflow-wrap: anywhere;
              padding: 5px 6px;
            }
            .event-row span:first-child {
              background: #eef3f6;
              border-right: 1px solid #d0d8dc;
              font-weight: bold;
            }
            .side-note {
              background: #f5f9fb;
              color: var(--muted);
              font-size: 12px;
              padding: 8px;
            }
            .setup-window {
              max-width: 760px;
              margin: 44px auto 0;
            }
            .setup-body {
              background: #fff;
              display: grid;
              gap: 12px;
              padding: 16px;
            }
            .missing {
              background: var(--warning);
              border: 1px solid var(--blue);
              border-radius: 8px;
              color: #000;
              font-weight: bold;
              padding: 9px 11px;
            }
            code {
              background: #e7edf0;
              border: 1px solid #a8b6bd;
              border-radius: 3px;
              color: #000;
              padding: 1px 4px;
            }
            a {
              color: var(--link);
              font-weight: bold;
            }
            .card h2 { font-size: 17px; margin: 0; letter-spacing: 0; }
            .meta { display: grid; gap: 6px; color: var(--muted); }
            .meta div { display: flex; justify-content: space-between; gap: 14px; border-bottom: 1px solid rgba(255,255,255,.06); padding-bottom: 5px; }
            .meta span:last-child { color: var(--text); text-align: right; overflow-wrap: anywhere; }
            .actions { margin-top: auto; display: flex; gap: 8px; flex-wrap: wrap; }
            .message { color: var(--muted); min-height: 22px; }
            .warning { color: var(--warn); }
            .error { color: var(--danger); }
            .bar { height: 8px; background: #11151a; border-radius: 99px; overflow: hidden; border: 1px solid var(--line); }
            .bar > div { height: 100%; background: var(--accent); width: 0; transition: width .2s ease; }
            .setup { border-color: rgba(240,198,106,.65); }
            @media (max-width: 860px) {
              body {
                overflow-x: hidden;
              }
              .stage-shell {
                overflow-x: hidden;
                width: min(100vw - 12px, 760px);
                margin: 6px auto;
              }
              .mgb-header {
                padding-right: 6px;
              }
              .brand-row {
                align-items: flex-start;
                flex-direction: column;
                gap: 3px;
                white-space: normal;
              }
              .red-rule {
                display: none;
              }
              .client-area {
                padding: 9px;
              }
              .window-title {
                align-items: flex-start;
                flex-direction: column;
              }
              h1 {
                font-size: 16px;
                line-height: 1.15;
              }
              .update-layout {
                grid-template-columns: 1fr;
              }
              .actions {
                justify-content: flex-start;
              }
              button,
              a.button {
                max-width: 100%;
                white-space: normal;
              }
              .meta div,
              .event-row {
                grid-template-columns: 96px minmax(0, 1fr);
              }
            }
        """;

    private const string UpdatePageTemplate =
        """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>MyGameBuilder Local updates</title>
          <style>
        __MGB_COMMON_CSS__
          </style>
        </head>
        <body>
          <div class="stage-shell">
            <header class="mgb-header">
              <div class="brand-row">
                <img class="mgb-logo" src="/_updates/flash-assets/logo-mgb-tiny.png" alt="MGB">
                <span class="mgb-wordmark">MyGameBuilder.com</span>
                <span class="red-rule" aria-hidden="true"></span>
                <span class="mgb-tagline">Make Games, Make Friends, Have Fun</span>
              </div>
            </header>
            <main class="client-area">
              <section class="window">
                <div class="window-title">
                <h1>MyGameBuilder Local updates</h1>
                  <button id="check" type="button"><img class="button-icon" src="/_updates/flash-assets/load.png" alt="">Check now</button>
              </div>
                <div class="window-body">
                  <p class="tutorial-clue" id="summary">Checking local setup and available releases.</p>
                  <div class="update-layout">
                    <section class="target-stack" aria-label="Update targets">
                      <article class="target-panel" id="frontend-card">
                        <div class="panel-heading">
                          <span class="status-lamp" aria-hidden="true"></span>
                          <img class="panel-icon" src="/_updates/flash-assets/load.png" alt="">
                <h2>Frontend files</h2>
                        </div>
                        <div class="panel-body">
                          <p class="message" id="frontend-message">frontend.sqlite is missing or ready to update.</p>
                <div class="meta" id="frontend-meta"></div>
                <div class="bar"><div id="frontend-progress"></div></div>
                <div class="actions">
                            <button class="primary" id="frontend-install" type="button"><img class="button-icon" src="/_updates/flash-assets/save.png" alt=""><span id="frontend-install-label">Install frontend</span></button>
                          </div>
                </div>
              </article>
                      <article class="target-panel" id="s3-card">
                        <div class="panel-heading">
                          <span class="status-lamp" aria-hidden="true"></span>
                          <img class="panel-icon" src="/_updates/flash-assets/save.png" alt="">
                <h2>S3 data archive</h2>
                        </div>
                        <div class="panel-body">
                <p class="message warning" id="s3-message"></p>
                <div class="meta" id="s3-meta"></div>
                <div class="bar"><div id="s3-progress"></div></div>
                <div class="actions">
                            <button id="s3-install" type="button"><img class="button-icon" src="/_updates/flash-assets/save.png" alt=""><span id="s3-install-label">Install data archive</span></button>
                          </div>
                </div>
              </article>
                      <article class="target-panel" id="app-card">
                        <div class="panel-heading">
                          <span class="status-lamp" aria-hidden="true"></span>
                          <img class="panel-icon" src="/_updates/flash-assets/play.png" alt="">
                <h2>App</h2>
                        </div>
                        <div class="panel-body">
                <p class="message" id="app-message"></p>
                <div class="meta" id="app-meta"></div>
                <div class="bar"><div id="app-progress"></div></div>
                <div class="actions">
                            <button id="app-install" type="button"><img class="button-icon" src="/_updates/flash-assets/play.png" alt=""><span id="app-install-label">Install app update</span></button>
                          </div>
                </div>
              </article>
            </section>
                    <aside class="side-panel" aria-label="Update event log">
                      <div class="side-heading">Network and Event log</div>
                      <div class="event-grid" id="event-log"></div>
                      <p class="side-note">The S3 data archive is optional and can be a large download.</p>
                    </aside>
                  </div>
                </div>
              </section>
          </main>
          </div>
          <script>
            const token = "__CSRF_TOKEN__";
            let busy = false;
            let autoChecked = false;
            let frontendPrompted = false;
            const targets = {
              app: { title: "App", installPath: "/_updates/app/install", installLabel: "Install app update" },
              s3: { title: "S3 data archive", installPath: "/_updates/archives/s3/install", installLabel: "Install data archive" },
              frontend: { title: "Frontend files", installPath: "/_updates/archives/frontend/install", installLabel: "Install frontend" },
            };

            document.getElementById("check").addEventListener("click", () => post("/_updates/check"));
            document.getElementById("app-install").addEventListener("click", () => post(targets.app.installPath));
            document.getElementById("s3-install").addEventListener("click", () => {
              if (confirm("The S3 data archive can be a large download and may take a while to install. Continue?")) post(targets.s3.installPath);
            });
            document.getElementById("frontend-install").addEventListener("click", () => post(targets.frontend.installPath));

            async function post(path) {
              if (busy) return;
              busy = true;
              setBusy(true);
              try {
                const response = await fetch(path, { method: "POST", headers: { "X-MGB-Update-Token": token } });
                if (!response.ok) throw new Error(await response.text());
                await refresh();
              } catch (error) {
                document.getElementById("summary").textContent = error.message || String(error);
              } finally {
                busy = false;
                setBusy(false);
              }
            }

            async function refresh() {
              const response = await fetch("/_updates/status", { cache: "no-store" });
              render(await response.json());
            }

            function render(status) {
              document.getElementById("summary").textContent = status.frontendArchive.missing
                ? "Install the frontend archive to start playing. The S3 data archive is optional and may be large."
                : status.s3Archive.missing
                  ? "Frontend files are installed. The S3 data archive is optional and may be large."
                  : "Your local files are installed. Updates remain opt-in.";
              renderTarget("frontend", status.frontendArchive, true);
              renderTarget("s3", status.s3Archive, false);
              renderTarget("app", status.app, false);
              renderLog(status);

              if (status.enabled && status.frontendArchive.missing && status.frontendArchive.state !== "working") {
                if (!status.frontendArchive.availableVersion && !autoChecked) {
                  autoChecked = true;
                  setTimeout(() => post("/_updates/check"), 100);
                  return;
                }

                if (status.frontendArchive.updateAvailable && !frontendPrompted) {
                  frontendPrompted = true;
                  setTimeout(() => {
                    if (confirm("Frontend files are missing. Install the latest frontend archive now?")) {
                      post(targets.frontend.installPath);
                    }
                  }, 100);
                }
              }
            }

            function renderTarget(id, item, primaryMissing) {
              const message = document.getElementById(`${id}-message`);
              const install = document.getElementById(`${id}-install`);
              const installLabel = document.getElementById(`${id}-install-label`);
              const card = document.getElementById(`${id}-card`);
              const current = !item.missing && Boolean(item.availableVersion) && !item.updateAvailable;
              card.classList.toggle("setup", Boolean(primaryMissing && item.missing));
              card.dataset.state = item.state || "unknown";
              card.dataset.missing = item.missing ? "true" : "false";
              card.dataset.current = current ? "true" : "false";
              message.textContent = item.message || (item.missing ? "Not installed." : "Installed.");
              message.classList.toggle("error", item.state === "error");
              message.classList.toggle("current", current);
              document.getElementById(`${id}-progress`).style.width = `${item.progressPercent || 0}%`;
              install.disabled = !item.canInstall || item.state === "working" || !item.updateAvailable;
              install.title = install.disabled ? disabledReason(item) : "";
              installLabel.textContent = installButtonLabel(id, item);
              document.getElementById(`${id}-meta`).innerHTML = [
                ["Installed", item.installedVersion || (item.missing ? "Missing" : "Unknown")],
                ["Available", item.availableVersion || "None found"],
                ["Download", formatBytes(item.downloadSizeBytes)],
                ["Status", statusLabel(item)],
              ].map(([label, value]) => `<div><span>${label}</span><span>${escapeHtml(value)}</span></div>`).join("");
            }

            function installButtonLabel(id, item) {
              if (item.state === "working") return "Working...";
              if (!item.updateAvailable && item.availableVersion && !item.missing) return "Up to date";
              if (!item.updateAvailable && !item.availableVersion) return "No release found";
              if (!item.canInstall) return "Unavailable";
              return targets[id].installLabel;
            }

            function disabledReason(item) {
              if (item.state === "working") return "This update is already in progress.";
              if (!item.updateAvailable && item.availableVersion && !item.missing) return "This component is up to date.";
              if (!item.updateAvailable && !item.availableVersion) return "No release is available for this component yet.";
              if (!item.canInstall) return item.message || "This component cannot be installed right now.";
              return "";
            }

            function statusLabel(item) {
              if (item.state === "working") return "Working";
              if (item.state === "error") return "Error";
              if (item.missing) return item.updateAvailable ? "Ready to install" : "Missing";
              if (item.updateAvailable) return "Update available";
              if (item.availableVersion) return "Up to date";
              return item.state || "Idle";
            }

            function renderLog(status) {
              document.getElementById("event-log").innerHTML = [
                ["Frontend", status.frontendArchive],
                ["S3", status.s3Archive],
                ["App", status.app],
              ].map(([label, item]) => {
                const text = item.message || (item.missing ? "Not installed." : "Installed.");
                return `<div class="event-row"><span>${label}</span><span>${escapeHtml(text)}</span></div>`;
              }).join("");
            }

            function setBusy(busy) {
              document.getElementById("check").disabled = busy;
              if (busy) {
                document.getElementById("app-install").disabled = true;
                document.getElementById("s3-install").disabled = true;
                document.getElementById("frontend-install").disabled = true;
              }
              if (!busy) refresh();
            }

            function formatBytes(value) {
              if (!value) return "Unknown";
              const units = ["B", "KB", "MB", "GB", "TB"];
              let n = value, i = 0;
              while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
              return `${n.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
            }

            function escapeHtml(value) {
              return String(value).replace(/[&<>"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[char]));
            }

            refresh();
            setInterval(refresh, 3000);
          </script>
        </body>
        </html>
        """;

    private const string MissingFrontendArchiveTemplate =
        """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>MyGameBuilder Local setup needed</title>
          <style>
        __MGB_COMMON_CSS__
          </style>
        </head>
        <body data-stage="cyan">
          <div class="stage-shell">
            <header class="mgb-header">
              <div class="brand-row">
                <img class="mgb-logo" src="/_updates/flash-assets/logo-mgb-tiny.png" alt="MGB">
                <span class="mgb-wordmark">MyGameBuilder.com</span>
                <span class="red-rule" aria-hidden="true"></span>
                <span class="mgb-tagline">Make Games, Make Friends, Have Fun</span>
              </div>
            </header>
            <main class="client-area">
              <section class="window setup-window">
                <div class="window-title">
                  <h1>MyGameBuilder Local setup needed</h1>
                  <a class="build-link" href="/updates">Open updates</a>
                </div>
                <div class="setup-body">
                  <p class="missing">frontend.sqlite was not found at <code>__FRONTEND_ARCHIVE_PATH__</code>.</p>
                  <p><a href="/updates">Open the updates page</a> to install the latest frontend archive. You can also optionally install the S3 data archive there; it is a large download and may take a while.</p>
                </div>
              </section>
          </main>
          </div>
        </body>
        </html>
        """;
}
