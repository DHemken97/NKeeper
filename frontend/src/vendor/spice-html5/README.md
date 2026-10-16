# spice-html5 (vendored)

The SPICE web client from the SPICE project (freedesktop.org), **release spice-html5-0.3.0**,
licensed under the GNU LGPL v3 (`COPYING.LESSER`, with the GPL text in `COPYING`). The bundled
helpers in `thirdparty/` are by Tom Wu (jsbn, rsa, rng, prng4; MIT-style licence) and Paul
Johnston (sha1; BSD licence), as stated in their headers.

Provenance:

- Source: `https://gitlab.freedesktop.org/spice/spice-html5/-/archive/spice-html5-0.3.0/spice-html5-spice-html5-0.3.0.tar.gz`
- Tag `spice-html5-0.3.0` → commit `56ddb3005e39a9e76670fec9676e667d44167bf3` (2020-09-04)
- Archive SHA-256: `90fa3b30cda272471104af0b61ea08f2194048455133889a28a8bed0c00e3527`

Contents: `src/*.js` and `src/thirdparty/{jsbn,prng4,rng,rsa,sha1}.js`, **unmodified**. Not
included: the demo pages and `thirdparty/browser-es-module-loader` (a development polyfill).
`main.d.ts` is NKeeper's own type declaration for the parts NKeeper uses.

NKeeper loads it only for VM consoles (lazy-loaded) and does not use its drag-and-drop file
transfer or window-resize helpers, which rely on a page-global connection. Because the library
names its canvases with page-global IDs, NKeeper keeps one SPICE console open at a time.
