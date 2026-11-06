/*
  Everything you might want to change lives here.

  The app's name appears on the page only through SITE.name (and in
  index.html's <head>, which is marked "APP NAME"). Rename it here, and
  every heading, button and sentence on the page follows.

  An empty string ('') means "not ready yet": the page shows a tidy
  "coming soon" placeholder instead of a broken link.
*/
window.SITE = {
  name: 'RemoteCam',
  tagline: 'Run your Sony camera from anywhere.',
  version: '1.0.13',

  github: 'https://github.com/RemingtonWilcox/RemoteCam',
  releases: 'https://github.com/RemingtonWilcox/RemoteCam/releases/latest',
  issues: 'https://github.com/RemingtonWilcox/RemoteCam/issues',
  agentsGuide: 'https://github.com/RemingtonWilcox/RemoteCam/blob/main/AGENTS.md',

  downloads: {
    windows: 'https://github.com/RemingtonWilcox/RemoteCam/releases/latest/download/CamRemote-Windows-Setup.exe',
    macApple: 'https://github.com/RemingtonWilcox/RemoteCam/releases/latest/download/CamRemote-Mac-AppleSilicon.dmg',
    macIntel: 'https://github.com/RemingtonWilcox/RemoteCam/releases/latest/download/CamRemote-Mac-Intel.dmg',
    // The public TestFlight beta, one build for iPhone and iPad (iOS 16 or newer).
    testflight: 'https://testflight.apple.com/join/ck3rFy8f',
  },

  // The launch video's YouTube ID (https://youtu.be/ChdXwOyK7DU). It only
  // loads, from youtube-nocookie.com, after someone clicks play.
  youtubeId: 'ChdXwOyK7DU',
  videoTitle: 'I built a free app that runs my Sony camera from anywhere',

  // Who makes it: a small credit in the closer, the footer and the
  // thank-you note after a download.
  maker: 'Remington Wilcox',

  // PLACEHOLDER: a tip-jar link (Ko-fi, GitHub Sponsors or Buy Me a
  // Coffee). While it's '', no "support" link appears anywhere.
  support: 'https://ko-fi.com/remingtonwilcox',

  // PLACEHOLDER: full profile links. Leave '' for any you don't use:
  // unused ones are simply left out.
  socials: {
    youtube: 'https://www.youtube.com/@remwilcox',
    instagram: 'https://www.instagram.com/remingtonwilcox/',
    tiktok: 'https://www.tiktok.com/@remington.mp4',
    x: '',
    discord: '',
  },
};
