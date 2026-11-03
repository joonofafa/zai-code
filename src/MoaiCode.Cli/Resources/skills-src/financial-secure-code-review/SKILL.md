---
name: financial-secure-code-review
description: Use when reviewing or writing code, configs, logs, or SQL in a financial/card-payment environment for data-protection and security issues — card numbers (PAN), CVV, resident registration numbers, account numbers, credentials, logging of personal data, weak crypto, injection (including MyBatis ${}), disabled TLS checks, and any code that sends data outside the internal network.
license: MATE Builder original — no third-party content
---

# Financial secure code review

The context is a card company's internal systems. The standards are the payment-card data rules (PCI DSS), Korean financial security and privacy regulation (전자금융감독규정, 개인정보 보호법, 신용정보법), and the company's own security policy. When a company rule is stricter than this checklist, the company rule wins. Ask for it if it is not in the repository (`SECURITY.md`, coding standards, policy documents).

## Procedure
1. **Scope:** the changed files (`git diff --name-only <base>...HEAD`) or the paths the user names. Include configs (`*.yml`, `*.properties`, `*.xml`), SQL and mapper XML, scripts, Dockerfiles, and Kubernetes manifests.
2. **Pattern sweep:** run the searches in `references/patterns.md` with Grep over the scope. For card numbers, also run the Luhn checker so random digit strings do not flood the report:
   `python3 <skill-dir>/scripts/find_pan.py <paths...>` (prints file:line with the number masked).
3. **Read every hit in context.** Most hits are test fixtures, masked values, or constants. Classify each one: real issue / test data (still flag if it is a real-looking PAN that passes Luhn) / false positive.
4. **Data flow for sensitive fields:** for each DTO or entity with card, resident-number, account, or credential fields, trace where it is logged, serialized (`toString`, JSON, exception messages), cached, written to files, or sent out.
5. **Report:** must fix / should fix / note, each with `file:line`, the rule it breaks, the concrete risk, and a minimal fix. **Never print a full sensitive value in the report.** Mask it as first 6 + last 4 at most for PAN, and the birth date part only for resident numbers.

## Must-fix rules
- **CVV/CVC, PIN, PIN block, or full track/chip data** stored, logged, or cached after authorization. Never allowed.
- **Full PAN** in logs, exception messages, URLs or query strings, analytics events, error pages, or non-encrypted storage. Display masked (at most the first 6 and last 4 digits). Store PAN only encrypted or tokenized in approved stores.
- **Resident registration numbers (주민등록번호)** stored or logged without encryption, or collected without a legal basis.
- **Credentials in code or config:** passwords, API keys, private keys, tokens, JDBC URLs with passwords. Use the platform secret mechanism (Kubernetes Secret, vault, encrypted config).
- **SQL built by string concatenation**, including MyBatis `${...}` with user-controlled input. Use `#{...}` binding. `${}` is acceptable only for whitelisted identifiers such as sort columns.
- **TLS verification disabled:** trust-all `TrustManager`, `HostnameVerifier` returning true, `verify=False`, `InsecureSkipVerify: true`, `curl -k`, `NODE_TLS_REJECT_UNAUTHORIZED=0`.
- **Weak crypto for protection:** MD5/SHA-1 for passwords or integrity, DES/3DES/RC4, AES-ECB, static or zero IVs, hard-coded keys, `java.util.Random`/`Math.random` for tokens or OTPs. Use SecureRandom and approved algorithms (AES-GCM, SHA-256+, bcrypt/scrypt/PBKDF2/Argon2 for passwords). Korean standards such as SEED/ARIA may be mandated by policy.
- **Data leaving the internal network:** HTTP clients, SDKs, telemetry, CDN `<script>` tags, or webhooks pointing to external hosts in code that runs on the internal network. This breaks the network-separation (망분리) rules even if it "only sends metrics".

## Should-fix
- Sensitive fields reachable through `toString()`: Lombok `@Data`/`@ToString` without `@ToString.Exclude`, Kotlin data classes, records. Also full request or response body logging in filters or interceptors (`log.info("req={}", body)`).
- Exception handlers that return stack traces or SQL errors to clients.
- Debug or trace logging enabled in production configs (`logging.level.root=DEBUG`, SQL logging that includes bind values).
- Personal data in test fixtures that looks real (a Luhn-valid PAN or a real-format resident number). Use clearly fake values.
- Missing authorization checks on endpoints that return customer data. Check for IDOR: the customer id comes from the request without an ownership check.
- Deserialization of untrusted input (Java native serialization, unsafe YAML loaders, polymorphic Jackson typing).
- File paths built from input without normalization (path traversal). XML parsers without XXE protection.

## Do not
- Do not copy sensitive values out of the system into the conversation, temp files, or commits. Work with masked values.
- Do not "fix" by deleting audit logging. Mask the fields instead.
