---
name: man-page-lookup
description: Use before running or writing any command, config file, or flag you are not certain about — read the locally installed documentation (man pages, --help, info, /usr/share/doc, package docs) instead of guessing or searching the internet. Essential on offline or air-gapped hosts.
license: MATE Builder original — no third-party content
---

# Local documentation lookup (man pages first)

On an internal network there is no web search. The installed documentation matches the installed version exactly, so it is better than memory anyway. Never invent a flag: look it up, then use it.

## 1. Pick the right source
| Need | Command |
|---|---|
| Command options | `man -P cat <cmd> \| col -b` (plain text, no pager) |
| Quick option list | `<cmd> --help 2>&1 \| head -100` (some tools use `-h` or `help`) |
| Config file format | `man 5 <name>` — e.g. `man 5 crontab`, `man 5 sshd_config`, `man 5 systemd.unit` |
| System call / C API | `man 2 <name>`, `man 3 <name>` |
| Admin commands | `man 8 <name>` — e.g. `man 8 iptables`, `man 8 lvcreate` |
| Shell builtins (cd, export, ulimit, set) | `help <builtin>` in bash; `man bash` section SHELL BUILTIN COMMANDS |
| GNU tools full manual | `info <name> --output=- 2>/dev/null \| head -300` (coreutils, sed, grep, tar) |
| Find which page covers a topic | `man -k <keyword>` or `apropos <keyword>`; exact name `whatis <name>` |
| Where a page lives | `man -w <cmd>` |
| Package docs, examples, changelogs | `ls /usr/share/doc/<pkg>/`; RPM: `rpm -qd <pkg>`; Debian: `dpkg -L <pkg> \| grep -E 'doc\|man'` |
| Which package owns a binary | RPM: `rpm -qf $(command -v <cmd>)`; Debian: `dpkg -S $(command -v <cmd>)` |
| Installed version | `<cmd> --version`, `rpm -q <pkg>`, `dpkg -l <pkg>` |
| Language libraries | Python `python3 -m pydoc <module>`; Java `javap -public <class>` or the JDK `src.zip`; Go `go doc <pkg>.<Sym>`; Node: read the package's `README`/`*.d.ts` under `node_modules/` |

## 2. Read efficiently
- A man page can be thousands of lines. Do not dump it whole into the conversation. Pipe through `col -b` and then narrow it:
  - Sections: `man -P cat <cmd> | col -b | grep -n '^[A-Z][A-Z ]*$'` lists headings with line numbers, then `sed -n 'START,ENDp'`.
  - One option: `man -P cat <cmd> | col -b | grep -n -A6 -E '^\s+-x\b|--long-name'`.
  - Examples: most pages have an EXAMPLES section — read it first when you need usage.
- If the output is still large, save it to a temp file and use Grep/Read on that file.
- Compressed pages can be read directly: `zcat /usr/share/man/man1/<cmd>.1.gz | groff -man -Tutf8 2>/dev/null | col -b` (or `man -l <file>`).

## 3. When there is no man page
Minimal images often strip docs: for example, RHEL/UBI containers built with `tsflags=nodocs`, or slim Debian images with the `/usr/share/man` path excluded.
1. Try `--help`, `-h`, `help`, or running the command with no arguments.
2. Check `/usr/share/doc/<pkg>/`, `/usr/share/<pkg>/`, `/etc/<pkg>/` (sample configs are often fully commented), and `/opt/<product>/doc`.
3. Ask whether an internal docs mirror, wiki export, or vendor manual exists on a file share, and read it with Read.
4. Say clearly that you could not find documentation for the installed version. Then either stop, or mark any flag you take from memory as unverified and test it safely first (see below).

## 4. Verify before acting
- Check the installed version against what the page describes. Options differ between versions and between GNU, BSD, and BusyBox.
- Before a destructive command, find a dry-run or no-op mode in the docs and use it first, for example `--dry-run`, `-n`, `--check`, `--diff`, `--test`, or `--what-if`.
- Quote the exact option text you relied on, with its section, when you explain a command to the user.

## 5. Report
Report three things: the source you used (for example "man 5 sshd_config, installed openssh 8.7"), the command or config you derived from it, and anything you could not confirm locally.
