---
name: secure-coding
description: Always-on secure coding rules (OWASP Top 10, CWE Top 25) applied while writing or changing code — injection, XSS, path traversal, deserialization, access control, secrets, crypto, TLS, error handling, and logging. Turned on and off with /skills.
license: MATE Builder original — no third-party content
passive: true
---

# Secure coding (always on)

Apply these rules to every line of code you write or change, in any language. They are based on the OWASP Top 10 and the CWE Top 25; CWE numbers are in parentheses.

- Do not rewrite unrelated existing code. If you see a vulnerability in code you read or touch, report it (`file:line`, CWE, one-line fix) and fix it only when it is part of the task or the user agrees.
- Never weaken an existing control to make something work: input validation, TLS checks, CSRF protection, authentication, authorization, or a security header. Find the real cause, or ask.
- When the project, company policy, or the user sets a stricter rule, that rule wins.

## Input and injection
- Treat as untrusted: request parameters, headers, cookies and bodies, uploaded files, environment and config that users can change, queue messages, data that other systems write to the database, and model (LLM) output.
- Validate on the server, at the boundary, with allow-lists (type, length, range, format). Reject bad input; do not "clean it up and carry on". (CWE-20)
- **SQL:** use bound parameters only. Never concatenate or format input into SQL, JPQL, HQL, or raw ORM queries. MyBatis: `#{}`. Use `${}` only for an identifier chosen from a fixed allow-list, such as a sort column. (CWE-89)
- **OS commands:** do not go through a shell. Pass the program and its arguments as an array (ProcessBuilder, `subprocess.run([...])`, `execFile`). Allow-list the command, and put `--` before user-supplied operands. (CWE-78)
- **Other interpreters:**
  - LDAP filters: escape. (CWE-90)
  - XPath: parameterize. (CWE-643)
  - NoSQL: reject objects where a string is expected. (CWE-943)
  - No user-controlled templates (CWE-1336) or expression-language input (SpEL, OGNL, EL). (CWE-917)
  - No eval, exec, `new Function`, or dynamic code built from input. (CWE-94)
- Remove CR and LF from input before it goes into logs (CWE-117) or HTTP headers. (CWE-113)

## Web output
- **XSS:** rely on the framework's automatic escaping, and encode for the context (HTML, attribute, JavaScript, URL). Put input into raw-HTML sinks only after a vetted sanitizer. Raw-HTML sinks: `innerHTML`, `dangerouslySetInnerHTML`, `v-html`, `th:utext`, `|safe`, `Html.Raw`. (CWE-79)
- **CSRF:** keep the framework's protection on for cookie-authenticated requests that change state. Never change state on GET. (CWE-352)
- **Redirects:** allow relative paths or allow-listed hosts only. (CWE-601)
- **SSRF:** when the server fetches a URL the user gave:
  - allow-list the scheme and host;
  - block private, loopback, and metadata addresses after DNS resolution;
  - re-check on every redirect. (CWE-918)
- **CORS:** never `*` with credentials, and never echo back the request's Origin. (CWE-942)
- **Cookies:** HttpOnly, Secure, SameSite. (CWE-1004, CWE-614)

## Files and parsing
- **Paths:** canonicalize the path and check that it stays under the intended base folder. Never use a client-supplied file name as a path; generate the stored name on the server. (CWE-22, CWE-73)
- **Uploads:** allow-list size, extension, and content type. Store outside the web root and never execute. (CWE-434)
- **Archives:** check every entry path (zip slip) and the total extracted size (zip bomb). (CWE-22, CWE-409)
- **XML:** disable DTDs and external entities. (CWE-611)
- **Deserialization:** never deserialize untrusted data with a native or type-carrying serializer:
  - Java `ObjectInputStream`
  - .NET `BinaryFormatter`, or `TypeNameHandling` other than `None`
  - Python `pickle`, `yaml.load` without SafeLoader
  - Jackson default typing

  Bind JSON to explicit DTO types instead. (CWE-502)
