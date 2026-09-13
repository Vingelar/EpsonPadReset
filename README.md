**Developed with [Cursor AI](https://cursor.com)**

[![Developed with Cursor AI](https://img.shields.io/badge/developed%20with-Cursor%20AI-000000)](https://cursor.com)
# EpsonPadReset

Windows console tool (C# / .NET 8) that **reads and resets Epson EcoTank waste-ink counters** over USB, with an automatic EEPROM backup before any write.

Verified on **Epson ET-2820** (USB `vid_04b8`, key group `0x364A`).

> **This does not empty the absorbent pads.** It only clears the printer’s internal estimate of how full those pads are. If the foam is truly saturated, ink can still leak after a reset. Replace the pads or fit an external waste tank when you can.

---

## Credits and prior art

This project stands on the shoulders of:

- **[PadZero](https://github.com/Damnitbran/padzero/tree/main)** — free/open waste-counter reset tool for Windows. Its `usb_direct.py`, `padzero.py`, and `models.json` were the primary cross-reference for USBPRINT access, reset maps, and safety rails (backup before write, dry-run by default).
- **[reinkpy](https://codeberg.org/atufi/reinkpy)** — IEEE 1284.4 (D4) stack and `epson.toml` model database (read/write keys, memory layouts).
- Earlier lineage: [ReInk](https://github.com/lion-simba/reink), [epson_print_conf](https://github.com/Ircama/epson_print_conf).

Not affiliated with Seiko Epson Corporation.

---

## What problem this solves

Epson printers keep a **software counter** (no liquid sensor) that estimates waste ink sent into internal pads during cleaning, charging, power-on, and borderless printing. When the estimate crosses a threshold, the printer stops with messages such as:

- “Ink pad is at the end of its service life”
- “Service required” / error **E11**
- Similar maintenance / waste-ink warnings

Epson’s official path is often a service visit. Tools like PadZero and this program reset the counter in the printer EEPROM so printing can resume.

---

## How the software works

### 1. Find the printer (Windows USBPRINT)

On Windows, stock Epson drivers expose a bidirectional device interface under:

`GUID_DEVINTERFACE_USBPRINT` `{28D78FAD-5A12-11D1-AE5B-0000F803A8C2}`

EpsonPadReset enumerates those paths with SetupAPI and opens one with `CreateFileW` using:

- `GENERIC_READ | GENERIC_WRITE`
- `FILE_SHARE_READ | FILE_SHARE_WRITE`
- **no** `FILE_FLAG_OVERLAPPED` (overlapped I/O on `usbprint.sys` often returns empty reads on this stack)

That matches PadZero’s `usb_direct.py` approach: talk through the installed driver — no Zadig, no WinUSB swap, no admin required for the open itself.

Wi-Fi / SNMP is **not** used. Epson blocks `||` factory EEPROM commands on the network control path (`:NA;`). USB is required while resetting.

### 2. Identify the device

Optionally (verbose mode), the tool issues `IOCTL_USBPRINT_GET_1284_ID` and prints the IEEE 1284 ID string, for example:

```text
MFG:EPSON;CMD:...D4,END4...;MDL:ET-2820 Series;...
```

That confirms model family and that **D4 / END4** control channels are advertised.

### 3. Enter IEEE 1284.4 (D4) and open EPSON-CTRL

Modern EcoTank models (including ET-2820) speak Epson’s control traffic inside **IEEE 1284.4** packets (as implemented in reinkpy / used by PadZero via reinkpy).

Rough sequence:

1. Send the EJL enter-D4 banner (`@EJL 1284.4` …).
2. Expect an enter acknowledgement (e.g. `00 00 00 08 01 00 C5 00`).
3. **Init** on the transaction channel `(0,0)`.
4. If the peer replies “retry with another revision”, switch to that revision.  
   **ET-2820 negotiates revision `0x10`** (not `0x20`). OpenChannel / CreditRequest layouts differ between revisions.
5. **OpenChannel** for service socket `(0x02,0x02)` — Epson’s `EPSON-CTRL` channel.
6. **CreditRequest** until the CTRL channel has send credits.  
   Important: OpenChannel may report `grantedCredit = 0`. The tool must not invent a fake credit; without a real credit the EEPROM command is ignored and you get no `EE:` reply.

### 4. Read / write EEPROM with `||` factory commands

On the CTRL channel the tool sends Epson “factory” frames:

| Op | Shape | Meaning |
|----|--------|---------|
| Read | `\|\|` + length + header(`rkey`, `'A'`, …) + address | Reply contains `EE:AAAAvv;` |
| Write | `\|\|` + length + header(`rkey`, `'B'`, …) + address + value + write-key | Reply contains `:OK;` |

For the ET-2820 family (same group as ET-2800 in PadZero / reinkpy):

| Parameter | Value |
|-----------|--------|
| Read key (`rkey`) | `0x364A` |
| Write key (plain) | `Maribaya` (Caesar-shifted by +1 when sent, → `Nbsjcbzb`) |

`models.json` holds the **reset map**: which EEPROM addresses to set to which bytes (counters → `0`, maintenance-level markers → model-specific values such as `94`).

### 5. Safety rails (inspired by PadZero)

| Step | Behavior |
|------|----------|
| Default | **Dry-run**: `--reset` without `--yes` only prints the plan |
| Backup | Before a real write, dump EEPROM `0..255` to `dumps\*.json` |
| Verify | Each write is read back and checked |
| Scope | Only addresses listed in `raw_waste_reset` for the selected model |

After a successful reset, **power-cycle the printer** with its power button so the firmware reloads the counters.

### 6. Fallback transport

The tool also implements PadZero’s simpler **ESC/REMOTE** wrapper (no D4). On ET-2820 that path returns no data; D4 is tried first. Other models may prefer ESC/REMOTE.

---

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download) to build (or a self-contained publish)
- Official **Epson driver** installed (generic IPP-only drivers are not enough)
- USB cable in the printer’s **USB** port (not LINE/EXT fax jacks)
- Empty print queue recommended while resetting

---

## Build

```bat
cd EpsonPadReset
dotnet build -c Release
```

Output:

```text
bin\Release\net8.0-windows\EpsonPadReset.exe
```

Self-contained single-file (optional):

```bat
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

---

## Usage

```bat
EpsonPadReset.exe -l
EpsonPadReset.exe -i -m ET-2820 -v
EpsonPadReset.exe --dump -m ET-2820
EpsonPadReset.exe --reset -m ET-2820 -v
EpsonPadReset.exe --reset --yes -m ET-2820 -v
```

| Switch | Meaning |
|--------|---------|
| `-l` / `--list` | List USBPRINT device paths |
| `-i` / `--info` | Show waste counters (default if no other action) |
| `--dump` | Save EEPROM `0..255` JSON under `dumps\` |
| `--reset` | Show reset plan (dry-run unless `--yes`) |
| `--yes` | Actually write EEPROM |
| `-d N` | Device index from `-l` (default `0`) |
| `-m NAME` | Model key in `models.json` (default `ET-2820`) |
| `-v` | Verbose protocol tracing |

Typical workflow:

1. `EpsonPadReset.exe -i -m ET-2820 -v` — confirm connection and percentages.
2. `EpsonPadReset.exe --reset -m ET-2820 -v` — review which addresses would change.
3. `EpsonPadReset.exe --reset --yes -m ET-2820 -v` — backup + write.
4. Power-cycle the printer.

Backups land next to the EXE as:

```text
dumps\ET-2820_<timestamp>_pre-reset.json
```

---

## Project layout

| File | Role |
|------|------|
| `Program.cs` | CLI, backup orchestration, counter display |
| `UsbPrint.cs` | SetupAPI enumeration, sync USBPRINT I/O, 1284-ID IOCTL |
| `EpsonProtocol.cs` | D4 session, ESC/REMOTE fallback, EEPROM encode/decode, `models.json` binding |
| `models.json` | Per-model reset maps / dividers (ET-28xx family; ET-2820 based on ET-2800 / PadZero coverage) |

---

## Adding another model

1. Prefer data from [PadZero `models.json`](https://github.com/Damnitbran/padzero/blob/main/models.json) / reinkpy `epson.toml`.
2. Confirm the key group (`rkey` / write key) — similar model names can use **different** keys.
3. Add a `models.json` entry with `raw_waste_reset`, `read_key`, `write_key`, and optional `waste` dividers.
4. Test with `-i` and `--reset` (dry-run) before `--yes`.

If coverage is unknown, use `--dump` and compare with PadZero’s guidance for contributing dumps.

---

## Disclaimer

- Writing EEPROM can brick alignment or identity data if the wrong map/keys are used. Stick to known models.
- Resetting may affect warranty terms.
- You are responsible for your hardware and for disposing of waste ink safely.
- Provided as-is, without warranty of any kind.

---

## License

Protocol knowledge and reset maps are derived from AGPL-licensed projects ([PadZero](https://github.com/Damnitbran/padzero/tree/main), reinkpy). This repository is released under **AGPL-3.0-or-later** for compatibility. See `LICENSE`.
