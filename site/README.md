# The launch website

A single static page: plain HTML, CSS and JavaScript, with no build step and nothing loaded from anyone else.

| File | What it is |
|---|---|
| `index.html` | The page |
| `config.js` | **Everything you'd change:** the app's name, version, download links, TestFlight link, YouTube video and social links. The video section and the hero's **Watch the video** button stay hidden until `youtubeId` is set |
| `styles.css` | Look and layout, light and dark |
| `app.js` | The interactive parts |
| `theme.js` | Picks light or dark before the page draws |
| `assets/` | Pictures. `focus-jay.jpg` and its depth map drive the focus demo; the `.mp4` clips play muted on a loop while they're on screen |

## Change something

- **The name:** `name` in `config.js`, plus the three lines marked `APP NAME` at the top of `index.html`.
- **A new version:** `version` in `config.js`.
- **Placeholders:** anything left as `''` in `config.js` shows as "coming soon". Fill it in and it appears.
- **Pictures and clips:** they're in `assets/`. Replace a file with one of the same name, or change its `src` in `index.html`.

## Look at it

Open it through a small local web server, so the live-view demo can read its picture:

```bash
python -m http.server 8791 --directory site
```

Then open http://localhost:8791. Opening `index.html` straight from the folder works too, but zebras, peaking and the histogram stay off, because browsers don't let a page read pictures from a file on disk.

## Put it online

It's served by Cloudflare at https://getremotecam.com. To publish the current files:

```bash
cd tools/website
npx wrangler deploy
```

`_headers` sets the security headers, and `.assetsignore` keeps this README off the site.

## Privacy

No cookies, analytics or fonts from elsewhere, and a Content-Security-Policy in `index.html` that blocks anything else. The only outside request is the YouTube video, from `youtube.com`, and only after someone presses play. Light/dark is remembered in the browser only if the visitor presses the button.