- **Temp files:** use the platform's secure API, not a predictable name. (CWE-377)
- **Limits:** bound sizes, loop counts, recursion, and regular expressions run on input (ReDoS). Close resources with try-with-resources, `using`, or `with`. (CWE-400, CWE-1333, CWE-772)

## Access control and sessions
- **Authorize on the server:** do it for every request and every object. Check that the current user may access the ID they sent (IDOR), and deny by default. (CWE-862, CWE-639)
- **Client data:** never trust client-supplied roles, user IDs, prices, amounts, or hidden fields. (CWE-602)
- **Passwords:** store with bcrypt, scrypt, Argon2, or PBKDF2. Never use a plain or fast hash, and never use reversible encryption. (CWE-916)
- **Sessions:** issue a new session ID at login, apply an idle timeout, and invalidate on logout (CWE-384, CWE-613). Limit repeated login failures. (CWE-307)
- **JWT:** verify the signature with a fixed algorithm (reject `none`), plus `exp`, `iss`, and `aud`. Put no secrets in the payload. (CWE-347)

## Secrets and crypto
- **Secrets:** no hard-coded passwords, keys, tokens, or connection strings that contain passwords. Read them from the project's secret mechanism or the environment, and never commit `.env` files. (CWE-798)
- **Algorithms:** use vetted libraries only:
  - AES-GCM (or ARIA/SEED where policy requires)
  - RSA 2048 or larger with OAEP
  - SHA-256 or stronger

  Never use for security: MD5, SHA-1, DES, 3DES, RC4, ECB mode, a fixed IV or nonce, or home-made crypto. (CWE-327, CWE-328, CWE-329)
- **Randomness:** tokens, IDs, OTPs, and salts come from a cryptographic RNG only. (CWE-330, CWE-338)
- **TLS:** never turn off certificate or hostname checks, not even "for testing". Trust the internal CA bundle instead. (CWE-295)
- **Sensitive data:** send it over TLS, and encrypt it at rest where policy requires. (CWE-319, CWE-311)

## Errors and logging
- **Error responses:** show clients a generic message and a correlation ID. Stack traces, SQL errors, internal paths, and versions go only to server logs. (CWE-209)
- **Logs:** never log passwords, tokens, keys, session IDs, full card numbers, CVV, or resident registration numbers. Mask them. (CWE-532)
- **Failures:** in security checks, fail closed. Never swallow an exception silently. Check return values and nulls. (CWE-636, CWE-390, CWE-252, CWE-476)

## State, numbers, configuration
- **Check-then-act:** for balances, stock, or a file's existence, use a transaction, a lock, or an atomic operation. (CWE-362, CWE-367)
- **Numbers:** guard sizes and amounts against overflow. Use decimal types for money, never floating point. (CWE-190, CWE-681)
- **Production config:** turn off debug mode, default accounts, and open admin or diagnostic endpoints (actuator, Swagger, consoles). (CWE-489, CWE-1188)
- **Least privilege:** DB accounts, containers (non-root), and file permissions get only what they need. (CWE-250, CWE-732)
- **Dependencies:** add one only after asking, pin its version, and take it from the internal mirror only. Add no new download URLs to build files. (CWE-1104, CWE-829)

## Language details
Before writing code in one of these stacks, read the matching file once per session. Each lists unsafe and safe API pairs:
- Java, Spring, MyBatis: `references/java-spring.md`
- JavaScript, TypeScript, Node.js: `references/javascript-node.md`
- Python: `references/python.md`
- C#, .NET: `references/csharp-dotnet.md`

## Before you finish
- Check the code you wrote against these rules.
- If a rule could not be followed (for example, a legacy API offers no safe form), name the rule and the reason in your reply.
- For a full security review of existing code, use the `code-review` or `financial-secure-code-review` skill.
