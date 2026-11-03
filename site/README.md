# The launch website

A single static page: plain HTML, CSS and JavaScript, with no build step and nothing loaded from anyone else.

| File | What it is |
|---|---|
| `index.html` | The page |
| `config.js` | **Everything you'd change:** the app's name, version, download links, TestFlight link, YouTube video, social links and iPhone/iPad screenshots |
| `styles.css` | Look and layout, light and dark |
| `app.js` | The interactive parts |
| `theme.js` | Picks light or dark before the page draws |
| `assets/` | Pictures. `liveview.jpg` is the egret frame the live-view demo works on |

## Change something

- **The name:** `name` in `config.js`, plus the three lines marked `APP NAME` at the top of `index.html`.
- **A new version:** `version` in `config.js`.
- **Placeholders:** anything left as `''` in `config.js` shows as "coming soon". Fill it in and it appears.
- **iPhone and iPad screenshots:** put them in `assets/` and set `screens.iphone` and `screens.ipad` in `config.js`.

## Look at it

Open it through a small local web server, so the live-view demo can read its picture:

```bash
python -m http.server 8791 --directory site
```

Then open http://localhost:8791. Opening `index.html` straight from the folder works too, but zebras, peaking and the histogram stay off, because browsers don't let a page read pictures from a file on disk.

## Privacy

No cookies, analytics or fonts from elsewhere, and a Content-Security-Policy in `index.html` that blocks anything else. The only outside request is the YouTube video, from `youtube-nocookie.com`, and only after someone presses play. Light/dark is remembered in the browser only if the visitor presses the button.
