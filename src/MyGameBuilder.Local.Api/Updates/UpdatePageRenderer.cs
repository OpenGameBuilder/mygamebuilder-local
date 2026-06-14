using System.Text.Encodings.Web;

namespace MyGameBuilder.Local.Api.Updates;

public static class UpdatePageRenderer
{
    public static string BuildUpdatePage(string csrfToken) =>
        UpdatePageTemplate.Replace("__CSRF_TOKEN__", JavaScriptEncoder.Default.Encode(csrfToken), StringComparison.Ordinal);

    public static string BuildMissingFrontendArchivePage(string archivePath) =>
        MissingFrontendArchiveTemplate.Replace("__FRONTEND_ARCHIVE_PATH__", HtmlEncoder.Default.Encode(archivePath), StringComparison.Ordinal);

    private const string UpdatePageTemplate =
        """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>MyGameBuilder Local updates</title>
          <style>
            :root {
              color-scheme: dark;
              --bg: #14171c;
              --panel: #20262f;
              --panel-2: #262d37;
              --text: #f2f5f8;
              --muted: #b7c0cb;
              --line: #3b4654;
              --accent: #55c2a2;
              --warn: #f0c66a;
              --danger: #ff8f8f;
            }
            * { box-sizing: border-box; }
            body {
              margin: 0;
              min-height: 100vh;
              background: var(--bg);
              color: var(--text);
              font: 15px/1.45 system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
            }
            main { max-width: 980px; margin: 0 auto; padding: 32px 20px 48px; }
            header { display: flex; align-items: flex-end; justify-content: space-between; gap: 20px; margin-bottom: 22px; }
            h1 { font-size: 28px; margin: 0 0 6px; letter-spacing: 0; }
            p { margin: 0; color: var(--muted); }
            button, a.button {
              border: 1px solid var(--line);
              background: var(--panel-2);
              color: var(--text);
              border-radius: 6px;
              padding: 9px 13px;
              font: inherit;
              text-decoration: none;
              cursor: pointer;
            }
            button.primary { background: var(--accent); border-color: var(--accent); color: #071511; font-weight: 700; }
            button:disabled { opacity: .55; cursor: not-allowed; }
            .grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 14px; }
            .card {
              background: var(--panel);
              border: 1px solid var(--line);
              border-radius: 8px;
              padding: 16px;
              min-height: 240px;
              display: flex;
              flex-direction: column;
              gap: 12px;
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
              header { align-items: flex-start; flex-direction: column; }
              .grid { grid-template-columns: 1fr; }
            }
          </style>
        </head>
        <body>
          <main>
            <header>
              <div>
                <h1>MyGameBuilder Local updates</h1>
                <p id="summary">Checking local setup and available releases.</p>
              </div>
              <button id="check" type="button">Check now</button>
            </header>
            <section class="grid">
              <article class="card" id="frontend-card">
                <h2>Frontend files</h2>
                <p class="message" id="frontend-message">frontend.sqlite is missing or ready to update.</p>
                <div class="meta" id="frontend-meta"></div>
                <div class="bar"><div id="frontend-progress"></div></div>
                <div class="actions">
                  <button class="primary" id="frontend-install" type="button">Install frontend</button>
                </div>
              </article>
              <article class="card" id="s3-card">
                <h2>S3 data archive</h2>
                <p class="message warning" id="s3-message"></p>
                <div class="meta" id="s3-meta"></div>
                <div class="bar"><div id="s3-progress"></div></div>
                <div class="actions">
                  <button id="s3-install" type="button">Install data archive</button>
                </div>
              </article>
              <article class="card" id="app-card">
                <h2>App</h2>
                <p class="message" id="app-message"></p>
                <div class="meta" id="app-meta"></div>
                <div class="bar"><div id="app-progress"></div></div>
                <div class="actions">
                  <button id="app-install" type="button">Install app update</button>
                </div>
              </article>
            </section>
          </main>
          <script>
            const token = "__CSRF_TOKEN__";
            let busy = false;
            let autoChecked = false;
            let frontendPrompted = false;
            const targets = {
              app: { title: "App", installPath: "/_updates/app/install" },
              s3: { title: "S3 data archive", installPath: "/_updates/archives/s3/install" },
              frontend: { title: "Frontend files", installPath: "/_updates/archives/frontend/install" },
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
              const card = document.getElementById(`${id}-card`);
              card.classList.toggle("setup", Boolean(primaryMissing && item.missing));
              message.textContent = item.message || (item.missing ? "Not installed." : "Installed.");
              message.classList.toggle("error", item.state === "error");
              document.getElementById(`${id}-progress`).style.width = `${item.progressPercent || 0}%`;
              install.disabled = !item.canInstall || item.state === "working" || !item.updateAvailable;
              document.getElementById(`${id}-meta`).innerHTML = [
                ["Installed", item.installedVersion || (item.missing ? "Missing" : "Unknown")],
                ["Available", item.availableVersion || "None found"],
                ["Download", formatBytes(item.downloadSizeBytes)],
                ["State", item.state],
              ].map(([label, value]) => `<div><span>${label}</span><span>${escapeHtml(value)}</span></div>`).join("");
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
            html, body { min-height: 100%; margin: 0; background: #191b1f; color: #f2f2f2; font-family: Arial, sans-serif; }
            body { display: grid; place-items: center; padding: 32px; box-sizing: border-box; }
            main { max-width: 760px; }
            h1 { font-size: 28px; line-height: 1.2; margin: 0 0 16px; }
            p { font-size: 16px; line-height: 1.55; margin: 12px 0; color: #d8dce3; }
            .missing { color: #ff7b7b; font-weight: 700; }
            code { color: #ffffff; background: #2a2f38; padding: 2px 5px; border-radius: 4px; }
            a { color: #8ee7c8; font-weight: 700; }
          </style>
        </head>
        <body>
          <main>
            <h1>MyGameBuilder Local setup needed</h1>
            <p class="missing">frontend.sqlite was not found at <code>__FRONTEND_ARCHIVE_PATH__</code>.</p>
            <p><a href="/updates">Open the updates page</a> to install the latest frontend archive. You can also optionally install the S3 data archive there; it is a large download and may take a while.</p>
          </main>
        </body>
        </html>
        """;
}
