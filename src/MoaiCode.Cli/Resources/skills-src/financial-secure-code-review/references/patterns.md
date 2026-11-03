# Search patterns (use with Grep, case-insensitive where noted)

Expect false positives: read every hit before reporting it.
The patterns use .NET/PCRE syntax (lookaheads allowed). They work with the Grep tool as is; on the shell use `grep -P`, not `grep -E`.

## Personal and card data
| What | Regex |
|---|---|
| Card number candidates (13–19 digits, optional space or dash) | `\b\d{4}[- ]?\d{4}[- ]?\d{4}[- ]?\d{1,7}\b` — then confirm with `scripts/find_pan.py` (Luhn check) |
| CVV-like fields | (case-insensitive) `\b(cvv|cvc|cvv2|cvc2|cav2|cid|securityCode|card_?verif)` |
| PIN / track data fields | (case-insensitive) `\b(pin_?block|pinBlock|track_?[12]|trackData|emv_?data)\b` |
| Resident registration number (주민등록번호) | `\b\d{2}(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])-?[1-8]\d{6}\b` |
| Resident-number field names | (case-insensitive) `(jumin|rrn|resident_?no|ssn|주민)` |
| Mobile phone (KR) | `\b01[016789]-?\d{3,4}-?\d{4}\b` |
| Account number fields | (case-insensitive) `(acct_?no|account_?(no|num)|계좌)` |
| Card number fields | (case-insensitive) `(card_?no|cardNum|card_?number|pan\b|카드번호)` |

## Logging sensitive data
| What | Regex |
|---|---|
| Logging a sensitive field | (case-insensitive) `log(ger)?\.(trace|debug|info|warn|error)\(.*(card|pan|cvv|jumin|rrn|ssn|password|passwd|pwd|secret|token|acct)` |
| Printing whole objects or bodies | `log(ger)?\.\w+\(.*(toString\(\)|getBody\(\)|request\b|response\b|body\b)` |
| System.out / print debugging | `System\.(out|err)\.print|printStackTrace\(\)|console\.log\(|print\(` |
| Lombok toString on DTOs | `@Data\b|@ToString\b` (then check the class for sensitive fields without `@ToString.Exclude`) |
| SQL bind logging | (case-insensitive) `(log4jdbc|p6spy|show_sql\s*[:=]\s*true|org\.hibernate\.(SQL|type)\s*[:=]\s*(debug|trace))` |

## Credentials
| What | Regex |
|---|---|
| Password assignments | (case-insensitive) `(password|passwd|pwd|secret|api_?key|access_?key|token)\s*[:=]\s*["']?[^\s"'$<{][^\s"']{3,}` |
| JDBC URL with password | (case-insensitive) `jdbc:[a-z]+://[^\s"']*(password|pwd)=` |
| Private keys | `-----BEGIN ([A-Z]+ )?PRIVATE KEY-----` |
| Bearer / basic auth literals | (case-insensitive) `(authorization:\s*(bearer|basic)\s+[a-z0-9._\-+/=]{10,})` |
| Cloud key formats | `AKIA[0-9A-Z]{16}` (AWS), `AIza[0-9A-Za-z_\-]{35}` (Google) |

## Injection
| What | Regex |
|---|---|
| MyBatis `${}` in mapper XML or annotations | `\$\{[^}]+\}` in `*Mapper.xml` / `@Select(...)` |
| SQL built by concatenation (Java/Kotlin) | (case-insensitive) `"\s*(select|insert|update|delete)\b[^"]*"\s*\+` |
| JDBC Statement (not Prepared) | `createStatement\(\)|\.execute(Query|Update)?\(\s*[a-zA-Z_]+\s*\+` |
| Shell command from input | `Runtime\.getRuntime\(\)\.exec\(|ProcessBuilder\(|subprocess\.(call|run|Popen)\(.*shell\s*=\s*True|os\.system\(` |
| Unsafe deserialization | `ObjectInputStream|readObject\(\)|enableDefaultTyping|activateDefaultTyping|yaml\.load\((?!.*SafeLoader)|pickle\.loads?\(` |
| XXE-prone parsers | `DocumentBuilderFactory\.newInstance\(\)|SAXParserFactory\.newInstance\(\)|XMLInputFactory\.newInstance\(\)` (then check for the disallow-doctype feature) |

## Transport and crypto
| What | Regex |
|---|---|
| TLS verification off | (case-insensitive) `(TrustAllCerts|X509TrustManager|checkServerTrusted\s*\([^)]*\)\s*\{\s*\}|HostnameVerifier|ALLOW_ALL_HOSTNAME|verify\s*=\s*False|InsecureSkipVerify\s*:\s*true|curl\s+(-k|--insecure)|NODE_TLS_REJECT_UNAUTHORIZED)` |
| Plain HTTP endpoints | `http://(?!localhost|127\.0\.0\.1|0\.0\.0\.0)` (then check whether internal policy allows them) |
| Weak hashes and ciphers | (case-insensitive) `(MD5|SHA-?1\b|DESede|\bDES\b|RC4|/ECB/|AES/ECB|NoPadding)` |
| Insecure randomness | `new Random\(|Math\.random\(|java\.util\.Random|random\.random\(|\brand\(\)` (fine for non-security use; flag for tokens, OTPs, IDs) |
| Static IV / key literals | (case-insensitive) `(iv|key|secret)\s*=\s*(new byte\[\]\s*\{|"[0-9a-f]{16,}"|'[0-9a-f]{16,}')` |

## External transmission (network separation)
| What | Regex |
|---|---|
| Absolute URLs to public hosts | `https?://(?!([a-z0-9-]+\.)*(internal|local|corp|svc|cluster\.local)\b)[a-z0-9.-]+\.[a-z]{2,}` (adjust to the company's internal domains) |
| Telemetry / analytics SDKs | (case-insensitive) `(sentry|datadog|newrelic|google-analytics|gtag\(|segment\.io|mixpanel|amplitude|firebase)` |
| CDN scripts in web pages | `<script[^>]+src=["']https?://` |
| Package downloads at build or run time | (case-insensitive) `(curl|wget)\s+[^|]*https?://|pip\s+install|npm\s+(install|i)\b|go\s+get\b` (on an air-gapped network these must point at the internal mirror) |
