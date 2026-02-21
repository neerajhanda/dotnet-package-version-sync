# dotnet-package-version-sync

`dotnet-package-version-sync` is a .NET 10 CLI that scans a solution and reconciles NuGet package versions across projects.

## What it does

By default, it does **approach 1**:
- Finds packages referenced by 2+ projects.
- Picks a preferred version (highest compatible explicit version).
- Updates project files so common packages use the same version.

Optionally, it does **approach 2** with `--centralize`:
- Creates or updates `Directory.Packages.props`.
- Writes package versions as `<PackageVersion />` entries.
- Removes project-level `Version` values from `<PackageReference />`.

## Usage

```powershell
dotnet run --project DotNetPackageVersionSync -- --solution path\to\MySolution.slnx
```

### Options

- `-s, --solution <path>`: Path to a `.sln`/`.slnx` file, or directory containing one.
- `-c, --centralize`: Enable Central Package Management (`Directory.Packages.props`).
- `-n, --dry-run`: Print planned changes without editing files.
- `-h, --help`: Show help.

## Examples

Align common package versions only:

```powershell
dotnet run --project DotNetPackageVersionSync -- --solution C:\repo\App.slnx
```

Preview centralization changes:

```powershell
dotnet run --project DotNetPackageVersionSync -- --solution C:\repo\App.slnx --centralize --dry-run
```

Apply central package management:

```powershell
dotnet run --project DotNetPackageVersionSync -- --solution C:\repo\App.slnx --centralize
```

## Notes

- Project types supported from the solution file: `.csproj`, `.fsproj`, `.vbproj`.
- If any project uses `packages.config`, the tool exits with an error and lists affected projects.
- If a package has incompatible or non-comparable version specs across projects, the tool reports it and skips it.
- In `--centralize` mode, if a package shared by multiple projects cannot be reconciled, the run exits with an error.
