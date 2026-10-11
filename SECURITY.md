# Security Policy

## Supported Versions

Security updates are provided for the latest release on the `main` branch.

| Version | Supported          |
| ------- | ------------------ |
| 1.1.x   | :white_check_mark: |
| < 1.1.0 | :x:                |

---

## Reporting a Vulnerability

The Windows Dynamic Bar project takes the privacy and security of its users seriously.

If you believe you have discovered a security vulnerability or sensitive privacy issue:

1. **Do not disclose the issue publicly.** Please do not open a public GitHub issue.
2. **Private Disclosure:** Report the vulnerability via GitHub Security Advisories or by contacting the project maintainer privately via GitHub profile contact information.
3. Include:
   - A clear description of the vulnerability.
   - Steps to reproduce or proof-of-concept code.
   - Potential impact and affected Windows versions.
4. You will receive an acknowledgment within 48–72 hours.
5. Once a fix is verified, a patch and advisory will be published.

---

## Local Security & Privacy Architecture

Windows Dynamic Bar is built with a **local-only** security model:
- **Zero Remote Telemetry:** The app never collects, stores, or transmits your activity, keystrokes, clipboard content, or network data to any remote server.
- **Clipboard Encryption:** Saved clipboard history is encrypted at rest using Windows DPAPI (`DataProtectionScope.CurrentUser`).
- **No Elevation Needed:** The app runs entirely in standard user space without requiring administrator privileges.
