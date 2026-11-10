# Java, Spring, MyBatis: unsafe and safe pairs

## SQL (CWE-89)
- **JDBC:**
  - Unsafe: `stmt.executeQuery("... WHERE id = '" + id + "'")`
  - Safe: `PreparedStatement ps = con.prepareStatement("... WHERE id = ?"); ps.setString(1, id);`
- **JPA:**
  - Unsafe: `em.createQuery("... where name = '" + name + "'")`
  - Safe: `em.createQuery("... where name = :name").setParameter("name", name)`. Spring Data `@Query` with `:param` or `?1`.
- **MyBatis:**
  - Unsafe: `WHERE id = ${id}`
  - Safe: `WHERE id = #{id}`.
  - `ORDER BY ${col}` is acceptable only after an allow-list check in Java: `Set.of("name", "created_at").contains(col)`.
  - `LIKE`: `LIKE CONCAT('%', #{kw}, '%')` (Oracle: `'%' || #{kw} || '%'`).
- **Dynamic IN lists:** `<foreach collection="ids" item="id" open="(" separator="," close=")">#{id}</foreach>`.

## OS commands (CWE-78)
- Unsafe: `Runtime.getRuntime().exec("sh -c ls " + dir)` or `exec(String)` with input.
- Safe: `new ProcessBuilder("ls", "--", dir)`, after an allow-list check on `dir`. Never `bash -c` with input.

## XSS (CWE-79)
- Thymeleaf: `th:text`, not `th:utext`. JSP: `<c:out value="${v}"/>` or `fn:escapeXml`, not raw `${v}` in markup.
- `@ResponseBody` returning HTML built from strings: escape with `HtmlUtils.htmlEscape`, or return JSON.

## Paths and files (CWE-22, CWE-434)
```java
Path base = Paths.get("/data/uploads").toRealPath();
Path p = base.resolve(name).normalize();
if (!p.startsWith(base)) throw new IllegalArgumentException("bad path");
```
- Store uploads under a generated name: `UUID.randomUUID() + allowedExt`.
- Check `MultipartFile.getSize()`, and check the extension against an allow-list. Never under `static/` or `webapp/`.
- Zip: for every `ZipEntry`, resolve, normalize, and check `startsWith(base)`, and count the bytes written.

## XML (CWE-611)
```java
DocumentBuilderFactory f = DocumentBuilderFactory.newInstance();
f.setFeature(XMLConstants.FEATURE_SECURE_PROCESSING, true);
f.setAttribute(XMLConstants.ACCESS_EXTERNAL_DTD, "");
f.setAttribute(XMLConstants.ACCESS_EXTERNAL_SCHEMA, "");
f.setXIncludeAware(false); f.setExpandEntityReferences(false);
```
Also set the Xerces feature `disallow-doctype-decl` (Apache xml features namespace) to `true`. Do the same for SAXParserFactory, XMLInputFactory (`IS_SUPPORTING_EXTERNAL_ENTITIES` = false, `SUPPORT_DTD` = false), and TransformerFactory.

## Deserialization (CWE-502)
- No `ObjectInputStream.readObject()` on untrusted data. If legacy code needs it: `ObjectInputFilter` with an allow-list.
- Jackson: no `enableDefaultTyping()` / `activateDefaultTyping` and no `@JsonTypeInfo(use = Id.CLASS)` on external input. Bind to concrete DTOs.
- XStream / SnakeYAML: `new Yaml(new SafeConstructor(new LoaderOptions()))`, and an XStream type allow-list.

## Spring Security and web
- **CSRF:** keep `csrf()` on for session or cookie auth. Disable it only for stateless token APIs.
- **Authorization:**
  - Use `@PreAuthorize` or `requestMatchers(...).hasRole(...)`, with `anyRequest().authenticated()` last.
  - Check object ownership in the service: `repo.findByIdAndOwnerId(id, currentUserId)`.
- **Mass assignment:** bind requests to DTOs, not entities. Otherwise use `@InitBinder` `setAllowedFields`. (CWE-915)
- **Passwords:** `BCryptPasswordEncoder` / `Argon2PasswordEncoder`. Never `MessageDigest` MD5/SHA for passwords.
- **Sessions:** `sessionFixation().migrateSession()` (the default), plus `server.servlet.session.cookie.http-only=true` / `secure=true` / `same-site=strict|lax`.
- **CORS:** `allowedOrigins` lists exact hosts. Never `"*"` with `allowCredentials(true)`.
- **Redirects:** `"redirect:" + url` only after an allow-list check, or for relative paths.
- **Actuator:** `management.endpoints.web.exposure.include=health,info` in production. Disable Swagger, the H2 console, and DevTools in the production profile.
- **Errors:** `server.error.include-stacktrace=never` and `include-message=never`. `@ControllerAdvice` returns a generic body.

## Crypto and randomness
- `SecureRandom` for tokens. Never `java.util.Random` or `Math.random()`. (CWE-330)
- `Cipher.getInstance("AES/GCM/NoPadding")` with a fresh 12-byte IV from SecureRandom for every message. Never `"AES"` alone (defaults to ECB) or `"DES"`. (CWE-327)
- TLS: no trust-all `X509TrustManager`, and no `HostnameVerifier` that returns `true`. Load the internal CA into a truststore. (CWE-295)

## Logging (CWE-117, CWE-532)
- Use parameterized SLF4J: `log.info("user={}", id)`. Logback/Log4j2 can strip CR/LF with a pattern replace (`%replace(%msg){'[\r\n]',''}`).
- Lombok: add `@ToString.Exclude` on secret fields, or avoid `@Data` on DTOs with card, password, or resident-number fields.

## Money and numbers
- `BigDecimal` with an explicit `RoundingMode` for amounts. Use `Math.addExact` / `multiplyExact` where overflow matters. (CWE-190)
