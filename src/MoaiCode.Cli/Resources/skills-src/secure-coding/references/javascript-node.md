# JavaScript, TypeScript, Node.js: unsafe and safe pairs

## SQL and NoSQL (CWE-89, CWE-943)
- **Driver:**
  - Unsafe: `db.query("SELECT * FROM t WHERE id = " + id)`, or template strings in raw queries.
  - Safe: `db.query("SELECT * FROM t WHERE id = $1", [id])` (pg), or `?` placeholders (mysql2).
  - Use the ORM's query builder, or a tagged template that binds (Prisma `$queryRaw\`...${id}\``, never `$queryRawUnsafe` with input).
- **MongoDB:**
  - Before `find({ user: req.body.user })`, check `typeof req.body.user === "string"`. This blocks `{ "$ne": null }` operator injection.
  - Never `$where` with input.

## Commands and code (CWE-78, CWE-94)
- Unsafe: `exec("convert " + file)`, `eval(input)`, `new Function(input)`, `vm.runInNewContext` as a sandbox.
- Safe: `execFile("convert", ["--", file])`, or `spawn(cmd, args, { shell: false })`.

## XSS (CWE-79)
- **React:** `{value}` is safe. `dangerouslySetInnerHTML` only after DOMPurify. Check `href` / `src` values from users for a `javascript:` scheme.
- **Vue:** `{{ }}`, not `v-html`. **Angular:** do not call `bypassSecurityTrust*` on input.
- **Plain DOM:** `el.textContent = v`, not `innerHTML`. No `document.write` with input.
- **Server templates:** EJS `<%= %>` (escaped), not `<%- %>`. Handlebars `{{ }}`, not `{{{ }}}`.

## Paths and files (CWE-22)
```js
const base = path.resolve("/data/uploads");
const p = path.resolve(base, name);
if (!p.startsWith(base + path.sep)) throw new Error("bad path");
```
- Uploads (multer etc.): set `limits.fileSize` and check the extension against an allow-list. Store under a generated name outside `public/`.

## Prototype pollution (CWE-1321)
- No deep merge or `Object.assign` of request bodies into existing objects. Reject `__proto__`, `constructor`, and `prototype` keys.
- Use `Object.create(null)` or `Map` for maps keyed by input.

## HTTP and Express
- `helmet()` for security headers. `app.disable("x-powered-by")`.
- **CSRF:** cookie-session apps need a CSRF token (`csrf-csrf` or similar) or `SameSite=Strict` cookies.
- **Cookies:** `{ httpOnly: true, secure: true, sameSite: "lax" }`.
- **CORS:** `cors({ origin: allowedOrigins, credentials: true })` with exact origins from config. Never `origin: true` together with credentials.
- **Redirects:** `res.redirect(url)` only for relative paths or allow-listed hosts.
- **Errors:** never send `err.stack` to clients. Production is `NODE_ENV=production`.
- **Body limits:** `express.json({ limit: "100kb" })`.
- **SSRF:** before `fetch` / `axios` of a user URL, resolve the host and refuse private ranges. Set `maxRedirects: 0`, or re-check each hop.

## Crypto and auth
- Randomness: `crypto.randomBytes(32)` / `crypto.randomUUID()`. Never `Math.random()` for tokens. (CWE-330)
- Passwords: `bcrypt`, `argon2`, or `crypto.scrypt`. Compare secrets with `crypto.timingSafeEqual`.
- Ciphers: `createCipheriv("aes-256-gcm", key, iv)` with a fresh 12-byte IV. Never `createCipher` (deprecated, no IV).
- JWT: `jwt.verify(token, key, { algorithms: ["RS256"], audience, issuer })`. Never `jwt.decode` for trust.
- TLS: never `rejectUnauthorized: false` or `NODE_TLS_REJECT_UNAUTHORIZED=0`. Pass the internal CA with `ca:` or `NODE_EXTRA_CA_CERTS`. (CWE-295)

## Regular expressions (CWE-1333)
- Avoid nested quantifiers such as `(a+)+` on input, and cap the input length before matching.

## Browser storage
- No tokens or personal data in `localStorage`. Prefer HttpOnly cookies. Never put secrets in front-end bundles or `NEXT_PUBLIC_*` / `VITE_*` variables.
