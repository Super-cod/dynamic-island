# Contributing to Windows Dynamic Bar

Thank you for your interest in contributing to **Windows Dynamic Bar**!

Windows Dynamic Bar is an independent, source-available, non-commercial software project created and maintained by **Supercell**.

---

## 1. Non-Commercial Licence Terms

Please review [LICENSE.md](LICENSE.md) before contributing.

- This software is distributed under a **source-available, non-commercial licence**.
- Commercial use, distribution for pay, and sale of the software are strictly prohibited.
- By submitting a pull request, you agree that your contributions will be distributed under the project's non-commercial licence terms. You retain copyright in your contributions.

---

## 2. Ways to Contribute

- **Report Bugs:** Open a detailed issue using our [Bug Report template](.github/ISSUE_TEMPLATE/bug_report.yml).
- **Suggest Features:** Share workflow and UX ideas via the [Feature Request template](.github/ISSUE_TEMPLATE/feature_request.yml).
- **Submit Pull Requests:** Bug fixes, performance improvements, and documentation polish are welcome.

---

## 3. Development Setup

### Prerequisites

- **Windows 10 (version 2004 / build 19041 or higher)** or **Windows 11**
- **.NET 8.0 SDK** (v8.0.400 or later)
- Visual Studio 2022 (Community or higher) with the **.NET desktop development** workload, or VS Code / JetBrains Rider.

### Build and Test

Clone the repository and build from PowerShell:

```powershell
# Restore dependencies
dotnet restore -m:1

# Build Release
dotnet build WindowsDynamicBar.sln -c Release

# Run automated test suite
dotnet run --project tests\WindowsDynamicBar.Tests\WindowsDynamicBar.Tests.csproj -c Release
```

All 80+ tests must pass before opening a pull request.

---

## 4. Coding Conventions

- **Keep changes focused:** Avoid large unsolicited rewrites or introducing unnecessary external NuGet packages.
- **Respect privacy:** Windows Dynamic Bar is strictly **local-only**. Never add remote telemetry, analytics, tracking, or network calls that transmit user data.
- **Performance:** Keep CPU and RAM usage minimal. Telemetry polling must remain throttled and background resources properly disposed.
- **UI & Accessibility:** Adhere to Windows 11 Fluent design principles and dark/light system theme support.

---

## 5. Submitting Pull Requests

1. Fork the repository and create your feature branch (`git checkout -b fix/my-fix`).
2. Verify that `dotnet build` succeeds and all tests pass (`dotnet run --project tests\WindowsDynamicBar.Tests -c Release`).
3. Commit with clear, descriptive commit messages.
4. Push to your fork and submit a Pull Request describing your changes.
