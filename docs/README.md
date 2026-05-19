# Project landing page

This folder contains the GitHub Pages site for **ghcp-logs-analyzer**.

## Enabling GitHub Pages

1. Go to **Settings → Pages** in the repository.
2. Under **Source**, select **Deploy from a branch**.
3. Choose branch `main` and folder `/docs`, then **Save**.
4. The site will be published at `https://<owner>.github.io/ghcp-logs-analyzer/`.

The `_config.yml` disables the default Jekyll theme (Primer) that was causing the build to fail.
The `.nojekyll` file is kept as an additional guard against Jekyll processing.

## Files

- `index.html` — landing page
- `styles.css` — styling (dark theme inspired by GitHub)
- `.nojekyll` — disables Jekyll on GitHub Pages
