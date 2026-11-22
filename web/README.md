# DimDock Website

Static WinUI 3-styled landing page for [DimDock](https://github.com/ohivan/dimdock). Zero framework dependencies and zero build step — ready for Cloudflare Workers (Static Assets) or Cloudflare Pages.

## Local Preview

```bash
cd web
npm run dev
```

Opens at `http://127.0.0.1:4321`.

## Deploying to Cloudflare

### Cloudflare Workers (Static Assets)

Uses [`wrangler.jsonc`](./wrangler.jsonc) (`assets.directory = "./public"`):

- **CLI**: `cd web && npm run deploy`
- **Git integration**: Set **Root directory** to `web` and **Deploy command** to `npx wrangler deploy`.

### Cloudflare Pages

- **Build command**: *(leave empty)*
- **Build output directory**: `web/public`

## Configuration

Update `CONFIG` at the top of [`public/app.js`](./public/app.js) if you change the WinGet package ID, fallback version, or GitHub repository.
