(() => {
  const body = document.body;
  const repo = resolveRepo(body.dataset.repo) || deriveRepoFromLocation();
  const releaseLinks = document.querySelectorAll("[data-release-url]");
  const repoLinks = document.querySelectorAll("[data-repo-link]");
  const status = document.querySelector("[data-release-status]");

  if (repo) {
    const releaseUrl = `https://github.com/${repo}/releases/latest`;
    releaseLinks.forEach((link) => {
      link.href = releaseUrl;
    });
    repoLinks.forEach((link) => {
      link.href = buildRepoLink(repo, link.dataset.repoLink);
    });
  } else if (status) {
    status.textContent = "Set data-repo in index.html to enable automatic downloads.";
  }

  if (!repo) {
    return;
  }

  if (status) {
    status.textContent = "Checking latest release...";
  }

  fetch(`https://api.github.com/repos/${repo}/releases/latest`, {
    headers: { Accept: "application/vnd.github+json" },
  })
    .then((response) => {
      if (!response.ok) {
        throw new Error("Release request failed.");
      }
      return response.json();
    })
    .then((release) => {
      const version = release.tag_name || "latest";
      if (status) {
        status.textContent = `Latest release: ${version}`;
      }

      document.querySelectorAll("[data-version]").forEach((node) => {
        node.textContent = version;
      });

      const assets = Array.isArray(release.assets) ? release.assets : [];
      const mappings = [
        { key: "gui-win", regex: /transcribe-.*-winx64\.zip$/i },
        { key: "gui-mac", regex: /transcribe-.*-osx-arm64\.zip$/i },
        { key: "gui-linux", regex: /transcribe-.*-linux-x64\.zip$/i },
        { key: "cli-win", regex: /transcribe-cli-.*-winx64\.zip$/i },
        { key: "cli-mac", regex: /transcribe-cli-.*-osx-arm64\.zip$/i },
        { key: "cli-linux", regex: /transcribe-cli-.*-linux-x64\.zip$/i },
      ];

      mappings.forEach((mapping) => {
        const asset = assets.find((item) => mapping.regex.test(item.name));
        const link = document.querySelector(`[data-asset="${mapping.key}"]`);
        if (asset && link) {
          link.href = asset.browser_download_url;
          link.title = `Download ${asset.name}`;
        }
      });
    })
    .catch(() => {
      if (status) {
        status.textContent = "Latest release: open on GitHub.";
      }
    });

  function resolveRepo(value) {
    if (!value) {
      return null;
    }
    const trimmed = value.trim();
    if (!trimmed || trimmed === "OWNER/REPO") {
      return null;
    }
    const parts = trimmed.split("/");
    return parts.length === 2 ? trimmed : null;
  }

  function deriveRepoFromLocation() {
    const host = window.location.hostname;
    const pathParts = window.location.pathname.split("/").filter(Boolean);
    if (host.endsWith("github.io") && pathParts.length > 0) {
      const owner = host.split(".")[0];
      const repo = pathParts[0];
      if (repo && repo !== `${owner}.github.io`) {
        return `${owner}/${repo}`;
      }
    }
    return null;
  }

  function buildRepoLink(repo, type) {
    const base = `https://github.com/${repo}`;
    switch (type) {
      case "issues":
        return `${base}/issues`;
      case "releases":
        return `${base}/releases`;
      case "readme":
        return `${base}#readme`;
      default:
        return base;
    }
  }
})();
