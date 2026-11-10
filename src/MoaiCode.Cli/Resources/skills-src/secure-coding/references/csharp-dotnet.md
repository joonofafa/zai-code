# C#, .NET: unsafe and safe pairs

## SQL (CWE-89)
- Unsafe: `new SqlCommand("... WHERE id = '" + id + "'")`, `FromSqlRaw($"... {id}")`, `ExecuteSqlRaw` with an interpolated string.
- Safe:
  - `cmd.Parameters.AddWithValue("@id", id)`.
  - EF Core `FromSql($"... {id}")` / `FromSqlInterpolated` / `ExecuteSql` (interpolation is turned into parameters).
  - Dapper: `conn.Query<T>("... WHERE id = @id", new { id })`.

## Commands (CWE-78)
- Unsafe: `Process.Start("cmd.exe", "/c dir " + input)`, `bash -c` with input.
- Safe: `new ProcessStartInfo("tool") { ArgumentList = { "--", input }, UseShellExecute = false }`. Allow-list the program.

## Deserialization (CWE-502)
- Never `BinaryFormatter`, `NetDataContractSerializer`, `SoapFormatter`, `LosFormatter`, or `ObjectStateFormatter`.
- Json.NET: `TypeNameHandling.None` (the default). Never `All` / `Auto` / `Objects` on external input. `System.Text.Json` with concrete types is safe.

## XML (CWE-611)
- `XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }`. For `XmlDocument`, set `XmlResolver = null`.

## Paths and files (CWE-22)
```csharp
var baseDir = Path.GetFullPath("/data/uploads") + Path.DirectorySeparatorChar;
var full = Path.GetFullPath(Path.Combine(baseDir, name));
if (!full.StartsWith(baseDir, StringComparison.Ordinal)) throw new ArgumentException("bad path");
```
- `Path.Combine` drops the base when the second argument is rooted. Always check the result as above.
- `ZipFile.ExtractToDirectory` checks for zip slip in .NET Core 2.0+, but still limit the total size. For manual `ZipArchiveEntry` loops, check every entry path.
- Uploads (`IFormFile`): check `Length`, and check the extension against an allow-list. Store under a generated name outside `wwwroot`.
- Temp files: `Path.GetTempFileName()` or a random name in a private folder.

## ASP.NET Core
- **XSS:** Razor `@value` encodes. `Html.Raw` only after a sanitizer. In JavaScript contexts, use `JavaScriptEncoder`. (CWE-79)
- **CSRF:**
  - `[ValidateAntiForgeryToken]` / `AutoValidateAntiforgeryTokenAttribute` for cookie-auth forms.
  - Minimal APIs with cookie auth: use antiforgery middleware.
- **Authorization:**
  - Add a `[Authorize]` fallback policy (`options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()`), and add `[AllowAnonymous]` explicitly where needed.
  - Check resource ownership with `IAuthorizationService` or in the query. (CWE-862, CWE-639)
- **Over-posting:** bind to input DTOs, not EF entities. (CWE-915)
- **Redirects:** `LocalRedirect(url)` or `Url.IsLocalUrl(url)`. (CWE-601)
- **Cookies:** `CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax }`.
- **CORS:** `WithOrigins(allowedOrigins)` with exact origins from config. Never `AllowAnyOrigin` with `AllowCredentials`.
- **Errors:** `UseExceptionHandler` / ProblemDetails in production. `UseDeveloperExceptionPage` only in Development. (CWE-209)
- **Sizes:** set `[RequestSizeLimit]` / `FormOptions.MultipartBodyLengthLimit`.
- **Regex:** pass a `matchTimeout`, or use `RegexOptions.NonBacktracking` for input. (CWE-1333)

## Crypto and auth
- Randomness: `RandomNumberGenerator.GetBytes(32)` / `GetInt32`. Never `System.Random` for tokens. (CWE-330)
- Passwords: `PasswordHasher<T>` (PBKDF2), or `Rfc2898DeriveBytes.Pbkdf2` with SHA-256 and high iterations.
- Encryption: `AesGcm` with a fresh 12-byte nonce. Never `Aes` in ECB mode, or `DESCryptoServiceProvider`. (CWE-327)
- Constant-time compare: `CryptographicOperations.FixedTimeEquals`.
- JWT: `TokenValidationParameters` with `ValidateIssuer` / `ValidateAudience` / `ValidateLifetime` / `ValidateIssuerSigningKey` all `true`, and `ValidAlgorithms` fixed.
- TLS: never `ServerCertificateCustomValidationCallback = (_, _, _, _) => true` or `DangerousAcceptAnyServerCertificateValidator`. Install the internal CA into the trust store. (CWE-295)

## Secrets and logging
- No secrets in `appsettings.json` in source control. Use user-secrets (development), environment variables, or the platform secret store. (CWE-798)
- Use structured logging: `logger.LogInformation("user {UserId}", id)`. Never log tokens, passwords, or full request bodies with personal data. (CWE-532)

## Numbers
- `decimal` for money. Use `checked` arithmetic where overflow matters. (CWE-190)
