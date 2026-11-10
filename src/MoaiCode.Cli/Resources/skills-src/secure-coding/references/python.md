# Python: unsafe and safe pairs

## SQL (CWE-89)
- Unsafe: `cur.execute(f"SELECT * FROM t WHERE id = {id}")`, or `% id`, `.format(id)`, or `+`.
- Safe: `cur.execute("SELECT * FROM t WHERE id = %s", (id,))` (psycopg, pymysql), or `?` (sqlite3).
- SQLAlchemy: `text("... WHERE id = :id").bindparams(id=id)`, or ORM filters. Django: the ORM, or `raw(sql, [params])`, never `extra()` with input.
- Identifiers such as table or sort column: an allow-list, or `psycopg.sql.Identifier`.

## Commands and code (CWE-78, CWE-94)
- Unsafe: `os.system(cmd)`, `subprocess.run(cmd, shell=True)` with input, `eval`, `exec`, `compile` of input.
- Safe: `subprocess.run(["convert", "--", path], check=True)`. Use `ast.literal_eval` only for literal data.

## Deserialization (CWE-502)
- Never `pickle.loads`, `marshal`, `shelve`, `dill`, or `joblib.load` on untrusted data.
- `yaml.safe_load`, not `yaml.load` without a loader. For models: `torch.load(..., weights_only=True)`; prefer `safetensors`.

## XML (CWE-611)
- Parse untrusted XML with `defusedxml` (if the project already has it). With lxml: `etree.XMLParser(resolve_entities=False, no_network=True)`.

## Paths and files (CWE-22, CWE-377)
```python
base = Path("/data/uploads").resolve()
p = (base / name).resolve()
if not p.is_relative_to(base):
    raise ValueError("bad path")
```
- Temp files: `tempfile.NamedTemporaryFile` / `mkstemp`, not `mktemp()` or fixed `/tmp/name` paths.
- `tarfile`: `extractall(path, filter="data")` (3.12+), or check every member. Also check `zipfile` member names.
- Uploads (Flask/Django/FastAPI): check size, and check the extension against an allow-list. Name the stored file with `uuid4().hex`.

## Web frameworks
- **Templates:**
  - Jinja2: keep autoescape on. No `|safe` or `Markup()` on input.
  - `render_template_string` must not get user-controlled templates. (CWE-1336)
- **Django:** keep `CsrfViewMiddleware`. Set `DEBUG=False`, a strict `ALLOWED_HOSTS`, and `SESSION_COOKIE_SECURE` / `CSRF_COOKIE_SECURE` in production.
- **Flask:** `app.run(debug=True)` never in production. Set `SESSION_COOKIE_HTTPONLY` / `SECURE` / `SAMESITE`.
- **FastAPI:** validate with Pydantic models (types, `constr`, `conint`). Add auth dependencies on every router.
- **Redirects:** use `url_has_allowed_host_and_scheme` (Django), or allow relative paths only.

## Crypto and auth
- Randomness: `secrets.token_urlsafe()`, `secrets.token_bytes()`. Never `random` for tokens. (CWE-330)
- Passwords: `bcrypt` / `argon2-cffi` / `hashlib.scrypt` / `hashlib.pbkdf2_hmac` (high iterations). Never `hashlib.md5`/`sha1`/`sha256` alone.
- Encryption: `cryptography` AESGCM with `os.urandom(12)` nonces. Never `Crypto.Cipher` in ECB mode, or home-made XOR.
- Constant-time compare: `hmac.compare_digest`.
- JWT (PyJWT): `jwt.decode(t, key, algorithms=["RS256"], audience=..., issuer=...)`.
- TLS: never `verify=False` (requests/httpx) or `ssl._create_unverified_context()`. Use `verify="/path/internal-ca.pem"`. (CWE-295)

## SSRF (CWE-918)
- Before `requests.get(user_url)`: allow-list the scheme and host, resolve with `socket.getaddrinfo`, and reject `ipaddress.ip_address(x).is_private`, `is_loopback`, `is_link_local`. Set `allow_redirects=False`, or re-check each hop.

## Logging (CWE-117, CWE-532)
- `logger.info("user=%s", user_id)`, not f-strings with secrets. Strip `\r\n` from input that goes into logs.

## Other
- `assert` is removed under `-O`. Never use it for security checks.
- Regular expressions on input: cap the length, and avoid nested quantifiers. (CWE-1333)
- Money: `decimal.Decimal`, never `float`.
