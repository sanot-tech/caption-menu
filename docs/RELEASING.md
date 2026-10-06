# Release process — CaptionMenu

## One-shot

```powershell
# 1. Clean build + full test (must pass: exit code 0)
dotnet publish src\CaptionMenu\CaptionMenu.csproj -c Release -r win-x64 -o out
pwsh -File .\Test-CaptionMenu.ps1

# 2. Tag + push
git tag -a v1.0.0 -m "Right-click any title bar to pin it on top"
git push origin v1.0.0
```

GitHub Actions (`.github/workflows/release.yml`) picks up the tag and uploads
`CaptionMenu.exe` as a release asset.

## Version bump

`Version` in `src/CaptionMenu/CaptionMenu.csproj`:

```xml
<Version>1.0.0</Version>
```

SemVer, because the hook/menu internals are the risky part — a minor bump means
"new menu items", a patch means "bug fix in an existing behaviour".

## Pre-release checklist

- [ ] `Test-CaptionMenu.ps1` exits `0` on the machine you release from
- [ ] `README.md` matches actual behaviour (menu items, flags, requirements)
- [ ] Icon generated and `ApplicationIcon` uncommented in the `.csproj`
- [ ] Tested at 100% **and** 150% DPI
- [ ] Tested on a fresh machine without a .NET runtime installed
