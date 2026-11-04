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
  version: '1.0.11',

  github: 'https://github.com/RemingtonWilcox/RemoteCam',
  releases: 'https://github.com/RemingtonWilcox/RemoteCam/releases/latest',
  issues: 'https://github.com/RemingtonWilcox/RemoteCam/issues',
  agentsGuide: 'https://github.com/RemingtonWilcox/RemoteCam/blob/main/AGENTS.md',

  downloads: {
    windows: 'https://github.com/RemingtonWilcox/RemoteCam/releases/latest/download/CamRemote-Windows-Setup.exe',
    macApple: 'https://github.com/RemingtonWilcox/RemoteCam/releases/latest/download/CamRemote-Mac-AppleSilicon.dmg',
    macIntel: 'https://github.com/RemingtonWilcox/RemoteCam/releases/latest/download/CamRemote-Mac-Intel.dmg',
    // PLACEHOLDER: the public TestFlight invite link (https://testflight.apple.com/join/...).
    testflight: '',
  },

  // PLACEHOLDER: the launch video's YouTube ID, the part after "v=" in
  // https://www.youtube.com/watch?v=XXXXXXXXXXX. It only loads, from
  // youtube-nocookie.com, after someone clicks play.
  youtubeId: '',
  videoTitle: 'How far away can I run my camera?',

  // PLACEHOLDER: full profile links. Leave '' for any you don't use.
  socials: {
    youtube: '',
    instagram: 'https://www.instagram.com/remingtonwilcox/',
    tiktok: '',
    x: '',
    discord: '',
  },
};
