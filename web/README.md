# DimDock Website

Static WinUI 3-styled landing page for [DimDock](https://github.com/ohivan/dimdock). Zero framework dependencies and zero build step — ready for Cloudflare Workers (Static Assets) or Cloudflare Pages.

## Local Preview

```bash
cd web
bun run dev
```

Uses Bun's built-in HTML dev server with live reload (`http://localhost:3000`). To test with Cloudflare's [`wrangler.jsonc`](./wrangler.jsonc) and [`_headers`](./public/_headers), run `bun run dev:cf`.

## Deploying to Cloudflare

### Cloudflare Workers (Static Assets)

Uses [`wrangler.jsonc`](./wrangler.jsonc) (`assets.directory = "./public"`):

- **CLI**: `cd web && bun run deploy`
- **Git integration**: Set **Root directory** to `web` and **Deploy command** to `bunx wrangler deploy` (or `npx wrangler deploy`).

### Cloudflare Pages

- **Build command**: *(leave empty)*
- **Build output directory**: `web/public`

## Configuration

Update `CONFIG` at the top of [`public/app.js`](./public/app.js) if you change the WinGet package ID, fallback version, or GitHub repository.
